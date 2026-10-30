---
id: delivery-cli-validate
title: "sqlflow validate for OSDU documents: what each delivery, cache, mapping and other document checks offline"
type: cli-command
summary: "What sqlflow validate checks in each document OSDU Delivery adds, its flows, mapping, dictionary and repository document, offline, and what it leaves to check."
keywords:
  - validate
  - offline validation
  - ci gate
  - validate a mapping
  - validate a delivery flow
  - unknown key
  - strict keys
  - flowType
  - documentType
  - pinned mapping
  - floating reference
  - dictionary
  - yaml error
  - literal secret
  - missing mapping file
  - dictionary not found
  - reference in a location
related:
  - cli-validate
  - delivery-cli-check
  - delivery-flow-overview
  - delivery-flow-delivery
  - delivery-flow-mapping
  - delivery-flow-cache
  - delivery-flow-dictionary
  - concept-cli-conventions
sourceRefs:
  - sqlflow/src/SqlFlow.Cli/Program.cs
  - sqlflow/src/SqlFlow.Cli/LocalInspectVerbs.cs
  - sqlflow/src/SqlFlow.Yaml/YamlDocumentLoader.cs
  - sqlflow/src/SqlFlow.Execution/DocumentLoader.cs
  - sqlflow/src/SqlFlow.Core/Secrets/SecretHygiene.cs
  - osdu/src/SqlFlow.Delivery/Engine/DeliveryServices.cs
  - osdu/src/SqlFlow.Delivery/Documents/DeliveryDocumentLoader.cs
  - osdu/src/SqlFlow.Delivery/Documents/DeliveryFlowKind.cs
  - osdu/src/SqlFlow.Delivery/Documents/CacheFlowKind.cs
  - osdu/src/SqlFlow.Delivery/Documents/RetrievalFlowKind.cs
  - osdu/src/SqlFlow.Delivery/Documents/AssertionFlowKind.cs
  - osdu/src/SqlFlow.Delivery/Documents/DimensionFlowKind.cs
  - osdu/src/SqlFlow.Delivery/Documents/InventoryFlowKind.cs
  - osdu/src/SqlFlow.Delivery/Documents/RemovalFlowKind.cs
  - osdu/src/SqlFlow.Delivery/Documents/DownloadFlowKind.cs
  - osdu/src/SqlFlow.Delivery/Documents/DictionaryDocumentKind.cs
  - osdu/src/SqlFlow.Delivery/Documents/CompanionFiles.cs
  - osdu/src/SqlFlow.Delivery/Documents/EnrichBinding.cs
  - osdu/src/SqlFlow.Delivery/Documents/MappingMapper.cs
  - osdu/src/SqlFlow.Delivery/Documents/MappingMapper.Record.cs
  - osdu/src/SqlFlow.Delivery/Documents/MappingMapper.Enrich.cs
  - osdu/src/SqlFlow.Delivery/Documents/CacheDocumentMapper.cs
  - osdu/src/SqlFlow.Delivery/Documents/DictionaryMapper.cs
  - osdu/src/SqlFlow.Delivery/Documents/RetrievalDocumentMapper.cs
  - osdu/src/SqlFlow.Delivery/Documents/AssertionDocumentMapper.cs
  - osdu/src/SqlFlow.Delivery/Documents/DimensionDocumentMapper.cs
  - osdu/src/SqlFlow.Delivery/Documents/InventoryDocumentMapper.cs
  - osdu/src/SqlFlow.Delivery/Documents/RemovalDocumentMapper.cs
  - osdu/src/SqlFlow.Delivery/Documents/DownloadDocumentMapper.cs
  - osdu/src/SqlFlow.Delivery/Model/DeclaredPartition.cs
  - osdu/src/SqlFlow.Delivery/Model/FlowDefinition.cs
  - osdu/docs/census/README.md
---

# sqlflow validate for OSDU documents: what each delivery, cache, mapping and other document checks offline

