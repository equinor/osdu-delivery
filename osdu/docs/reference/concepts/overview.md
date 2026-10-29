---
id: delivery-concept-overview
title: "What OSDU Delivery is: SQLFlow extended to publish records into OSDU, with a ledger of every record"
type: concept
summary: "OSDU Delivery as an extension of SQLFlow: the pre, ing and delivery flow chain, the ledger that makes every record traceable, and where to look."
keywords:
  - osdu delivery
  - sqlflow extension
  - what is osdu delivery
  - delivery chain
  - pre ing delivery
  - publish records to osdu
  - ledger
  - traceability
  - record history
  - where to start
  - gui pages
  - osdu verbs
related:
  - delivery-concept-architecture
  - delivery-flow-overview
  - delivery-guide-getting-started
  - delivery-concept-ledger
  - delivery-concept-lineage
  - concept-architecture-and-execution
  - guide-getting-started
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Hosting/OsduDeliveryBranding.cs
  - osdu/src/SqlFlow.Delivery/Documents/DeliveryFlowKind.cs
  - osdu/src/SqlFlow.Delivery/Engine/DeliveryServices.cs
  - osdu/src/SqlFlow.Delivery/Engine/DeliveryExecutor.cs
  - osdu/src/SqlFlow.Delivery/Model/FlowDefinition.cs
  - osdu/src/SqlFlow.Delivery/Identity/DeliveryKey.cs
  - osdu/src/SqlFlow.Delivery/Identity/TargetId.cs
  - osdu/src/SqlFlow.Delivery/Ledger/ILedger.cs
  - osdu/src/SqlFlow.Delivery.Data/OsduDbContext.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryCliModule.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/DeliveryControlPlaneModule.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryEndpoints.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/RecordChain.cs
  - osdu/gui/src/module.tsx
  - osdu/hosts/osdu-delivery-mcp/src/lib.rs
---

# What OSDU Delivery is: SQLFlow extended to publish records into OSDU, with a ledger of every record

OSDU Delivery publishes subsurface records (wells, wellbores, well logs and the other OSDU kinds) into an OSDU platform,
and keeps every delivered record traceable: which source file and row it came from, which mapping and cache version
rendered it, every attempt to send it, the OSDU id and version it landed as, and every operator action taken on it. It
introduces itself as "OSDU Delivery, powered by SQLFlow", because that is what it is: SQLFlow with a module installed.
SQLFlow lands and loads the source data; the module renders it, delivers it and keeps the ledger.

Use this page to place the product. [The architecture](architecture.md) shows how the module meets SQLFlow, and
[your first delivery](../guides/getting-started.md) walks one record from a file to OSDU.

## SQLFlow underneath, the module on top

Everything SQLFlow is stays as it is, and its documentation applies unchanged. The module adds what delivering to OSDU
needs, and nothing else.

| SQLFlow provides (its documentation) | OSDU Delivery adds (this corpus) |
| --- | --- |
| Flow documents in git, file flows and `ing` flows ([flow overview](../../../../sqlflow/docs/reference/flow/overview.md)) | Eight flow kinds and three companion documents ([OSDU documents](../flow/overview.md)) |
| The catalog, the repository sync, schedules and the run queue ([control plane](../../../../sqlflow/docs/reference/concepts/control-plane.md)) | The `osdu` schema beside the catalog: the ledger, templates, caches, partitions ([architecture](architecture.md)) |
| Compute nodes that pull runs ([architecture and execution](../../../../sqlflow/docs/reference/concepts/architecture-and-execution.md)) | The executors that run the OSDU kinds on those nodes |
| Lineage and its waves ([lineage graph](../../../../sqlflow/docs/reference/concepts/lineage-graph-and-plan.md)) | OSDU types, mappings, cache types and dimensions as lineage nodes ([lineage](lineage.md)) |
| The `sqlflow` command line ([run](../../../../sqlflow/docs/reference/cli/run.md), [validate](../../../../sqlflow/docs/reference/cli/validate.md)) | The OSDU verbs: `check`, `preview`, `values`, `records`, `config`, `partition`, `cache`, `template`, `assertions`, `dimensions`, `inventory`, `removal` |
| The workbench GUI and the MCP server | The OSDU pages of the GUI, the delivery API under `/api/v1/delivery`, the `osdu-delivery-mcp` server |
| Secret references, users, tokens and scopes ([connections and secrets](../../../../sqlflow/docs/reference/concepts/connections-and-secrets.md)) | Nothing of its own: the module reads secrets and checks people the platform's way |

