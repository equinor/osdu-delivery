---
id: delivery-flow-large-files
title: "Large files (uploadBlockMiB): a file above one block goes to Azure in blocks, copied by the store when it may read the file"
type: flow-reference
summary: "How a 16 GB LAS file reaches Azure: in blocks and a commit, copied between accounts by the store, or sent from the node when the store may not read it."
keywords:
  - large file
  - 16 GB LAS file
  - big file upload
  - file over 5 GB
  - RequestBodyTooLarge
  - block blob
  - Put Block
  - Put Block From URL
  - server side copy
  - copy between storage accounts
  - azcopy
  - SAS token
  - signed URL expired
  - CannotVerifyCopySource
  - uploadBlockMiB
  - uploadConcurrency
  - uploadServerCopy
yamlPath: "target.protocolOptions.uploadBlockMiB, target.protocolOptions.uploadConcurrency, target.protocolOptions.uploadServerCopy (flowType: delivery)"
related:
  - delivery-flow-routes
  - delivery-flow-enrich-records
  - delivery-concept-availability-and-retention
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/SignedUploads.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/BlockBlob.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/UploadParts.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/DatasetUploads.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/DatasetCollections.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/FileUploads.cs
  - osdu/src/SqlFlow.Delivery/Storage/FileStore.cs
  - osdu/src/SqlFlow.Delivery/Model/FlowDefinition.cs
  - osdu/docs/census/keys.delivery.json
  - osdu/specs/core/INTEGRATION.md
---

# Large files (uploadBlockMiB): a file above one block goes to Azure in blocks, copied by the store when it may read the file

OSDU on Azure hands out a **SAS**: a URL that carries its own permission to write one blob. The bytes never pass through an
OSDU service. One `PUT` to a SAS holds at most 5000 MiB, so a 16 GB LAS file cannot go in one request whatever the network
does. It goes the way `azcopy` sends it, and the way Microsoft's guidance for Azure Data Manager for Energy has it sent: cut
into blocks, staged one by one, then committed as one blob.

A flow needs no setting for this. Every route that uploads a file takes the same path: `file`, `dataset`, `manifest`,
`workflow`, the composed routes, and the [files an enrichment adds](enrich-records.md).

## How a file goes

| The file | What happens | `transfer` in the step |
| --- | --- | --- |
| No larger than one block | One `PUT`, as the service's own documentation shows it | `single` |
| Larger than one block, and it sits in Azure Storage the store may read | The store copies it from the file's own account, block by block. No byte of it passes through the node. | `serverCopy` |
| Larger than one block, otherwise | The node reads the file once, front to back, and sends the blocks | `blocks` |

Both ways of sending in blocks end in one commit, and the blob appears only then. Nothing partial is ever registered: an
upload that fails leaves blocks that Azure discards by itself.

## The keys

```yaml
target:
  protocol: dataset
  protocolOptions:
    uploadBlockMiB: 32
    uploadConcurrency: 4
    uploadServerCopy: true
```

| Key | Default | Meaning |
| --- | --- | --- |
| `uploadBlockMiB` | 32 | The size of a block, 1 to 1024. A file no larger goes in one request. |
| `uploadConcurrency` | 4 | The blocks of one file sent at once, 1 to 32. A file sent from the node holds this many blocks in memory; a copy holds none. |
| `uploadServerCopy` | true | Whether a file in Azure Storage is copied by the store. `false` always sends it from the node. |

A file larger than 50,000 blocks hold, which at 32 MiB is about 1.5 TiB, goes in larger blocks, since that is the most one
blob takes.

## Trying it on a small file

Set `uploadBlockMiB: 1`, and a file of a few MiB takes every step a 16 GB file takes: nothing else about the path depends on
the size. The record's steps show `transfer` and `blocks`, and the file lands whole.

## The two signatures

A copy between accounts takes two, and neither is ever logged, stored or returned:

- **Where the file goes.** The Dataset or File service signs it for one staging blob, with create and write and nothing else.
- **Where it comes from.** The node signs a read of that one file with its own identity (a user delegation SAS), for as long as
  the first signature lasts. It needs no key, and the node needs nothing more than the role that lets it read the file. Unlike a
  bearer token, it does not need the two accounts to share a tenant.

## When the store may not read the file

The first block goes alone, and a refusal costs one request. The file is then sent from the node, with no change to the flow,
and the step's `notCopied` says why:

| What stands in the way | What happens |
| --- | --- |
| The landing account's firewall refuses the store (`403 CannotVerifyCopySource`) | Sent from the node |
| Public network access is off, and the account is reached over a private endpoint | Sent from the node |
| The node could not sign a read (it reaches the account only over the Data Lake endpoint, or holds no role) | Sent from the node |
| A `file` or `manifest` route, and the file's store keeps no content MD5 | Sent from the node, which gives the blob its MD5 |
| The file sits on a local path | Sent from the node, with no remark |

An account behind a firewall still allows a copy when the node's own public address is on its list of allowed addresses: the
store is then let through as the node would be. To have a landing account copied rather than sent, add the address the nodes
leave from to that list.

The `file` and `manifest` routes register through the File service, which keeps the blob's content MD5 as the dataset's
checksum and, when a blob has none, reads all of it to compute one, within the registration's request. So a file whose store
holds no MD5 goes from the node on those routes, which gives it one. The `dataset` route computes no checksum, and copies either
way.

## A location lasts one hour

The Dataset service's locations last one hour, whatever is asked: it does not pass an expiry on to the File service
([the contract notes](../../../specs/core/INTEGRATION.md#25-dataset-service)). So 16 GiB sent from a node needs 37 Mbit/s or
more, sustained. A location cannot be extended, and a new one is another blob, so a file that outlasts its location fails with
`The signed location ran out at ...` and its next try starts over. Raise `uploadConcurrency`, or send from a node nearer the
storage. The `file` and `manifest` routes take `uploadUrlExpiry` where the service honours it.

## What survives a failure

- A block the store fails (a 5xx, or a connection lost) is sent again by itself, up to three times, on top of the flow's own
  retries. A repeated block lands on the same place, and the rest of the file is not touched.
- A repeated commit finds the same blocks.
- A refusal (a 4xx) is not repeated.
- A file that changes under a copy, or ends early under a read, commits nothing.
- Anything that ends the try starts the file again on the next one, on a new location, since a location is a credential and
  is kept nowhere.

## A request body ceiling

`reliability.maxRequestBodyBytes` bounds a request, so it bounds a block: a file above it still goes, in blocks no larger.
It holds a record only for a file that has to go in one request, on a location that takes no blocks.

## Other stores

Only an Azure blob takes blocks here. A presigned `PUT` on MinIO, S3 or Google Cloud Storage goes in one request, and a
collection's files go as [the route](routes.md#dataset) sends them. A file collection on Azure goes to its Data Lake directory in
parts of `uploadBlockMiB`, in order, each repeated by itself, and is always sent from the node.

## What the record keeps

The step that sent the file (`storage-<set>` on the `dataset` route, `upload-<n>` on the `file` route) says how it went, in the
record's history and on the attempt ([steps and returned values](../concepts/protocols.md#steps-and-returned-values)):

| Value | Meaning |
| --- | --- |
| `transfer` | `single`, `blocks` or `serverCopy`. |
| `blocks` | How many blocks it went in. |
| `md5` | The file's MD5, when it was read here or its store keeps one. |
| `notCopied` | Why the store did not copy it, when it was asked to or could not be. |

## Related

- [Routes](routes.md): what each route does around the upload.
- [Records an enrichment adds](enrich-records.md): a LAS file of a well log.
- [Availability and retention](../concepts/availability-and-retention.md#size-ceilings): the size ceilings.