`sqlflow validate` is SQLFlow's verb: it parses and validates a document, or every document under a folder, through
the loader a run uses, without a database and without executing anything. How the verb behaves, its options, the folder
mode and its `--json` report, the `.sqlflow/env` file, the secret-hygiene warning and the exit codes are on
[sqlflow validate](../../../../sqlflow/docs/reference/cli/validate.md). The command line is `sqlflow`: OSDU Delivery's
CLI is SQLFlow's CLI with the OSDU verbs added, and the module registers its document kinds with it, so `validate`
reads the documents OSDU Delivery adds as well. This page says what each of them checks.

Validation is offline: no catalog, no module database, no OSDU, and no secret resolution (`${env:...}` and
`${keyvault:...}` stay references). It does read the files a flow names beside it, from disk and the way a run finds
them: the mapping a delivery flow pins, the dictionaries a cache or dimension flow names (see each kind below), and the
[repository document](../flow/repository.md) above every flow, whose code its runs carry. What
needs the template folders or the database (the template a mapping fills, the cache it reads, the partition registry)
is [`sqlflow check`](check.md); what needs OSDU or the ingestion tables is the run.

## The documents it recognises

| Document | Discriminator | OK line |
| --- | --- | --- |
| Delivery flow | `flowType: delivery` | `OK  '<name>' is valid (delivery: <record tables> -> <target.endpoint>).` |
| Retrieval flow | `flowType: retrieval` | `OK  '<name>' is valid (retrieval: <source.endpoint> -> <target.location>).` |
| Cache flow | `flowType: cache` | `OK  '<name>' is valid (cache: <source.endpoint, else source.connection> -> catalog).` |
| Assertion flow | `flowType: assertion` | `OK  '<name>' is valid (assertion: <source.endpoint> -> report).` |
| Dimension flow | `flowType: dimension` | `OK  '<name>' is valid (dimension: <source.endpoint> -> dimensions).` |
| Inventory flow | `flowType: inventory` | `OK  '<name>' is valid (inventory: <source.endpoint> -> inventories).` |
| Removal flow | `flowType: removal` | `OK  '<name>' is valid (removal: <key sources> -> <delivery flows>).` |
| Download flow | `flowType: download` | `OK  '<name>' is valid (download: <source.endpoint> -> <target.location>).` |
| Mapping | `documentType: mapping` | `OK  '<Name>@<version> -> <template.kind>' is valid (mapping).` |
| Dictionary | `documentType: dictionary` | `OK  '<name> (<n> entries, key <key>)' is valid (dictionary).` |
| Repository document | `documentType: repository` | `OK  '<code>' is valid (repository).` |

The eight flow kinds take SQLFlow's envelope keys (`schedule`, `mode`, and the rest SQLFlow documents for every flow) as
any flow does. The mapping, the dictionary and the repository document are companion documents: they are not flows, have no envelope, and are
reported under their own document type. A `flowType` no kind registers fails with SQLFlow's message, which ends by
listing the registered kinds (`Registered kinds: 'assertion' for ..., 'cache' for ..., 'delivery' for ..., ...`); a
`documentType` no kind registers fails with
`<file>: unknown documentType '<type>'. Registered document types: 'dictionary' for a lookup table a cache flow holds in the partition's cache, 'mapping' for how a delivery flow renders the rows of an ingestion table into records of one OSDU kind, 'repository' for the code a repository's runs carry in the correlation id of their OSDU requests.`

In the folder mode each document is one line, and `--json` reports each document's `kind` as the discriminator above
(`delivery`, `mapping`, `dictionary`, `repository`, ...).

## What every document checks

- **Strict keys.** Every loader refuses a key it does not know, rather than ignoring it, with the line and column, and a
  key written twice in one map:

  ```text
  ERROR  flows/welldb-wellbore-03-delivery.yaml: invalid YAML at line 14, column 1 - Property 'rendr' not found on type 'SqlFlow.Delivery.Documents.FlowYaml'.
  ERROR  flows/welldb-wellbore-03-delivery.yaml: invalid YAML at line 4, column 1 - Encountered duplicate key batch
  ```

  The keys each document accepts are listed in the key census, `osdu/docs/census/keys.<document>.json`, which a test
  holds against the loaders.
- **Required keys**, named as written: `'<key>' is required.`, for example `'source.work' is required.` on a delivery
  flow, `'dataset.system' is required.` on a mapping.