The `sqlflow` binary, the `SqlFlow.*` projects and the `SQLFLOW_*` environment variables keep their names on purpose.
The product's name is what the CLI banner, the GUI and the notifications carry.

## The chain: pre, ing, then the OSDU flow

Data reaches OSDU through SQLFlow's own flows. There is no drop manifest, no drop reader and no replica: three flows,
each with its own run, log and failure, ordered by lineage like any other flows.

```text
source files ──file flow──▶ pre.Wellbore, pre.v_Wellbore ──ing flow──▶ silver.Wellbore ──delivery flow──▶ OSDU
                                                                                              └──▶ the ledger
```

| Step | Flow | What it does |
| --- | --- | --- |
| 1 | A SQLFlow file flow (`welldb-wellbore-01-pre`) | Lands the source files into a `pre` table as text and refreshes its typed view `pre.v_Wellbore` |
| 2 | A SQLFlow `ing` flow (`welldb-wellbore-02-ing`) | Upserts the view into a keyed ingestion table, `OsduData.silver.Wellbore`, stamping `UpdatedDate_DW` on every row it inserts or changes |
| 3 | An OSDU delivery flow (`welldb-wellbore-03-delivery`) | Reads the rows changed since its last run, renders each with its pinned mapping, sends what renders differently, and records all of it |

The delivery flow reads the ingestion table through SQLFlow's own columns: `UpdatedDate_DW` drives its incremental read,
and the provenance columns the file flow wrote and the `ing` flow carried (`FileName_DW`, `RowNumber_DW`) become each
record's origin in the ledger. A delivered record therefore still points at the file and row it came from.

## What a delivery does

A delivery run plans, then delivers, through one engine on whichever node takes the run:

1. **Read what changed.** The rows of the ingestion table (and its child tables) whose `UpdatedDate_DW` moved since the
   last run, re-reading a short overlap below the watermark. The first run, and the first after the mapping, its
   template or the parameters changed, reads every row of the scope.
2. **Render.** Each row through the mapping the flow pins by `Name@version`, against the template that mapping pins
   (an OSDU schema saved in the module database) and the version of the partition's cache it reads. The record's OSDU id
   is minted from the delivery key, a UUID derived from the mapping's `dataset.system` and the row's key, so the same row
   always becomes the same record (`dev:master-data--Wellbore:<key>`); a mapping can make it from the key's own values
   instead.
3. **Decide.** Compare what the row renders to with what the ledger last delivered. Unchanged records are skipped,
   changed ones are queued ([change detection](change-detection.md)). The mapping was checked against its template
   before the first row rendered, and each document can be checked against its schema before it is sent
   ([preflight](preflight.md)).
4. **Deliver.** Send the queued records through the flow's route (the storage service, a DDMS, a manifest workflow and
   others, [routes](../flow/routes.md)), retrying what may succeed later and holding what cannot, and write one attempt
   per try.
5. **Verify** (an operation of its own). Read delivered records back from OSDU and compare versions with the ledger.

`plan` is the same run up to step 3, sending nothing. Concepts: [submissions](submissions.md),
[record lifecycle](record-lifecycle.md), [protocols](protocols.md).

## Traceability is the product

Every delivered record can be reconstructed from the ledger alone, in the module's `osdu` schema:

| Question | What answers it |
| --- | --- |
| Which source row is this? | The record's source key (`welldb:WB-0001`), its delivery key, and the origin file and row number |
| How did that row get into the table? | The record's chain: each change of its row in the ingestion table, the `ing` run that wrote it and the landing run that brought its file in, as the catalog's run history proves them |
| What rendered it? | The render context the record holds: the mapping reference and fingerprint, the template version, the cache version and the parameter values its document was rendered with, plus the cached entries that document used |
| What happened when it was sent? | One append-only attempt per try: the document hash it sent, what each step of the route answered, the OSDU version, the outcome and the error |
| What is it in OSDU? | The OSDU id and the version the last delivery landed as |
| Who did what to it? | The audit trail: every release, redelivery, removal, reversal and verification, with who did it and when |

