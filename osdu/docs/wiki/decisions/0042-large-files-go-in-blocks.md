---
id: delivery-wiki-0042-large-files-go-in-blocks
title: "Decision 0042: a large file goes to Azure in blocks, and the store copies it when it may read it"
type: decision
summary: "Why a file above one block goes in blocks, why the node makes the calls azcopy makes rather than running it, and why a refused copy falls back to sending."
keywords:
  - large file
  - block blob
  - Put Block From URL
  - azcopy
  - az storage blob copy start
  - user delegation SAS
  - server side copy
  - CannotVerifyCopySource
updated: 2026-11-02
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/SignedUploads.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/BlockBlob.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/UploadParts.cs
  - osdu/src/SqlFlow.Delivery/Storage/FileStore.cs
  - osdu/specs/core/INTEGRATION.md
  - osdu/tests/SqlFlow.Delivery.Tests/LargeFileLiveTests.cs
referenceRefs:
  - delivery-flow-large-files
  - delivery-flow-routes
---
# 0042: A large file goes to Azure in blocks, and the store copies it when it may read it

Status: accepted. Documented in [large files](../../reference/flow/large-files.md).

The human's words: "i have heard that some las files are 16 gB and pushing that over a post is not feasebal", "the most ideal way
would be to use az upload command but from the worker node where we point from one storge account to another", "A team i am
working with were mentioning something about as SAS token", and "Test the large file logic on small files to make sure its
working".

## What stood in the way

The upload was already token based: `storageInstructions` hands out a SAS and the bytes go straight to storage. What failed was
one request carrying the whole file. Azure's Put Blob holds 5000 MiB, and before a 16 GB file reached that limit it met
`reliability.timeoutSeconds`, whose 100 seconds cover a whole request. Raising the timeout would only have moved the failure.

Microsoft's guidance for Azure Data Manager for Energy says the same: below 5 GB one `PUT`, above it `azcopy`, because "a single
PUT blob call can't be greater than 5 GB". OSDU has no large file API. The community File service left this to the client, and
the Azure provider has since left that repository (`b23f00c`), so the last source is at `fd355c86`.

## The Seismic route already had it

`AzureBlobStore` already staged blocks and committed them with a running MD5, as sdutil writes them, and `PartReader` read a file
once while hashing it. So the block writing moved to `BlockBlob`, which both use, rather than a second uploader being written.
The Seismic route keeps sending one block at a time, because its credential renewal must not race another block.

## The node makes the calls azcopy makes, rather than running it

Between two accounts, `azcopy` is a client of Put Block From URL. Making those calls from the node gives the same copy on the
wire, with no byte through the node, and keeps what the delivery path needs. A separate program would have put a SAS on a command
line, where a process listing shows it, added a binary to every node image, and taken the requests out of the flow's retries, rate
limit and address policy, and out of what the attempt records.

## `az storage blob copy start` was tried, and it cannot work here

It is Copy Blob, which answers `202 pending` and carries on by itself. Against real storage, a signature of OSDU's shape (`sp=cw`)
cannot read the blob, so `HEAD` and Get Block List both answer 403, and nothing could ever say the copy finished. The dataset
would be registered over a blob still being filled. Put Block From URL answers when the block is staged, so the commit that
follows can be trusted. `az storage blob upload` takes a local file, so it would have needed the file on the node.

## The node signs a read of the file, rather than sending a bearer token

Put Block From URL takes either. A bearer token asks for the same tenant on both sides, which a platform's storage does not
promise, and its service version must be 2020-10-02 or later. A user delegation SAS for that one file, read only and ending with
the destination's, works across tenants and needs no role beyond the one reading the file already takes.

## A copy that is refused is not a failure

A landing account behind a firewall refuses the store with `403 CannotVerifyCopySource`, and the dev estate reaches its landing
account over a private endpoint. So the first block goes alone, a 4xx sends the file from the node, and the step records why. A
copy that had to be configured per estate would have stopped working the first time a network changed.

Against real storage, allowing the caller's own address through the source's firewall let the copy through as well. That is
observed behaviour, not a documented rule, so nothing relies on it: the fallback is what makes the path safe.

## The File service's checksum decides the file route

A blob committed from blocks has no content MD5 unless the commit states one, and `POST /files/metadata` then reads the whole blob
inside its request to compute one, up to 5 GiB. So a streamed file states the MD5 read on the way, and on the routes that register
through the File service a file whose store holds no MD5 is sent rather than copied. The empty blob the service creates before it
signs carries the empty hash, and a commit without an MD5 clears it, so a stale value cannot reach a record.

## A small file has to take the same path

The request was to test this on small files, and a size threshold nobody can lower can only be tested by moving 5 GB. So the
threshold is `uploadBlockMiB`, the block size itself: at 1, a 3 MiB file goes in four blocks through every line a 16 GiB file
does. The fake's block store answers as real storage answered, and `LargeFileLiveTests` runs the same code against two real
accounts, one of them Data Lake, including a real 16 GiB copy built from one repeated block.

## What it costs

A location lasts one hour on the Dataset route, because the service does not pass an expiry on, and it cannot be renewed: a new
one is another blob. So an upload that outlasts its hour fails and starts over. A file sent from the node holds
`uploadConcurrency` blocks in memory. A copied file on the `dataset` route has no MD5 in the ledger unless its store keeps one, and
what stops a change under a copy is the file's version compared before the commit, not a check of each block, since Put Block From
URL documents no condition on its source.
