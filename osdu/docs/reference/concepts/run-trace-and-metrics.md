---
id: delivery-concept-run-trace-and-metrics
title: "What an OSDU run's trace says, the SqlFlow.Delivery metrics, and probing delivery targets on a schedule"
type: concept
summary: "The steps and bounded allowances of an OSDU run's trace, the SqlFlow.Delivery meter and its exporters, and the scheduled target probe."
keywords:
  - run trace
  - live trace
  - run log
  - trace steps
  - progress line
  - trace allowances
  - metrics
  - opentelemetry
  - otlp
  - azure monitor
  - dotnet-counters
  - osdu_delivery.records
  - target probe
  - alerting
  - probe in the audit trail
  - metrics export
related:
  - concept-run-artifacts
  - concept-control-plane
  - delivery-concept-ledger
  - delivery-concept-control-plane
  - delivery-concept-environment-variables
  - delivery-guide-operations-runbook
  - delivery-cli-run
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Engine/RunTrace.cs
  - osdu/src/SqlFlow.Delivery/Engine/RunTraceGate.cs
  - osdu/src/SqlFlow.Delivery/Engine/RunHttpTrace.cs
  - osdu/src/SqlFlow.Delivery/Engine/RunArtifacts.cs
  - osdu/src/SqlFlow.Delivery/Engine/RunLogLoggerFactory.cs
  - osdu/src/SqlFlow.Delivery/Engine/FlowRuntime.cs
  - osdu/src/SqlFlow.Delivery/Engine/DeliveryExecutor.cs
  - osdu/src/SqlFlow.Delivery/Engine/Worker/DeliveryWorker.cs
  - osdu/src/SqlFlow.Delivery/Http/IHttpObserver.cs
  - osdu/src/SqlFlow.Delivery/Http/HttpExecutor.cs
  - osdu/src/SqlFlow.Delivery/Diagnostics/DeliveryMetrics.cs
  - osdu/src/SqlFlow.Delivery.Telemetry/DeliveryTelemetry.cs
  - osdu/src/SqlFlow.Delivery.Telemetry/TelemetryOptions.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Background/ScheduledTargetProbeService.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Configuration/DeliveryModuleOptions.cs
  - osdu/src/SqlFlow.Delivery/Engine/Operations/DeliveryOperations.cs
---

# What an OSDU run's trace says, its metrics, and watching the targets

Every run of an OSDU flow kind (delivery, retrieval, cache, assertion, dimension, inventory, removal) leaves the same artifacts as
SQLFlow's own kinds: a `run.json` with the result and the event timeline, a `run.log`, and a `trace.sql`, which is empty
because these kinds generate no SQL of their own. On a node its lines stream to the control plane as the live trace the
GUI's trace panel and `sqlflow trigger --follow` show. Those artifacts, the log levels and the trace endpoints
(`GET /api/v1/runs/{runId}/trace`, `/trace/text`, `/trace/stream`) are SQLFlow's, documented in
[run artifacts](../../../../sqlflow/docs/reference/concepts/run-artifacts.md) and
[the control plane](../../../../sqlflow/docs/reference/concepts/control-plane.md).

This page covers what an OSDU run writes there, how much of it, the module's metrics, and the scheduled probe of the
delivery targets.

## The steps of a run's trace

Each line is filed under the step that said it, which is what the engine was doing rather than the class that logged it:

| Step | What it says |
| --- | --- |
| `run` | The run and who asked (`delivery flow '<name>': <operation> (parameters: ...) requested by <actor>, run <id>`); the render context (`Rendering with mapping <name@version> (template <version>) and <cache>; ids are minted in partition <partition>.`); the route (`<operation> by the <route> route`); whether the legal service accepts the mapping's legal tags; where each `${env:NAME}` the run resolves takes its value, once per reference when it is first resolved (stage `run.reference`: `Reference OSDU_URL takes its value from the configuration the control plane supplied with this run.`, `... from this node's environment.`, or `... from this node's environment: the supplied configuration hands it there.`), names and origins only, never a value; each interface of a source starting; recovered leases, fan-out members, undos; how the run ended. |
| `source` | The ingestion table opened, the window fixed and the candidate records in it. |
| `plan`, `intake` | The submission being planned, the preflight's warnings, records held with their reason, the submission's counts. |
| `deliver` | Batches claimed and finished, and the progress line: `Delivering: <settled> of <planned> planned record(s) settled after <time> (<rate> a second): <n> delivered, <n> already in OSDU, <n> to try again, <n> held, <n> failed; working on <batch>.` |
| `verify` | What a verify run compared and the drift it found. |
| `record` | The delivery events as they complete: a submission, a batch, a verify (information level), and each record's event (debug level, within the allowances below). |
| `target`, `search` | What a route's protocol, or a render's search of OSDU, says while it works. |
| `http` | Each call to OSDU at debug level (`<METHOD> <url> answered HTTP <status> in <time>`), and each retry: `<METHOD> <url>: <cause> on attempt <n> of <max>; trying again in <wait>.` |

