---
id: delivery-concept-request-correlation
title: "Request correlation: the correlation id every OSDU request carries, from setting it up to finding a run in the platform's logs"
type: concept
summary: "Every OSDU request carries correlation-id osdu-delivery-<repository>-<operation>-<run id>: its parts, setup, scope, refusals, tracing and changes."
keywords:
  - correlation id
  - correlation-id header
  - telemetry
  - telemetry.operation
  - operation code
  - repository code
  - repository document
  - osdu-delivery.yaml
  - run id
  - azure logs
  - application insights
  - log analytics
  - trace a run
  - find a run by id
  - look up a run
  - delivery_run
  - trace-id
  - osdu-delivery header
  - header policy
  - troubleshooting correlation
  - editor key census
related:
  - delivery-flow-repository
  - delivery-flow-overview
  - delivery-concept-ledger
  - delivery-concept-run-trace-and-metrics
  - delivery-concept-record-lifecycle
  - delivery-concept-removal-and-reversal
  - delivery-concept-renaming-a-pipeline
  - delivery-concept-protocols
  - delivery-concept-explorer
  - delivery-cli-validate
  - delivery-cli-run
  - delivery-table-attempt
  - delivery-guide-operations-runbook
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Http/OsduCorrelation.cs
  - osdu/src/SqlFlow.Delivery/Engine/RunCorrelation.cs
  - osdu/src/SqlFlow.Delivery/Model/FlowTelemetry.cs
  - osdu/src/SqlFlow.Delivery/Model/RepositoryDefinition.cs
  - osdu/src/SqlFlow.Delivery/Documents/RepositoryDocuments.cs
  - osdu/src/SqlFlow.Delivery/Documents/RepositoryDocumentKind.cs
  - osdu/src/SqlFlow.Delivery/Documents/CompanionFiles.cs
  - osdu/src/SqlFlow.Delivery/Documents/DeliveryDocumentLoader.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/OsduHttpClient.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/Etp/EtpConnection.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/Ddms/ProductionTimeSeriesShape.cs
  - osdu/src/SqlFlow.Delivery/Engine/Workflows/WorkflowXCom.cs
  - osdu/src/SqlFlow.Delivery/Engine/Operations/DeliveryOperations.cs
  - osdu/src/SqlFlow.Delivery/Engine/Search/PartitionSchemaIndex.cs
  - osdu/src/SqlFlow.Delivery/Http/HttpExecutor.cs
  - osdu/src/SqlFlow.Delivery/Ledger/ProblemSignature.cs
  - osdu/src/SqlFlow.Delivery/Catalog/DeliveryDocumentHash.cs
  - osdu/src/SqlFlow.Delivery.Setup/OsduSignIn.cs
  - osdu/docs/census/keys.repository.json
  - osdu/gui/src/module.tsx
  - osdu/gui/src/features/delivery/RunSearchHits.tsx
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryRunLookup.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/RunSearchContributor.cs
  - osdu/hosts/osdu-delivery-mcp/src/tools/flows.rs
---

# Request correlation: the correlation id every OSDU request carries, from setting it up to finding a run in the platform's logs

Every request OSDU Delivery sends to an OSDU service carries a `correlation-id` header whose value says what sent it:
which repository, which operation, which run. OSDU's services on Azure log that header with everything they do for the
request, so one value finds a run in the platform's logs, and a prefix of it finds every run of a repository or a flow.

```text
correlation-id: osdu-delivery-wl-dim-01a186ef-b444-7732-bb2e-9a7f4dfd4767
                ^             ^  ^   ^
                product       |  |   the run's id
                              |  the flow's operation code
                              the repository's code
```

This page holds the whole design: the value, the two documents that set it, how to set it up, which requests carry it,
what is refused, how the editor checks it, where the value is kept, how to find a run in the platform's logs, what
changed for existing flows, and where it is built. The [repository document](../flow/repository.md) page is that document's own reference, and
[decision 0037](../../wiki/decisions/0037-request-correlation.md) says why it is built this way.

## The value

