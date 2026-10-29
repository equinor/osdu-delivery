---
id: delivery-flow-repository
title: "Repository document (documentType: repository): the code a repository's runs carry in their correlation id"
type: flow-reference
summary: "osdu-delivery.yaml at a repository's root names the code its runs carry in every OSDU request's correlation-id; how a flow finds it."
keywords:
  - repository document
  - documenttype repository
  - osdu-delivery.yaml
  - repository code
  - correlation id
  - telemetry
  - header policy
yamlPath: "(root, documentType: repository)"
related:
  - delivery-concept-request-correlation
  - delivery-flow-overview
  - delivery-cli-validate
  - delivery-cli-run
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Documents/RepositoryDocuments.cs
  - osdu/src/SqlFlow.Delivery/Documents/RepositoryDocumentKind.cs
  - osdu/src/SqlFlow.Delivery/Documents/DeliveryDocumentLoader.cs
  - osdu/src/SqlFlow.Delivery/Documents/CompanionFiles.cs
  - osdu/src/SqlFlow.Delivery/Model/RepositoryDefinition.cs
  - osdu/src/SqlFlow.Delivery/Engine/RunCorrelation.cs
  - osdu/docs/census/keys.repository.json
---

# Repository document (documentType: repository): the code a repository's runs carry in their correlation id

A repository document names the code of a flow repository: the segment every run of its flows carries after
`osdu-delivery` in the `correlation-id` of its OSDU requests, `osdu-delivery-<code>-<operation>-<run id>`
([request correlation](../concepts/request-correlation.md)). It is one small file at the repository's root, reviewed with
the flows, and it does nothing on its own.

```yaml
# osdu-delivery.yaml, at the root of the repository
documentType: repository
code: wl
description: The well log estate.
```

| Key | Type | Required | Meaning |
| --- | --- | --- | --- |
| `documentType` | string | yes | `repository`. |
| `code` | string | yes | The repository's code: a lower-case letter, then lower-case letters and digits, at most 16 in all and no hyphen. |
| `description` | string | no | Free text. |

## What it changes

Only the correlation id of the OSDU requests its flows' runs send. Nothing a flow reads, writes or delivers changes:

| A run of `welldb-welllog-05-dimensions` (a dimension flow, operation code `dim`) | Carries |
| --- | --- |
| Under a repository document with `code: wl` | `correlation-id: osdu-delivery-wl-dim-<run id>` |
| With no repository document above it | `correlation-id: osdu-delivery-dim-<run id>` |

Adding one to a repository takes a file at its root, a `sqlflow validate .`, a commit and a sync
([setting it up](../concepts/request-correlation.md#setting-it-up-for-a-repository)).

## Where a flow finds it

A run of any flow kind reads the file nearest its flow's file: `osdu-delivery.yaml` (or `osdu-delivery.yml`) in the
flow's folder, else in each folder above it in turn.

- **The repository's root is as far as it looks.** The folder holding `.git` (a folder in a clone, a file in a git
  worktree) is the last one read, so a repository never takes the code of a folder it sits in. Without a `.git`, the
  walk stops after 16 folders.
- **The nearest wins.** A folder of the repository may hold one of its own, and the flows under it take that one: a
  repository that keeps several sources apart can give each a code.
- **The name is matched exactly.** `OSDU-Delivery.yaml` is not the document on any host, as a node on Linux would not find
  it; a file of another name that declares `documentType: repository` is refused when it is read.
- **One per folder.** A folder holding both `osdu-delivery.yaml` and `osdu-delivery.yml` is refused:
  `<folder> holds both osdu-delivery.yaml and osdu-delivery.yml; a folder holds one repository document. Remove one.`

A flow with none above it runs as it always did, and its runs' correlation id leaves the repository's segment out
(`osdu-delivery-<operation>-<run id>`). The run log says so.

Since every run reads it, a node runs every OSDU flow from a checkout of its repository at the run's commit, never from a
snapshot of the flow's one file ([which kinds need the repository](overview.md#which-kinds-need-the-repository)).

## What is refused

The document is read strictly, as every OSDU document is:

| Problem | Message |
| --- | --- |
| No code | `osdu-delivery.yaml: code is required: the repository's code, which every run of its flows carries in its correlation id.` |
| A code that breaks the rule | `osdu-delivery.yaml: code 'WL' is not a repository code: a lower-case letter, then lower-case letters and digits, at most 16 in all and no hyphen, so the correlation id splits on its hyphens.` |
| A key it does not take | `osdu-delivery.yaml: invalid YAML at line 3, column 1 - Property 'name' not found on type 'SqlFlow.Delivery.Documents.RepositoryYaml'.` |
| Another file name | `repository.yaml: a repository document is found by its name, walking up from each flow's folder; name the file osdu-delivery.yaml.` |

A run of a flow under a document it cannot read fails before it sends anything, with the document's message.
`sqlflow validate` reads the document as its own, and with every flow under it, so a flow is refused for the document a
run of it would fail on:

```text
$ sqlflow validate .
OK      osdu-delivery.yaml  (repository 'wl')
OK      welldb\flows\welldb-welllog-05-dimensions.yaml  (dimension 'welldb-welllog-05-dimensions')
2 valid, 0 broken of 2 document(s) under <folder>.

$ sqlflow validate welldb/flows/welldb-welllog-05-dimensions.yaml
ERROR  welldb/flows/welldb-welllog-05-dimensions.yaml: ../../osdu-delivery.yaml: code 'WL' is not a repository code: a lower-case letter, then lower-case letters and digits, at most 16 in all and no hyphen, so the correlation id splits on its hyphens.
```

## See also

- [Request correlation](../concepts/request-correlation.md): the whole design, from the value and the operation codes to
  setting it up, which requests carry it and finding a run in the platform's logs.
- [Laying out a repository](overview.md#laying-out-a-repository).
