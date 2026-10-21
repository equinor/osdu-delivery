# osdu/

**OSDU Delivery turns delivering data to OSDU from a development task into configuration.** Three things make that
possible: a drop-off zone that brings any source data into one shape (rows of a keyed table), a template that fixes the
target (a versioned OSDU schema the repository keeps in a template folder), and a cache that holds every lookup (OSDU reference data, and lookup tables from
files or databases, versioned per partition). A mapping connects the three, and one generic engine delivers what it
renders and keeps a ledger that accounts for every record it sends. Nothing in this folder is written for one kind of
data; the well logs in `samples/` are the worked example, not a special case. The pitch and a tour are in the
[repository README](../README.md).

## What is here

| Folder | What it holds |
| --- | --- |
| `src/SqlFlow.Delivery/` | The engine: the six flow kinds, mapping and rendering, the partition cache, change detection, the ten delivery routes, the ledger, the worker, verify, removal and reversal |
| `src/SqlFlow.Delivery.Data/` | `OsduDbContext` over the `osdu` schema, with its migrations and schema version |
| `src/SqlFlow.Delivery.ControlPlane/` | The delivery API under `/api/v1/delivery` and the module's background services |
| `src/SqlFlow.Delivery.Cli/` | The OSDU verbs of `sqlflow` (`check`, `preview`, `records`, `template`, `cache` and the rest) |
| `src/SqlFlow.Delivery.Setup/` | The local run's setup and check, which `dev.bat` and `./dev.sh` run: as `osdu-delivery-setup` and as `sqlflow dev` |
| `src/SqlFlow.Delivery.Search/` | The query strings sent to OSDU's search service, built and escaped canonically |
| `src/SqlFlow.Delivery.Telemetry/` | Where the module's metrics go: an OpenTelemetry export to OTLP, Azure Monitor or the console |
| `hosts/` | The control plane, worker and CLI hosts, the setup's host (`osdu-delivery-setup`), and `osdu-delivery-mcp`, the assistant's server with its tools for building and checking flows and mappings |
| `gui/` | The OSDU pages of the workbench, the mapping builder among them |
| `tests/` | The delivery, ledger, route, control plane and search suites |
| `specs/` | The OSDU service contracts every route is built from, pinned with their provenance (`specs/sources.json`), and an integration brief per service |
| `samples/` | The `welldb` sample estate (well logs with their curves, delivered to the Wellbore DDMS), the bundled OSDU schemas, and sample cache records |
| `deploy/` | Dockerfiles, Docker Compose, Kubernetes, Bicep, Terraform and Radix |
| `docs/` | The reference corpus, the wiki (decisions, patterns, narratives, incidents and maps) and the key census |
| `tools/` | `SampleData`, which generates the sample's synthetic well logs and lookup tables |

`sqlflow/` next to this folder is the engine underneath, vendored as a subtree and changed only through the extension
points [../docs/sqlflow-changes.md](../docs/sqlflow-changes.md) records; nothing here is copied from SQLFlow, and
`sqlflow/` only ever gains generic extension points, never OSDU code.

## Where the code came from

The code was copied from the previous implementation, `D:\Projects\eq\src\osdu-delivery` at commit `7377609`, taken from
the committed tree (no build output, no local files).

| Here | From |
| --- | --- |
| `src/SqlFlow.Delivery/` | `src/SqlFlow.Delivery/`: the delivery flow, retrieval, cache and assertion kinds, ledger, protocols, rendering, templates and mapping builder, OSDU cache, worker, verifier, removal, fan-out |
| `src/SqlFlow.Delivery.Data/DeliveryEntities.cs` | `src/SqlFlow.Catalog/DeliveryEntities.cs`: the ledger's entity model, to become `OsduDbContext` in the `osdu` schema |
| `src/SqlFlow.Delivery.ControlPlane/` | `src/SqlFlow.ControlPlane/Api/DeliveryEndpoints.cs`, `DeliveryTemplateEndpoints.cs`, `Background/CacheUpdateRolloutService.cs`, `Background/DataDefinitionsWarmupService.cs` |
| `src/SqlFlow.Delivery.Cli/DeliveryVerbs.cs` | `src/SqlFlow.Cli/DeliveryVerbs.cs`: the `check`, `cache` and `template` verbs |
| `gui/src/features/delivery/`, `gui/src/api/delivery.ts` | The OSDU GUI pages and their API client |
| `gui/e2e/` | The OSDU specs (seed, runs, pipelines, cache, templates and mapping builder) with the e2e setup and helpers |
| `tests/SqlFlow.Delivery.Tests/` | The delivery domain suites |
| `tests/SqlFlow.Delivery.ControlPlane.Tests/` | The module, plan run, record origin and template API suites, and the sample estate they use |
| `docs/` | `docs/delivery/` and the CLI reference of the delivery verbs (both since replaced by the reference corpus in `docs/reference/`) |
| `samples/welldb/` | The sample estate, laid out the way a repository is: one folder for the WellDB source, holding the well log chain's flows (pre, ingestion and delivery), the WellLog mapping, the template folder holding the bundled OSDU schemas its mappings fill, the lookups cache flow with the lookup tables' files and the flows that load them, the schedules, and the drop-off folder with five sample WellDB logs |

## What was left behind, and why

SQLFlow's own pre-ingestion and ingestion flows replace the way data used to arrive, so these were not copied:

- the drop manifest, its file encodings and the drop reader, and the disk-backed join of drop scopes;
- the CSV and JSON scope readers (`Formats/`), which SQLFlow's file flow provides;
- the replica (`Replica/`), whose typing, schema evolution and upsert SQLFlow's pre-ingestion and ingestion flows provide;
- the SQL Server source extraction into drops (`Engine/SqlSource/`), which an ingestion flow provides;
- inline drops, replica slices and origins, and known-state publishing;
- the drop-off area (endpoints, retention service, GUI page, API tests) and the submit-a-drop dialog;
- the sample drops (`samples/wells-welllog-03-header-delivery/out/`) and the sample drop generator (`tools/SampleDrop`);
- the tests and documents of all of the above.

## State

The module builds with SQLFlow and its suites pass: the delivery, ledger and route suites against fakes of the OSDU
services, whose every request is checked against the service's pinned contract, and the SQL Server suites against a
disposable database. The routes are documented in [docs/reference/flow/routes.md](docs/reference/flow/routes.md).

This build has run against a live OSDU: on 2026-09-17 against Azure Data Manager for Energy 0.29, partition `dev`, the
storage, file, manifest and ddms routes each delivered and were read back, verify and reconcile were exercised, and
every id created was removed at the reversible scope.