Every count the GUI shows (delivered, pending, held, failed, deleted) is counted from the ledger's records, and record
search runs on indexes over it: by key, OSDU id, source file, or the values a mapping names under `dataset.identity`
(a wellbore name, say). No delivery path, retry or operator action bypasses the ledger. The details are on
[the ledger](ledger.md).

## More than delivery

The other five flow kinds deliver no records. A cache flow captures what mappings read, so lineage runs it before the
deliveries that read it; the other four read what OSDU holds and run after the deliveries that write it
([lineage](lineage.md)):

| Kind | Use it to |
| --- | --- |
| [Cache](../flow/cache.md) | Capture what mappings look values up in: OSDU reference data, your own lookup tables, dictionaries, dimensions |
| [Retrieval](../flow/retrieval.md) | Bring OSDU records back as JSON Lines files on the lake, for SQLFlow flows to load into tables |
| [Assertion](../flow/assertion.md) | Test what a partition holds once the data has landed, with a report per run |
| [Dimension](../flow/dimension.md) | Keep the distinct values of an attribute of OSDU records, cleaned for people to pick from |
| [Inventory](../flow/inventory.md) | Find every id a kind holds that no ledger accounts for, and remove what a flow allows |
| [Removal](../flow/removal.md) | Take out of OSDU, once, the records whose keys the source no longer holds, on a schedule, with a guard |

## Where to look

| Where | What you find there |
| --- | --- |
| The GUI, the menu | SQLFlow's pages and this product's side by side, grouped by what a person does there ([the menu](menu.md)). **Ledger**: Delivery (the overview), Records, Removals, Audit trail, Orphaned ledgers. **OSDU**: Explorer (what OSDU holds, read live), Search terms, Assertions, Inventories, Dimensions, Partitions. **Build**, **Delivery**: Mappings (with the mapping builder), Templates, Cache, Order. A title bar picker sets the partition every OSDU page reads ([GUI](gui.md)). |
| The GUI, a flow's pipeline page | Tabs per kind: Delivery, Records, Issues, Deletions, Submissions and Preview for a delivery flow, set among the page's Runs, Schedules and YAML by what each is about; Retrievals, Cache versions, Tests with History and Reports, Dimensions, Inventories, Removals for the others. Each kind adds its own fields to the trigger dialog. |
| The `sqlflow` command line | SQLFlow's verbs plus the OSDU verbs: `check` and `preview` before a run, `records` after one, `template`, `cache`, `partition` and `config` to set an estate up ([CLI pages](../cli/run.md)). |
| The API | Everything under `/api/v1/delivery`, on the control plane's authenticated routes ([API](api.md)). |
| An AI assistant | `osdu-delivery-mcp`: SQLFlow's MCP server with the delivery tools added. It answers from metadata (states, ids, counts, history, definitions), never from record content or source rows, and a `!` question from the data an approved AI context opens, with a read-only query ([MCP server](../guides/mcp.md)). |

## What it does not do

- It does not change where a flow reads or delivers. The source location, the OSDU endpoint and partition, the legal
  tags and ACLs and the credential references are the flow author's, in the flow and the mapping.
- It only reads the repository. Templates are files of the repository's template folders, which its sync records and
  every reader reads from the file; cache versions live in the module database, captured by cache flow runs.
- It does not read source files itself. Landing and keying are SQLFlow's file and `ing` flows.

## See also

- [Architecture: the module on SQLFlow](architecture.md)
- [The OSDU documents](../flow/overview.md)
- [Your first delivery, end to end](../guides/getting-started.md)
- [Which delivery problem maps to which shape](../../wiki/maps/pattern-catalog.md)
- [SQLFlow: getting started](../../../../sqlflow/docs/reference/guides/getting-started.md)