- **Partitions.** `partitions` names each partition literally, by its data-partition-id (letters, digits, underscore,
  hyphen and dot), never as a reference, at least one, at most 64, none twice. A flow that names its partitions, or
  leaves them to the registry, may not also set the `data-partition-id` header: each run sets it.

  ```text
  ERROR  <file>: partitions[0] is the reference '${env:OSDU_PARTITION}'. A partition is named literally, by its data-partition-id: the name is the key its cache, its ledger and its runs are kept under, and a key some host resolves is no key. Keep references for the endpoint and the credentials.
  ```

- **Parameters.** Every `{name}` token in a value that takes substitution names a declared parameter
  (`<file>: source.work uses '{region}', which is not declared under parameters.`).
- **Ranges and forms** of the settings: `parallelism.recordsAtOnce must be at least 1.`, a service path starts with `/`,
  a kind is `authority:source:entityType:version`, and so on.
- **Credentials.** SQLFlow's credential-hygiene check warns about a flow's credential references that embed a secret
  keyword (`password=`, `secret=` and the like) without failing. The module's loaders go further and refuse a literal
  secret in every OSDU flow document, naming the key and never the value:
  - the `secretRef` of an auth block: a delivery flow's `target.auth` and `target.airflow.auth`, every other kind's
    `source.auth`;
  - a field of a token request named as a secret (`client_secret`, `password`, `refresh_token`, `client_assertion`, ...);
  - a header that carries a credential (`Authorization`, an API or subscription key, a token, any `x-api-*` header) in
    `target.headers`, `target.airflow.headers` or `source.headers`; a scheme may stand before the reference
    (`Bearer ${env:OSDU_TOKEN}`);
  - an endpoint, `token.url` or `token.discoveryUrl` written literally whose user info or query carries a credential;
  - a `source.connection` that carries a literal password (delivery and cache flows).

  A credential header in `protocolOptions.uploadHeaders` and a `workflowPayload` entry named as a secret are refused
  outright, reference or not: both are sent as written and never resolved. Literals stay allowed where the value is no
  secret: `secondarySecretRef` (a user name or client id), `client_id`, `scope`, `audience`.

  ```text
  ERROR  <file>: target.auth.secretRef holds a literal value, and it is the credential the flow authenticates with (the token, the API key, the password or the client secret). A flow document holds references only: write ${keyvault:vault/secret} or ${env:NAME}, and keep the value in the key vault or the environment of the nodes that run the flow (locally, the git-ignored .sqlflow/env file).
  ERROR  <file>: source.connection carries a literal password. A flow document holds references only: put the connection string, or its password, behind ${keyvault:vault/secret} or ${env:NAME}, or connect with Azure AD (Authentication=Active Directory Default).
  ```

- **Locations.** A file location (a delivery flow's payload `root`s, a retrieval or download flow's `target.location`) holds no
  `${...}` reference: it is substituted from the run's parameters and read as written.
  `<file>: source.payloads.las.root '<location>' holds a ${...} reference, and a location is not resolved from one: write the path or storage URI itself, and vary it per run or environment with a {parameter} token declared under parameters.`
  A delivery flow's `source.work` may open with one (`${env:LANDING_URL}/.work/welllog/{logSource}`), which the run
  resolves; a reference anywhere else in it is refused.

## Delivery flow

- `name`, `source`, `source.connection`, `source.work`, `target` and `target.endpoint` are required; in the single form
  `render`, `render.mapping`, `target.protocol`, `source.record.object` and `source.record.key` too, and in the interface
  form each interface's `record` and `mapping`.
- The mapping is pinned as `Name@version`:
  `<file>: render.mapping 'Wellbore' must be pinned as 'Name@version'; floating references are not allowed.`
- `source.record.object` is a three-part name: `source.record.object 'silver.Wellbore' must be a three-part name [database].[schema].[table]: it has 2 part(s).`
  Its `key` names each column once.