A described record also gets its own lines on its way: `Sending <record> to <id>: <what>` and
`Delivered <record> as <id> version <n> (<phase>) in <time>`, or why it was held, failed or put back for a retry.
Warnings and errors keep their level on the event and are prefixed `warning:` and `error:` in `run.log`.

## Bounded, whatever the run's size

A delivery run moves millions of records, and every line of its trace is streamed, kept in the run's events and stored
in the catalog. So every line the engine logs while it serves a run passes one gate, whichever part logged it, and what a
run writes is bounded by these allowances:

| Allowance | What the trace carries |
| --- | --- |
| 20 described records | The first 20 records a run meets are described in full, each time it meets them again (the plan line, the send, each step and call, the outcome). Nothing is said about any other record on its own. |
| 100 lines per step | Outside the described records, each step writes at most 100 lines of its own. |
| 100 problems | The run names at most 100 warnings about records (held, failed, retried, drift). The run's own warnings (`run` step) take from that step's lines instead, so record problems never crowd them out. |
| 50 retries | At most 50 retried calls are named. |
| 100 references | Each `${env:NAME}` the run resolves is named once with where its value came from; past 100 the trace says so once (`This run resolves more than 100 references; the trace says where the ones named above take their values, and names no other.`). These lines do not take from the `run` step's 100. |
| Paced progress | Progress lines come every 15 seconds for the first two minutes, every minute up to an hour, then every five minutes, from run-level totals rather than per batch. |

Errors are always written. When an allowance runs out the trace says so once, so it shows where its lines of that sort
stop, for example:
`The first 20 records of this run are on its trace in full; the rest show in the progress lines, and every record's history is in the ledger.`
Nothing the trace leaves out is lost: every record's attempts, with each step and status, are in the
[ledger](ledger.md) and on the record's page.

A URL on the trace never carries its user info or query string (a signed upload URL keeps its credential there), and
messages are redacted before they are written.

## Metrics

The engine publishes its telemetry on the .NET metrics API under the meter `SqlFlow.Delivery`:

| Instrument | Unit | Tags | Measures |
| --- | --- | --- | --- |
| `osdu_delivery.records` | `{record}` | `flow`, `partition`, `route`, `outcome` | Delivery tries settled: `delivered`, `unchanged`, `retry`, `held`, `failed`; and `waiting`, a record left waiting for a record it refers to (not a try). |
| `osdu_delivery.record.duration` | s | `flow`, `partition`, `route`, `outcome` | How long a try of one record took, from its claim to its outcome. |
| `osdu_delivery.http.requests` | `{request}` | `method`, `host`, `result` | Call attempts to OSDU services and their storage. `result` is `2xx` to `5xx`, or `transport`, `timeout`, `refused` (the URL guard), `cancelled` or `error`. |
| `osdu_delivery.http.request.duration` | s | `method`, `host`, `result` | How long an attempt took, redirects and response body included. |
| `osdu_delivery.http.retries` | `{retry}` | `method`, `host`, `reason` | Calls repeated after a passing failure: a status code, `transport` or `timeout`. |
| `osdu_delivery.probes` | `{probe}` | `flow`, `partition`, `interface`, `outcome` | Target probes settled, scheduled or asked for: `reachable`, `unreachable`, `error`, `cancelled`. |

`partition` is the partition the record's ledger is kept in (empty when it is not known), so a flow delivering to several
partitions reads per partition or as one. These are rates to watch and alert on; the delivered, pending, held and failed
counts the GUI and the CLI show come from the ledger, never from here. On any process,
`dotnet-counters monitor --counters SqlFlow.Delivery -p <pid>` reads them whatever is configured.

### Where the metrics go

Nothing leaves the process until a deployment names an exporter. The control plane reads the `Osdu:Telemetry` section; a
node reads the same settings from its environment. Both hosts register the export the same way, as a hosted service that
starts with the host (a node starts it before it takes work, and stops and flushes it after its drain), and the host
logs where the metrics go:
`Metrics on the meter SqlFlow.Delivery are exported to <Exporter> every <n>s as service <name>.`, or
`Metrics are published on the meter SqlFlow.Delivery and exported nowhere (Osdu:Telemetry:Exporter is none); 'dotnet-counters monitor --counters SqlFlow.Delivery' reads them on this process.`
A refused OTLP endpoint is named without its user info or query.

