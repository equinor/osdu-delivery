---
id: delivery-flow-delivery-errors
title: "Delivery flow load errors: what sqlflow validate refuses, and each message"
type: flow-reference
summary: "What sqlflow validate refuses in a flowType: delivery document, with each message, and what it leaves to sqlflow check and a run."
keywords:
  - "delivery flow validation errors"
  - "what validate refuses"
  - "floating mapping"
  - "must be pinned as Name@version"
  - "three-part name"
  - "literal password refused"
  - "literal secret refused"
  - "invalid yaml property not found"
  - "cache step entries"
  - "render.cacheVersion pruned"
yamlPath: "(root, flowType: delivery)"
related:
  - delivery-flow-delivery
  - delivery-flow-delivery-record-table
  - delivery-concept-preflight
  - delivery-flow-mapping
  - delivery-flow-interfaces
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Documents/DeliveryDocumentLoader.cs
  - osdu/src/SqlFlow.Delivery/Documents/DeliveryFlowKind.cs
  - osdu/src/SqlFlow.Delivery/Documents/WorkflowMapper.cs
  - osdu/src/SqlFlow.Delivery/Documents/YamlModels.cs
---

# Delivery flow load errors

What a [delivery flow](delivery.md) document is refused for.

Every message starts with the file. A sample of what `sqlflow validate` refuses:

| Cause | Message |
| --- | --- |
| A floating mapping | `render.mapping 'WellLog' must be pinned as 'Name@version'; floating references are not allowed.` |
| No route in the single form | `'target.protocol' is required.` |
| An unknown key | `invalid YAML at line 28, column 3 - Property 'replica' not found on type 'SqlFlow.Delivery.Documents.FlowSourceYaml'.` |
| A two-part table name | `source.record.object 'silver.WellLog' must be a three-part name [database].[schema].[table]: it has 2 part(s).` |
| A child join missing a key column | `source.datasets.curves.join does not cover key column 'log_source' of the record table; every key column must be joined, or a child row could belong to several records.` |
| A scope on an undeclared parameter | `source.record.scope binds column 'log_source' to parameter 'source', which is not declared under parameters.` |
| A limit out of range | `source.record.maxRecords is 5000; a run reads the first 1 to 1,000 records of its scope, or leave it out to read the scope whole.` |
| An undeclared token | `source.work uses '{source}', which is not declared under parameters.` |
| A reference in a payload root | `source.payloads.las.root '${env:LAS_ROOT}' holds a ${...} reference, and a location is not resolved from one: write the path or storage URI itself, and vary it per run or environment with a {parameter} token declared under parameters.` |
| A reference inside the work location | `source.work 'work/${env:WORK_ROOT}' names a reference other than at its start. A work location that names one is the reference's value with a path after it, such as ${env:LANDING_URL}/.work/welllog/{logSource}.` |
| A literal password | `source.connection carries a literal password. A flow document holds references only: put the connection string, or its password, behind ${keyvault:vault/secret} or ${env:NAME}, or connect with Azure AD (Authentication=Active Directory Default).` |
| A literal secret | `target.auth.secretRef holds a literal value, and it is the credential the flow authenticates with (the token, the API key, the password or the client secret). A flow document holds references only: ...` |
| A literal credential header | `target.headers.Authorization holds a literal value, and the header carries a credential; it holds a reference, after its scheme when it has one (Bearer ${env:NAME}). ...` |
| `enrich:` on the flow | `enrich declares what is added to each record, which the mapping says now: move each enrichment to the enrich: block of render.mapping's mapping, with its rows, mapping, key, after and onHold; bind its rows here under source.datasets.<name> (their files under source.datasets.<name>.files), and its workflow under workflows.<name>.` |
| A pinned mapping that is not there | `render.mapping: Mapping 'Wellbore@1.0.0' was not found under '../mappings'. Expected one of: Wellbore@1.0.0.yaml, Wellbore@1.0.0.yml, 1.0.0.yaml, 1.0.0.yml.` |
| A setting that moved to `parallelism` | `reliability.renderParallelism is now parallelism.renderThreads: what runs at once, and across the fleet, is the flow's parallelism, and reliability keeps what makes a request hold. Move it.` |
| A `parallelism` key by its old name | `parallelism.batchRecords is now parallelism.recordsPerBatch: the records one batch file holds. Rename it.` |
| A header beside `partitions` | `target.headers names 'data-partition-id', and the flow names its partitions: every run sets the header to the partition it targets. Remove the header.` |
| A pinned cache version whose records are pruned | `render.cacheVersion pins version 20261001T010000Z of the cache of partition 'dev', whose records the cache's retention pruned at <time>, so nothing can render against it. Pin a version the cache still holds ('sqlflow cache list dev' lists them), or remove render.cacheVersion to render against the current version. ...` |
| A pinned cache version with several partitions | `render.cacheVersion pins version 20261001T010000Z, which is a version of one partition's cache, and the flow names 2 partitions, each rendering against its own partition's cache. ...` |
| `render.cache` | `render.cache is not a setting any more: a flow reads the cache of the partition it delivers to (target.headers.data-partition-id), which every cache flow of that partition fills. Remove render.cache.` |
| A missing hash column | `the flow decides payload changes by content hash, so source.payloads.curves.hashColumn must name the record column holding it; or take the files' modified times instead with change.payloadDetect: lastModified.` |
| OAuth without a token block | `target.auth of type oauth2ClientCredentials needs a 'token' block with the token endpoint url and body.` |
| A cache step that is neither auto nor a list | `cache.before is 'everything': write auto, or list the types of the partition's cache by name (UnitOfMeasure) or by OSDU entity type (reference-data--UnitOfMeasure), for example [UnitOfMeasure].` |
| `auto` among a step's entries | `cache.after lists auto among its entries; auto stands alone: write cache.after: auto.` |
| An after step on the dspdm route | `cache.after is set, and the dspdm route writes DSPDM rows, which are no OSDU records a type of the partition's cache holds. Remove it.` |
| An entry the partition's cache does not hold (at run time) | `Pipeline '<name>' names in its cache steps what the cache of partition '<p>' cannot bring up to date, so nothing was read or sent: cache.after names '<entry>', which is neither a type of the partition's cache nor an entity type one holds; it holds ...` |

`validate` also finds and reads the mapping the flow pins, as a run finds it (`render.mappings`, else the nearest
`mappings` folder walking up), and refuses the flow when it is missing or does not load. What validate cannot check (the
mapping against its template, the cache, the tables, the order of interfaces) is checked by `sqlflow check` and by every
run before anything is sent ([preflight](../concepts/preflight.md)).