- `target.protocol` (or an interface's `route`) is one of `storage`, `file`, `dataset`, `manifest`, `ddms`,
  `fileAndDdms`, `manifestAndDdms`, `workflow`, `dspdm`, `etp`, and an interface's route agrees with what it declares (a
  `files` or `bulk` part, a `workflow`); the route-specific blocks (`target.ddms`, `target.dspdm`, `target.etp`, a
  workflow's stages) are checked against the route that uses them.
- `target.auth` is complete for its type: `oauth2ClientCredentials` needs a `token` block, `bearer`, `apiKeyHeader` and
  `basic` need `secretRef`, and `apiKeyHeader` needs `headerName`.
- `change.detect`, `change.payloadDetect`, the `reliability` and `parallelism` settings, `failWhen` and
  `target.protocolOptions` (media types, a manifest section, upload expiry, batch size up to 500) keep to their ranges,
  and a parallelism setting written under `reliability` is refused, naming where it goes.
- In the interface form: interface names are unique ignoring case, keys that belong to an interface are not written at
  the source level, `after:` names other interfaces of the document, and interfaces that wait for each other through
  `after:` are refused (`the interfaces wellbores -> welllogs -> wellbores wait for each other through after:, so none of them could run first.`).
- A workflow stage's `context` holds no credential: whatever the workflow, with a contract or without, a value under a
  key the contract marks a credential, or under any key named as a secret (a password, secret, token, assertion,
  signature, authorization, an API or account key), is a single `{secret:name}` placeholder naming a reference under the
  route's `secrets`. A literal would be sent to the Workflow service and kept in the record's step history:
  `<file>: target.workflow.stages[0].context: 'Payload.client_secret' is named as a secret; give it as {secret:name} with the reference under secrets, never as a value in the document.`
  A contract key whose value does not read as a template is refused with its reason.
- The mapping each interface pins is found and read as a run finds it (`render.mappings`, else the nearest `mappings`
  folder walking up from the flow file), and a flow whose mapping is missing or does not load is refused:
  `<file>: render.mapping: Mapping 'Wellbore@1.0.0' was not found under '../mappings'. Expected one of: Wellbore@1.0.0.yaml, Wellbore@1.0.0.yml, 1.0.0.yaml, 1.0.0.yml.`
  In the interface form the key is the interface's: `interfaces.<name>.mapping: Mapping ...`. Only `validate` refuses a
  flow for this; parsing alone (the estate scan, the repository sync, a node) keeps the flow and reports the missing
  mapping in its own terms.
- The enrichment mappings the pinned mapping's `enrich:` names are found and read the same way, and the flow is bound to
  its mapping's enrichments: a `workflows.<name>` or a `source.datasets.<name>.files` that no enrichment of the mapping
  reads is refused, and so is an enrichment on the `dspdm` or `etp` route ([Enrichments](../flow/enrich.md)).
- What is added to each record is the mapping's to say, so a flow that still declares `enrich:` (or an interface's
  `enrich`) is refused, saying where each part goes:
  `<file>: enrich declares what is added to each record, which the mapping says now: move each enrichment to the enrich: block of render.mapping's mapping, with its rows, mapping, key, after and onHold; bind its rows here under source.datasets.<name> (their files under source.datasets.<name>.files), and its workflow under workflows.<name>.`

Waits that come from the relationships a mapping fills, which need the templates, are worked out by `check`.

## Mapping

- `name`, `version`, `template.kind`, `dataset.system`, `dataset.key`, the `dataPartition` parameter and the `record`
  tree are required
  (`the mapping must declare the 'dataPartition' parameter; record ids and references are minted in that partition.`).