| Sent by | Value |
| --- | --- |
| A run of a flow | `osdu-delivery-<repository>-<operation>-<run id>` |
| A run of a flow with no repository document above it | `osdu-delivery-<operation>-<run id>` |
| An operation of the product's own (a probe, a read the explorer makes) | `osdu-delivery-<operation>-<id>`, a new id each time |
| A request sent outside both (a tool driving a route itself) | `osdu-delivery-<id>`, a new id per request |

### Where each part comes from

| Part | Comes from | When there is none |
| --- | --- | --- |
| `osdu-delivery` | The product: every value starts with it. | |
| The repository's code | `code` in the [repository document](../flow/repository.md), `osdu-delivery.yaml`, nearest above the flow's file. | The part is left out, and the run log says why. |
| The operation code | The flow's `telemetry.operation`. | The flow kind's code ([operation codes](#operation-codes)). |
| The run's id | The run: the id it is listed under in the GUI, the API and `sqlflow run`. Nothing new is minted for it. | |

Two files set the codes, both versioned with the flows. At the repository's root:

```yaml
# osdu-delivery.yaml
documentType: repository
code: wl
```

In a flow, only to name a code other than its kind's:

```yaml
telemetry:
  operation: whdr
```

Every request of a run carries the same value: its searches, its writes, its retries and its read-backs. One value
finds the whole run, and a prefix finds every run of a repository or a flow.

### The rule for a code

A code, the repository's or an operation's, is a lower-case letter, then lower-case letters and digits, at most 16 in
all, with no hyphen. The value therefore splits on its hyphens: `osdu-delivery`, then the codes, and the id is always
its last 36 characters. `wl`, `whdr` and `dim2` are codes; `WL`, `well-logs` and `2dim` are not.

### Operation codes

A flow of any kind names its own code under `telemetry`:

```yaml
flowType: delivery
name: welldb-welllog-03-delivery
telemetry:
  operation: whdr
```

Left out, a run carries its kind's code:

| Kind | Code |
| --- | --- |
| delivery | `dlv` |
| cache | `cache` |
| retrieval | `ret` |
| assertion | `ast` |
| dimension | `dim` |
| inventory | `inv` |
| removal | `rmv` |
| download | `dwn` |

The code names the flow, not the run's operation: a `plan` and a `deliver` of the same flow carry the same code, and the
run's id says which run it was. Give a flow a code of its own when its runs must be told apart from its kind's other
flows in the platform's logs.

### The product's own operations

The product's own operations carry their own code whichever flow's target they read through, and whatever repository
that flow is in:

| Code | Operation |
| --- | --- |
| `probe` | A target probe (`delivery-probe`), a person's or the scheduled one's. |
| `read` | A record read back from OSDU for its record page (`delivery-read`). |
| `explore` | The explorer's reads of a partition (`delivery-explore`): types, searches, a record, validation, dimension samples. |
| `preview` | A record rendered from its source row and sent nowhere (`delivery-preview`). |
| `values` | A mapping's value check (`delivery-check-values`). |
| `row` | A record's source row (`delivery-source`), which reads the ingestion tables. |
| `scope` | The values a run form offers for a parameter (`delivery-scope-values`), read from the ingestion tables. |
| `schema` | A reading of a partition's schemas, kept for the explorer's referenced-by. |
| `signin` | The local setup's check of a flow's OSDU client (`osdu-delivery-setup`, `sqlflow dev`). |

## The repository document

The repository's code is one small file, reviewed with the flows, that does nothing on its own:

| Key | Required | Meaning |
| --- | --- | --- |
| `documentType` | yes | `repository`. |
| `code` | yes | The repository's code ([the rule for a code](#the-rule-for-a-code)). |
| `description` | no | Free text. |

A run reads the file nearest its flow's file: `osdu-delivery.yaml` (or `osdu-delivery.yml`) in the flow's folder, else
in each folder above it in turn.

- **The repository's root is as far as it looks.** The folder holding `.git` (a folder in a clone, a file in a git
  worktree) is the last one read. Without a `.git`, the walk stops after 16 folders.
- **The nearest wins.** A folder may hold a document of its own, and the flows under it take that one, so a repository
  that keeps several sources apart can give each a code.
- **The name is matched exactly**, on every host: `OSDU-Delivery.yaml` is not the document.
- **One per folder.** A folder holding both `osdu-delivery.yaml` and `osdu-delivery.yml` is refused.

[The repository document](../flow/repository.md) lists every message it is refused with.

## Setting it up for a repository

1. Add `osdu-delivery.yaml` at the repository's root, beside `.git`:

    ```yaml
    documentType: repository
    code: wl
    description: The well log flows.
    ```

2. Validate the repository. The document is read as one of its own, and every flow under it with it:

    ```text
    $ sqlflow validate .
    OK      osdu-delivery.yaml  (repository 'wl')
    OK      welldb\flows\welldb-welllog-05-dimensions.yaml  (dimension 'welldb-welllog-05-dimensions')
    2 valid, 0 broken of 2 document(s) under <folder>.
    ```

3. Optionally, give a flow an operation code of its own (`telemetry.operation`). Do it in a commit that does not also
   rename the flow: the repository sync pairs a renamed flow with its ledger by its document, and a changed document
   under a new name is another flow ([renaming a pipeline](renaming-a-pipeline.md)).
4. Commit, push and sync the repository: the Repositories page, or `sqlflow repos sync <name>`. A node runs a flow from a
   checkout of the run's commit, so runs started after the sync carry the code.
5. Run a flow. The run log's first lines say what its requests carry, and which document gave the code:

    ```text
    2026-10-29 09:13:38.658Z INFO  run   dimension flow 'welldb-welllog-05-dimensions': plan (parameters: operation plan) requested by cli:jane@host, run 01a186ef-b444-7732-bb2e-9a7f4dfd4767
    2026-10-29 09:13:38.658Z INFO  run   OSDU requests of this run carry correlation-id osdu-delivery-wl-dim-01a186ef-b444-7732-bb2e-9a7f4dfd4767, the repository's code from /repos/estate/osdu-delivery.yaml
    ```

6. Filter the platform's logs on that value ([finding a run](#finding-a-run-in-the-platforms-logs)).

## Which requests carry it

### Inside a run

Every flow kind's executor opens the run's value as the first thing it does, before it reads a source or sends a
request, and closes it when the run ends. Everything the run sends carries it:

- searches, record writes, a record's tries and retries, and its read-backs;
- undoing what an unfinished try left, removal chunks, reversal pages and source removals;
- workflow triggers and their XCom reads, and the Reservoir DDMS's ETP session;
- a product operation the run calls, which keeps the run's value rather than taking its own.

Each part of a delivery split over nodes is a run of its own and carries its own id. A record's attempt names the value
its requests carried.

The schema index is the exception: its reading of a partition's schemas serves every later ask, whichever run or
explorer ask started it, so it always carries a value of its own, `osdu-delivery-schema-<id>`.

### Outside a run

A product operation opens its own value, `osdu-delivery-<operation>-<id>`, with a new id each time. Engine work reached
outside a run's scope carries its kind's code and a new id: `osdu-delivery-dlv-<id>` for a delivery's tries, undos,
removal chunks and reversal pages, `osdu-delivery-rmv-<id>` for a source removal. Any other request carries
`osdu-delivery-<id>`, a new id per request, so no request to OSDU goes without one.

### The services and transports

| Transport | Carries |
| --- | --- |
| The OSDU services a flow's client calls: storage, search, schema, file, dataset, legal, workflow and the DDMSs | `correlation-id`, set last, over any header of that name |
| The Reservoir DDMS's ETP session | `correlation-id` on its WebSocket upgrade |
| The Airflow REST API behind the Workflow service (XCom reads) | `correlation-id` |
| The production historian | `correlation-id`, and the same value in `trace-id`, the header its contract logs a request under |
| The setup check's read of a legal tag | `correlation-id: osdu-delivery-signin-<id>` |
| The token endpoint | Nothing: it is not OSDU. |
| A location a service signed (a landing zone, a dataset's retrieval URL, an object store a DDMS hands out) | Nothing: the request carries its own authorisation and none of the flow's headers, since some object stores refuse a request that also carries a bearer. |

## What is refused

A flow cannot set the header itself. A flow's operation code or header that breaks the rules is refused wherever the
flow's document is read; a repository document that breaks them is refused by `sqlflow validate` and fails every run
under it.

| Problem | Message |
| --- | --- |
| A `correlation-id` in a `headers` block (`source.headers`, `target.headers`, `target.airflow.headers`) | `flows/welldb-retrieval-01-wellbores.yaml: source.headers names 'correlation-id', which every request of a run carries as the run sets it: osdu-delivery, the code of the repository (osdu-delivery.yaml), the flow's operation code (telemetry.operation) and the run's id. Remove the header.` |
| An operation code that breaks the rule | `flows/welldb-welllog-03-delivery.yaml: telemetry.operation 'Header-Log' is not an operation code: a lower-case letter, then lower-case letters and digits, at most 16 in all and no hyphen, so the correlation id splits on its hyphens. Leave it out to take the kind's own, 'dlv'.` |
| A repository document with no code | `osdu-delivery.yaml: code is required: the repository's code, which every run of its flows carries in its correlation id.` |
| A repository code that breaks the rule | `osdu-delivery.yaml: code 'WL' is not a repository code: a lower-case letter, then lower-case letters and digits, at most 16 in all and no hyphen, so the correlation id splits on its hyphens.` |
| A key the repository document does not take | `osdu-delivery.yaml: invalid YAML at line 3, column 1 - Property 'name' not found on type 'SqlFlow.Delivery.Documents.RepositoryYaml'.` |
| A repository document under another name | `repository.yaml: a repository document is found by its name, walking up from each flow's folder; name the file osdu-delivery.yaml.` |
| Both names in one folder | `<folder> holds both osdu-delivery.yaml and osdu-delivery.yml; a folder holds one repository document. Remove one.` |

A run of a flow under a document it cannot read fails before it sends anything, with the document's message, and
`sqlflow validate` refuses the flow for it:

```text
$ sqlflow validate welldb/flows/welldb-welllog-05-dimensions.yaml
ERROR  welldb/flows/welldb-welllog-05-dimensions.yaml: ../../osdu-delivery.yaml: code 'WL' is not a repository code: a lower-case letter, then lower-case letters and digits, at most 16 in all and no hyphen, so the correlation id splits on its hyphens.
```

## In the editor

The GUI's YAML editor and the MCP server's `validate_flow` know both documents from the key census: `telemetry` and
`telemetry.operation` in every flow kind's file, and the repository document's own, `keys.repository.json`
([the key census](../../census/README.md)). A key's hover says what it does, a missing `code` is flagged, and a key the
loader would refuse, such as `telemetry.bogus`, or a `name` in `osdu-delivery.yaml`, is marked as an error. A
`telemetry` block in a mapping is refused too, since a mapping is not a run. The census checks keys, not values: the rule
for a code and the file's name are checked by `sqlflow validate` and by the run.

## Where the value is kept

| Where | What it shows |
| --- | --- |
| The run log and live trace | The run's first lines say what its requests carry, and which document gave the repository's code. |
| A record's attempt in the ledger | `correlationId` in its `ResultJson`: what the try's requests carried ([the ledger](ledger.md#osduattempt-every-try-and-every-decision-that-changed-the-record), [`osdu.Attempt`](../tables/attempt.md)). The record's Timeline and `sqlflow records show` show it. |
| A refused request's error | `(correlation-id <value>)`: the id the service answered with, else the one sent. |
| An issue's pattern | The whole value becomes `<id>`, so the same refusal in two runs is one issue ([issues](record-lifecycle.md#issues)). |
| An explorer or read-back answer | `correlationId`, the value its reads carried. |

A flow with no repository document above it says so in its run log instead:

```text
INFO  run   OSDU requests of this run carry correlation-id osdu-delivery-dim-01a186ef-b444-7732-bb2e-9a7f4dfd4767; no osdu-delivery.yaml in the flow's folder or above it names the repository's code
```

The value is kept in no column of its own: a run is listed by its id, and an attempt holds its value in its JSON.

## Finding a run in the platform's logs

On Azure, OSDU's services read the header through the platform's Azure library (`os-core-lib-azure`):

- each request's `correlation-id` goes on every log line written while the service handles it (`Slf4jMDCFilter`);
- the transaction log records it beside the partition and the content type, the only request headers it records
  (`TransactionLogFilter`);
- the messages the service publishes for others (the indexer, the workflow engine) carry it on
  (`PubSubAttributesBuilder`, `ServiceBusPublisher`).

A header of any other name would not reach those logs, which is why the value is carried in this one. Filter the logs on
the value or a prefix of it:

| To find | Filter on |
| --- | --- |
| One run | The whole value, `osdu-delivery-wl-dim-01a186ef-b444-7732-bb2e-9a7f4dfd4767`, or only the run's id |
| Every run of one repository | `osdu-delivery-wl-` |
| Every run of one flow, or of a kind's flows | `osdu-delivery-wl-dim-` |
| The product's own operations | `osdu-delivery-explore-`, `osdu-delivery-probe-`, ... |
| Everything OSDU Delivery sent | `osdu-delivery-` |

From the product's side, a failed try on a record's Timeline names the value its requests carried, so it leads to what
the service logged for it. From the platform's side, the id at the end of a value leads back to the run.

## From a log line to the run

Anything holding the id will do: the whole value as a log shows it, the bare run id, or a log line that quotes either.

| Where | What to do | What it gives |
| --- | --- | --- |
| The search | Paste it into the workbench's search. | The run, with every run of its execution; a row opens the run's page. |
| The **Runs** page | Paste it into the box beside the filters. | That run alone, whichever filters are set and however old it is. |
| The MCP server | `delivery_run`, with the value as `id`. | The run, its execution and the plans it registered, each with its link. |
| The API | `GET /api/v1/delivery/runs/lookup?id=<value>` | The same answer as the MCP server's ([the API](api.md)). |

Each part of a delivery split over nodes sends under its own id, so a log shows several values for one execution. Any of
them leads to all of them: the run that coordinated the parts first, then each part, with the one you pasted marked. The
platform's logs cannot group those values themselves, since only a prefix they share with the flow's other runs joins them.

An id no run has says so rather than coming back empty. That is how an explorer read or a probe answers, since a request
sent outside a run carries an id of its own that nothing here records (`osdu-delivery-explore-<id>`). The same answer comes
for a run of another environment, and for a run whose history has been removed.

## What changed for existing flows

- **A repository without a document runs as it did.** Its runs' value leaves the repository's part out
  (`osdu-delivery-<operation>-<run id>`), and the run log says so. Add the document to put the code in.
- **Every OSDU flow kind runs from a checkout** of its repository at the run's commit, since every run reads the
  document. Retrieval and inventory flows, and cache flows holding no dictionary, ran from a snapshot of their one file
  before ([which kinds need the repository](../flow/overview.md#which-kinds-need-the-repository)).
- **A `correlation-id` in a flow's `headers` is refused.** A flow that set one no longer loads until the header is
  removed: the run sets the value now.
- **A flow that names no operation code keeps its document hash**, so the repository sync pairs a renamed flow with its
  ledger as before ([renaming a pipeline](renaming-a-pipeline.md)).
- **Attempts written before keep their bare GUID.** An issue's pattern turns either into `<id>`, so they group as before.

## Troubleshooting

| What you see | Why | What to do |
| --- | --- | --- |
| The run log says `no osdu-delivery.yaml in the flow's folder or above it` | No document between the flow's folder and the repository's root, or one under another spelling (`OSDU-Delivery.yaml`). | Add `osdu-delivery.yaml` at the root, named exactly. |
| The value carries another repository's code than expected | A nearer document, in a folder between the flow and the root, wins. | The run log names the document it read; move or remove the nearer one. |
| Runs still carry the old value after the document was added | The run ran a commit synced before the document. | Sync the repository; runs started after carry the code. |
| A run fails before it sends anything, with a message naming `osdu-delivery.yaml` | The document cannot be read. | Fix what the message names; `sqlflow validate .` shows it ([what is refused](#what-is-refused)). |
| A flow fails to load: `names 'correlation-id'` | The flow sets the header in a `headers` block. | Remove the header. |
| A request in the platform's logs has no `osdu-delivery-` value | A request to a signed location or the token endpoint, which carry none, or a request another client sent. | Look for the run's other requests around it by time and path. |
| A refused request's error names an id that is not `osdu-delivery-...` | The service answered with a correlation id of its own, and the error names what the service answered. | The attempt's `correlationId` is what was sent. |

## Why it is built this way

[Decision 0037](../../wiki/decisions/0037-request-correlation.md) records the reasons and the alternatives. In short:

- **`correlation-id`**, because it is the header the platform's Azure library puts on every log line of a request; any
  other name would be sent and never logged.
- **The run's id**, because the platform's operators trace executions: one value per run groups everything it sent,
  where a value per try could not be grouped from the platform's side.
- **The repository's code in one document at its root**, versioned and reviewed with the flows, rather than repeated in
  every flow or kept in the control plane's configuration outside git.
- **No refusal for a missing document**, so no estate stops until it adds one.

## Where it is built

| Piece | Code |
| --- | --- |
| The value, the rule for a code, and the scope a value is open in | `osdu/src/SqlFlow.Delivery/Http/OsduCorrelation.cs` |
| A run's value and its run log line, opened first by each kind's executor | `osdu/src/SqlFlow.Delivery/Engine/RunCorrelation.cs` |
| A run found from a value or an id, and the search's runs category | `osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryRunLookup.cs`, `RunSearchContributor.cs` |
| Finding and reading the repository document | `Documents/RepositoryDocuments.cs`, `Documents/RepositoryDocumentKind.cs`, `Model/RepositoryDefinition.cs`, `Documents/CompanionFiles.cs` |
| The kinds' operation codes | `Model/FlowTelemetry.cs` |
| `telemetry.operation` and the header refusal | `Documents/DeliveryDocumentLoader.cs` |
| Putting the value on requests | `Engine/Protocols/OsduHttpClient.cs`, `Engine/Protocols/Etp/EtpConnection.cs`, `Engine/Workflows/WorkflowXCom.cs`, `Engine/Protocols/Ddms/ProductionTimeSeriesShape.cs`, `osdu/src/SqlFlow.Delivery.Setup/OsduSignIn.cs` |
| The product's operations' codes | `Engine/Operations/DeliveryOperations.cs` and each operation, `Engine/Search/PartitionSchemaIndex.cs` |
| Naming the value in errors and issues | `Http/HttpExecutor.cs`, `Ledger/ProblemSignature.cs` |
| Keeping existing flows' document hash | `Catalog/DeliveryDocumentHash.cs` |
| The editor's and `validate_flow`'s key checks | `osdu/docs/census/keys.repository.json` and `telemetry` in each flow kind's census, registered in `osdu/gui/src/module.tsx` and `osdu/hosts/osdu-delivery-mcp/src/docs.rs` |
| The tests | `osdu/tests/SqlFlow.Delivery.Tests/RequestCorrelationTests.cs` |

Paths without a root are under `osdu/src/SqlFlow.Delivery/`.

## See also

- [The repository document](../flow/repository.md): its keys, where a flow finds it, and its refusals.
- [What every OSDU document shares](../flow/overview.md#what-every-osdu-document-shares).
- [The delivery ledger](ledger.md) and [what a run's trace says](run-trace-and-metrics.md).
- [Decision 0037](../../wiki/decisions/0037-request-correlation.md): why every request carries the run's correlation id.