| Setting (`Osdu:Telemetry:`) | Node variable | Default | Meaning |
| --- | --- | --- | --- |
| `Exporter` | `OSDU_TELEMETRY_EXPORTER` | `None` | `None`, `Otlp`, `AzureMonitor` or `Console` (case does not matter). |
| `OtlpEndpoint` | `OSDU_TELEMETRY_OTLP_ENDPOINT` | the `OTEL_EXPORTER_OTLP_*` defaults | `http://collector:4317` for grpc, `http://collector:4318/v1/metrics` for httpprotobuf. |
| `OtlpProtocol` | `OSDU_TELEMETRY_OTLP_PROTOCOL` | `grpc` | `grpc` or `httpprotobuf`. |
| `OtlpHeadersRef` | `OSDU_TELEMETRY_OTLP_HEADERS` | none | `key=value` headers a hosted backend takes its key in, as a secret reference. |
| `AzureMonitorConnectionRef` | `OSDU_TELEMETRY_AZURE_MONITOR_CONNECTION` | none | The Application Insights connection string as a secret reference; required for `AzureMonitor`. |
| `ExportSeconds` | `OSDU_TELEMETRY_EXPORT_SECONDS` | `60` | Seconds between exports; at least 5. |
| `ServiceName` | `OSDU_TELEMETRY_SERVICE_NAME` | `osdu-delivery` | What a backend groups the metrics under. |
| `ServiceInstanceId` | `OSDU_TELEMETRY_SERVICE_INSTANCE` | the machine name | Tells replicas apart. |

A setting that is wrong stops the host with a message naming it, for example
`Osdu:Telemetry:OtlpHeadersRef must be a secret reference (${env:NAME} or ${keyvault:NAME}), not the value itself.` or,
on a node, `OSDU_TELEMETRY_EXPORTER is '<value>'; it is one of none, otlp, azuremonitor, console.` A one-shot CLI command
exports nothing.

```text
# A node exporting to an OpenTelemetry collector every 30 seconds
OSDU_TELEMETRY_EXPORTER=otlp
OSDU_TELEMETRY_OTLP_ENDPOINT=http://collector:4317
OSDU_TELEMETRY_EXPORT_SECONDS=30
OSDU_TELEMETRY_SERVICE_NAME=osdu-delivery-node
```

### What to alert on

- A rising share of `held` or `failed` outcomes per flow and partition.
- `5xx`, `transport` and `timeout` results per host, and any `refused` result: a URL the guard would not let a node reach
  ([environment variables](environment-variables.md)).
- A rising `waiting` share: children are delivered faster than the records they refer to; the flow's waiting count says
  whether they move.
- Any `unreachable` probe of a flow that should be delivering, and a run of `error` probes.

## Watching the targets

An OSDU can stop answering for reasons no run reveals until one is due: a rotated secret, a revoked entitlement, a moved
gateway path, maintenance. The GUI's **Probe target** (`POST /api/v1/delivery/flows/{pipelineId}/probe`) asks one
interface's target, under the flow's own credentials, whether it still answers; it calls the route's probe path (the
service's info endpoint) and writes nothing to OSDU. The API and the MCP tool ask the same route. The same probe can
run on a schedule, so nobody has to press it, and every probe, scheduled or asked for, takes one path: it is recorded
as a `probe` activity in the interface's ledger under who asked (`user:<subject>` for the button, the API and the MCP
tool; `service:schedule` for the schedule) and counted on `osdu_delivery.probes`. A flow whose ledger cannot be placed
is not probed, since nothing could record it (the API answers 422 `The probe could not be recorded`), and a probe its
caller stopped waiting for is recorded and counted `cancelled`.

The schedule is off unless a deployment turns it on, because each pass costs a token exchange and a live request for
every interface it covers. It is configured under `Osdu:TargetProbe` on the control plane
([the control plane](control-plane.md#configuration)): `Enabled`, `IntervalMinutes` (15, never under 5), `Pipelines`
(names, comma separated; empty probes every active delivery pipeline) and `MaxPerPass` (200).

Each pass:

1. **Closes what was left open.** A probe activity still `running` 15 minutes after it started, scheduled or asked for
   (the host stopped while it ran), is closed in every ledger; a younger one is left alone. Each probe also closes its
   own interface's stale ones before it starts, so this happens with the schedule off too.
2. **Probes each interface** of each covered flow, four at a time, through the same in-process path as the button, in the
   partition the interface delivers to. Each probe is recorded as an activity of kind `probe` by `service:schedule` and
   closed with what it found, and counted on `osdu_delivery.probes`.

The last result per flow and interface, scheduled or asked for, is therefore in the audit trail (the **Audit trail**
page, action `probe`, or `GET /api/v1/delivery/activities?kind=probe`). A probe from the button still answers in the
request. An `error` outcome says the probe itself could not run (the flow file is not where its repository was synced,
a credential will not resolve, the flow no longer parses), not that the OSDU is down.

Each probe logs its outcome under the category `SqlFlow.Delivery.ControlPlane.TargetProbes`:
`Probe of <flow> by <actor>: <summary>`, `Probe of <flow> by <actor> settled <outcome>: <summary>` when it did not
reach the target, and `Probe of <flow> by <actor> was not run, because it could not be recorded: <reason>`.

## See also

- [SQLFlow: run artifacts, logging and diagnostics](../../../../sqlflow/docs/reference/concepts/run-artifacts.md)
- [The ledger](ledger.md)
- [The control plane](control-plane.md)
- [Operations runbook](../guides/operations-runbook.md)