- `template.kind` is one type in one version, `authority:source:entityType:major.minor.patch`; `template.version`, when
  written, is the 16 hexadecimal characters of a template version, and left out, the version is settled from the
  [template folders](../concepts/templates.md#template-folders) on the mapping's path when it is read for a run. The
  template itself is not loaded.
- `dataset.key` and `dataset.identity` name columns of the dataset's own row, each once.
- `record` is laid out as the record is. Every word of the mapping language starts with `$`, and a word the language
  does not have is refused with the one it most likely meant:
  `record.data.FacilityName: '$form' is not a word of the mapping language. Did you mean '$from'? A property whose name starts with '$' is written $$form.`
  A map mixing `$` words and properties is refused, and so is a variable filled twice.
- Every modifier is one the language has (`trim`, `upper`, `lower`, `split`, `replace`, `equals`, `date`, `number`,
  `id`, `ref`) with its own settings checked:
  `record.data.FacilityName $modifiers[0]: 'trimm' is not a modifier. The modifiers are trim, upper, lower, split, replace, equals, date, number, id and ref.`
- Every `{$param.<name>}` names a declared parameter; every search a node reads is declared under `searches` and pins
  the schema of the kind it searches; a declared search or lookup that nothing reads is refused.
- `acl.owners`, `acl.viewers`, `legal.legaltags` and `legal.otherRelevantDataCountries` are literal lists of at least
  one text value.
- An `enrich:` block names each enrichment once (letters, digits and `_`), and each pins its enrichment `mapping` as
  `Name@version` or declares `bulk: true`; `rows` is `one` or `many`, `onHold` is `record` or `step`, `key` only with
  `rows: many`, and `after:` names other enrichments of the mapping without any two waiting for each other. A `$link`
  names an enrichment the mapping declares ([Enrichments](../flow/enrich.md)).
- A mapping holds no `fixtures` block: `a mapping holds no fixtures; delete its fixtures block. ...`

The mapping's vocabulary is on [The mapping document](../flow/mapping.md) and the pages it links.

## Cache flow

- `types` is required, each type with exactly one origin: an OSDU `kind`, a `dictionary`, an ingestion `table`, or a
  `dimension` a dimension flow builds. Type names are unique.
- An OSDU type lists the paths it caches and needs an `entityType` when its kind names none; `source.endpoint` is
  required when any type is searched on OSDU, and `source.endpoint` and `source.auth` are refused when none is.
- A table type names its `key` and at least one column besides it; `source.connection` is required when any type reads
  a table, and refused when none does.
- A dictionary type takes nothing but its name: the dictionary document names its key and fields.
- A type that narrows `partitions` names partitions of the flow, and every partition the flow names is built by some
  type.

- The dictionary each dictionary type names is found and read as a refresh finds it, in the nearest `dictionaries/`
  folder walking up from the flow file, and a flow naming one that is missing or does not load is refused:
  `<file>: type 'SamplingDomain': Dictionary 'sampling-domain' was not found under '../dictionaries'. Expected sampling-domain.yaml or sampling-domain.yml.`

## Dictionary

- One document per file, with the keys `documentType`, `name`, `description`, `key`, `fields` and `entries` only.
- `name` is a letter followed by letters, digits, `_` or `-`, at most 200 characters; the file is
  `dictionaries/<name>.yaml`.
- `entries` maps each key to one text (or `~`), or, for a dictionary with `fields`, to a map of those fields. At most
  100,000 entries. Keys are unique as written and once trimmed, and a key with spaces around it is refused:
  `line 8: the key 'MD ' has spaces around it, and a value is matched trimmed.`
- Neither the key nor a field may be called `id`.

## Repository document

- The file is `osdu-delivery.yaml` or `osdu-delivery.yml`, exactly; a file of another name declaring
  `documentType: repository` is refused, since a flow finds the document by its name.
- The keys are `documentType`, `code` and `description` only. `code` is required: a lower-case letter, then lower-case
  letters and digits, at most 16 in all and no hyphen.
- Every flow is checked against the document a run of it would read, the nearest above it up to the folder holding
  `.git`, and is refused for one a run could not read:
  `<file>: ../../osdu-delivery.yaml: code 'WL' is not a repository code: a lower-case letter, then lower-case letters and digits, at most 16 in all and no hyphen, so the correlation id splits on its hyphens.`
  A flow with none above it is valid ([repository document](../flow/repository.md)).
- Every flow's `telemetry.operation`, when written, is a code by the same rule, and no `headers` block of a flow names
  `correlation-id` ([request correlation](../concepts/request-correlation.md)).

## Retrieval flow

- `name`, `source.endpoint`, `source.headers` with a non-empty `data-partition-id`, `target` and `target.location` are
  required, and `source.kind` (one kind) or `source.kinds` (a list, each once).
- `target.location` takes `{run}`, `{date}` and declared parameters only:
  `target.location uses '{region}', which is neither a run token ({run}, {date}) nor declared under parameters.`
- `target.location` holds no `${...}` reference (see Locations above).
- `target.format` is `jsonl`, `target.compression` `none` or `gzip`, `target.manifest` a file name, not a path;
  `source.pageSize`, `source.fetchParallelism`, `target.rollRecords` and the incremental window keep to their ranges.
- `source.probePath` is a delivery flow's setting and is refused:
  `<file>: source.probePath is not a key of a retrieval flow: the target probe covers delivery flows only, and a retrieval run never calls it. A retrieval that cannot reach the search service fails on its first page and says why; remove the key.`

## Assertion flow

- `name`, `source.endpoint` and 1 to 500 tests are required; each test has a unique `name`, one `kind` and 1 to 100
  assertions.
- A test reads one type in one version: a kind with wildcards is refused.
- A test reads the records its `ids` name or the records a search finds, never both:
  `tests[0] 'wellbores-landed' reads records by ids, and names a query as well: a test reads the records its ids name, or the records a search finds, not both.`
- Each assertion names one subject with the keys that subject takes, and operands that suit its operator; tags, sort,
  spatial filters, `maxRecords` and bulk reads are checked. A `{token}` is `{partition}` or a declared parameter.
- A bulk read's `columns`, when listed, include every column the test's assertions name:
  `<file>: tests[2] 'log-curves': bulk.columns lists the only columns the test reads, and leaves out 'GR' (assert[4]); add it to bulk.columns, or leave bulk.columns out to read exactly the columns the assertions name.`

Whether each path is a property of the kind is checked against the template the template folders on the flow's path
hold when the tests run, or with
`sqlflow run <flow.yaml> --operation plan`.

## Dimension flow

- `name`, `source.endpoint` and 1 to 100 dimensions are required; each has a unique `name`, a `kind` (wildcards allowed
  per segment) and a `path`.
- A `path` is a property the search index holds:
  `dimensions[0] 'Wellbore': path FacilityName is not a property the index holds of the record. ...; its data is under data.`
- Labels, attributes, `clean` steps (each map step naming a dictionary), column names and `source.aggregationSize` are
  checked; two dimensions may not share a table name.
- The dictionary each map step names is found and read in the nearest `dictionaries/` folder walking up from the flow
  file: `<file>: dimension 'CurveMnemonic': Dictionary 'curve-aliases' was not found under '../dictionaries'. Expected curve-aliases.yaml or curve-aliases.yml.`

## Inventory flow

- `name`, `source.endpoint` and 1 to 100 inventories are required; each has a unique `name` and a `kind`.
- `source.read` is `search` or `storage`; a `query` is refused on a storage read.
- `owners` lists at most 50 identities, each once.
- A `removal` block names the findings an operator may remove, of `orphan`, `stale` and `forgotten` only:
  `removal.findings[0] is 'missing'; the ids an inventory may remove are those of orphan, stale, forgotten: what OSDU serves that no ledger holds live. ...`

## Removal flow

- `name` and 1 to 50 `removals` are required. Each removal names the delivery `flow` whose records it takes out (and its
  `interface` for a source of several), is named once in the flow (ignoring case; left out, the name is the delivery
  flow's, with its interface), and reads `keys` (the
  keys the source holds now), `removed` (the keys it lists as removed), or both:
  `<file>: removals[0] declares neither keys (the keys the source holds now) nor removed (the keys it lists as removed); a removal reads at least one.`
- A key source is a SQLFlow source reader (`type`, `location`, `options`) or a table or view of a source database
  (`connection`, `object`, `filter`), not both. A `connection` is a `${keyvault:vault/secret}` or `${env:NAME}`
  reference, a `filter` carries its own leading `AND`, and a table of removed keys names its `removedAt` column. A reader
  of `keys` takes no date window option, since every key outside the window would look removed.
- `guard.maxPercent` is 0 to 100 and `guard.maxRecords` 0 or more. `partition` is the run's: neither a declared
  parameter nor a value a removal gives its delivery flow ([Removal flow](../flow/removal.md)).

## Download flow

- `name`, `source.endpoint`, `target.location` and 1 to 100 `downloads` are required; each has a `kind` (wildcards
  allowed per segment) and a name no other download has, which names its folder and so has to be one Windows can make.
- `target.location` is an absolute path or a storage URI, holds no `${...}` reference (see Locations above), and takes
  `{partition}` and declared parameters only: a run token is refused, since every run writes to the same folder.
  `<file>: target.location '<location>' is a relative path, and a run reads its flow from a checkout of its own, so the files would land inside that checkout. Write an absolute path or a storage URI (abfss://... or https://<account>.blob.core.windows.net/...).`
- `source.read` is `search` or `storage`; a `query` is refused on a storage read, and its `{token}`s are `{partition}`
  or declared parameters. `partition` is not a declared parameter ([Download flow](../flow/download.md)).

## Examples

Validate the delivery flow and the mapping it pins:

```bash
sqlflow validate flows/welldb-wellbore-03-delivery.yaml
sqlflow validate mappings/Wellbore@1.0.0.yaml
```

```text
OK  'welldb-wellbore-03-delivery' is valid (delivery: OsduData.silver.Wellbore -> ${env:OSDU_URL}).
OK  'Wellbore@1.0.0 -> osdu:wks:master-data--Wellbore:1.3.0' is valid (mapping).
```

A source with two interfaces lists both record tables:

```text
OK  'welldb-03-delivery' is valid (delivery: OsduData.silver.Wellbore, OsduData.silver.WellLog -> ${env:OSDU_URL}).
```

Validate a whole repository, the CI gate:

```bash
sqlflow validate welldb
```

```text
OK      dictionaries\curve-aliases.yaml  (dictionary 'curve-aliases (4 entries, key key)')
OK      dictionaries\sampling-domain.yaml  (dictionary 'sampling-domain (5 entries, key key)')
OK      flows\welldb-03-delivery.yaml  (delivery 'welldb-03-delivery')
OK      flows\welldb-06-inventory.yaml  (inventory 'welldb-06-inventory')
OK      flows\welldb-lookups-00-cache.yaml  (cache 'welldb-lookups-00-cache')
OK      flows\welldb-retrieval-01-wellbores.yaml  (retrieval 'welldb-retrieval-01-wellbores')
OK      flows\welldb-wellbore-03-delivery.yaml  (delivery 'welldb-wellbore-03-delivery')
OK      flows\welldb-welllog-03-delivery.yaml  (delivery 'welldb-welllog-03-delivery')
OK      flows\welldb-welllog-04-assertion.yaml  (assertion 'welldb-welllog-04-assertion')
OK      flows\welldb-welllog-05-dimensions.yaml  (dimension 'welldb-welllog-05-dimensions')
OK      mappings\Wellbore@1.0.0.yaml  (mapping 'Wellbore@1.0.0 -> osdu:wks:master-data--Wellbore:1.3.0')
OK      mappings\WellLog@1.0.0.yaml  (mapping 'WellLog@1.0.0 -> osdu:wks:work-product-component--WellLog:1.4.0')
12 valid, 0 broken of 12 document(s) under <folder>.
```

A mapping that names a modifier the language does not have:

```text
ERROR  mappings/Wellbore@1.0.0.yaml: record.data.FacilityName $modifiers[0]: 'trimm' is not a modifier. The modifiers are trim, upper, lower, split, replace, equals, date, number, id and ref.
```

## Exit behavior

SQLFlow's: 0 when the document (or every document of the folder) is valid, 1 otherwise, with one redacted
`ERROR  <message>` line for a single document. A credential-hygiene warning does not change the exit code. See
[sqlflow validate](../../../../sqlflow/docs/reference/cli/validate.md).

## See also

- [sqlflow validate](../../../../sqlflow/docs/reference/cli/validate.md): the verb, its folder mode and `--json` report.
- [sqlflow check](check.md): the mapping against the template its template folders hold and the cache, which needs the module database.
- [The documents OSDU Delivery adds](../flow/overview.md): the eight flow kinds, the mapping, the dictionary and the repository document.
- [Delivery flow](../flow/delivery.md), [The mapping document](../flow/mapping.md), [Cache flow](../flow/cache.md),
  [Dictionary](../flow/dictionary.md), [Removal flow](../flow/removal.md), [Download flow](../flow/download.md): every key
  each document takes.
- [CLI conventions](../../../../sqlflow/docs/reference/concepts/cli-conventions.md): options, streams and exit codes shared by every command.
