---
id: delivery-cli-control-plane
title: "Control-plane verbs in OSDU Delivery: triggering, scheduling and following OSDU runs from a terminal"
type: cli-command
summary: "What sqlflow trigger, runs, schedules, pipelines, search, datasources tasks and health do against an OSDU Delivery control plane."
keywords:
  - control plane
  - trigger an osdu run
  - runs list --kind delivery
  - schedules create --operation
  - run result
  - resultjson
  - remote verbs
  - delivery-check-values
  - delivery-export-value-issues
  - module-databases readiness
  - osdu-partitions check
  - partition run value
  - direct database verbs
related:
  - cli-control-plane
  - delivery-cli-run
  - delivery-cli-config
  - delivery-concept-control-plane
  - delivery-concept-api
  - delivery-cli-records
  - delivery-concept-partitions
sourceRefs:
  - osdu/hosts/SqlFlow.Delivery.ControlPlane.Host/Program.cs
  - osdu/hosts/SqlFlow.Delivery.Cli.Host/Program.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/DeliveryControlPlaneModule.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Background/ConfiguredRunDispatcher.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Background/PartitionReadiness.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Background/DeliveryRunOutcomeDescriber.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/RecordSearchContributor.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryCliModule.cs
  - osdu/src/SqlFlow.Delivery/Engine/DeliveryRunPayload.cs
  - osdu/src/SqlFlow.Delivery/Engine/Operations/CheckValuesOperation.cs
  - osdu/src/SqlFlow.Delivery/Engine/Operations/ExportValueIssuesOperation.cs
  - osdu/src/SqlFlow.Delivery/Engine/Operations/DeliveryOperations.cs
  - osdu/src/SqlFlow.Delivery/Model/DeclaredPartition.cs
  - sqlflow/src/SqlFlow.Cli/Remote/RemoteVerbs.cs
  - sqlflow/src/SqlFlow.Cli/Remote/RemoteVerbs.Estate.cs
  - sqlflow/src/SqlFlow.Cli/Remote/RemoteContracts.cs
  - sqlflow/src/SqlFlow.ControlPlane/Api/RunEndpoints.cs
  - sqlflow/src/SqlFlow.ControlPlane/Api/SearchEndpoints.cs
  - sqlflow/src/SqlFlow.ControlPlane/Hosting/ControlPlaneHost.cs
  - sqlflow/src/SqlFlow.ControlPlane/Infrastructure/ModuleDatabaseVerification.cs
---

# Control-plane verbs in OSDU Delivery

OSDU Delivery's command line is `sqlflow`: SQLFlow's CLI with the OSDU verbs added. The control-plane verbs (`login`,
`trigger`, `runs`, `groups`, `schedules`, `repos`, `pipelines`, `search`, `datasources`, `nodes`, `health`, `doctor`)
are SQLFlow's, and signing in, the credential order, every subcommand and its output are documented in
[Control-plane verbs](../../../../sqlflow/docs/reference/cli/control-plane.md). OSDU Delivery's control plane
(`osdu/hosts/SqlFlow.Delivery.ControlPlane.Host`) is SQLFlow's control plane with the OSDU module composed in, so these
verbs work against it unchanged. This page covers what is different when they are pointed at OSDU flows.

## Two kinds of verbs in one binary

| Verbs | Talk to | Need |
| --- | --- | --- |
| SQLFlow's control-plane verbs (`trigger`, `runs`, `schedules`, ...) | The control plane's `/api/v1` | `--url` or `SQLFLOW_URL`, and a token (`sqlflow login`, `--token` or `SQLFLOW_TOKEN`). |
| The OSDU verbs (`check`, `preview`, `values`, `records`, `config`, `partition`, `cache`, `assertions`, `dimensions`, `inventory`, `removal`; `template` reads and writes files and needs neither) | The module's database directly | `--db` or `SQLFLOW_CATALOG_DB`, the catalog database, which holds the `osdu` schema. None of them calls the control plane. |

So a terminal that can reach the control plane but not the database can trigger and follow OSDU runs, but cannot read a
ledger with `sqlflow records`; it reads records through the GUI or the delivery API instead
([The delivery API](../concepts/api.md)).

## Triggering an OSDU run

`trigger` takes the kind arguments of the flow, the same three a local `sqlflow run` takes, and sends them with the run:

| Flag | Meaning |
| --- | --- |
| `--operation <name>` | Which of the kind's operations the run performs (`deliver`, `plan`, `verify`, ... for a delivery flow; `refresh` for a cache flow). Omitted takes the kind's default. |
| `--set name=value` | A run value, repeatable: a flow parameter, or `partition`, which names the OSDU partition the run targets. Repeating a name gives a delivery flow's parameter that `source.record.scope` binds several values, read as one scope; any other parameter, and `partition`, takes one. |
| `--payload <json>` or `--payload @<file>` | One JSON object whose shape the flow kind owns. |

```bash
# plan one log source in the test partition, change nothing, and follow the trace
sqlflow trigger --repo welldb --flow welldb-welllog-03-delivery --operation plan \
  --set partition=test --set logSource=WIRELINE --follow

# deliver it
sqlflow trigger --repo welldb --flow welldb-welllog-03-delivery --set partition=test --set logSource=WIRELINE

# refresh the lookup cache of every partition the cache flow serves, one after another
sqlflow trigger --repo welldb --flow welldb-lookups-00-cache --set 'partition=*'
```

What each operation does and what a delivery payload carries is in [Running an OSDU flow](run.md); how a run settles
its partition is in [Partitions](../concepts/partitions.md). The partition is taken off the run's values before the
flow's parameters are read, so it never moves a watermark or a submission's scope.

The control plane attaches the central configuration to every delivery, cache, retrieval, assertion, dimension,
inventory, removal and download run it queues, from a trigger, a schedule or a run group alike, so the node resolves `${env:NAME}` from it first
([sqlflow config](config.md)). A local `sqlflow run` gets none of it.

## Schedules

`schedules create` carries `--operation` and `--set` values into every fire of the schedule, so an operation or a
partition is part of the schedule rather than something to remember:

```bash
# the hourly delivery to test, and a nightly verify of the same flow as a schedule of its own
sqlflow schedules create --repo welldb --flow welldb-welllog-03-delivery --cron "0 * * * *" \
  --set partition=test --set logSource=WIRELINE
sqlflow schedules create --repo welldb --flow welldb-welllog-03-delivery --cron "0 3 * * *" \
  --operation verify --set partition=test --set logSource=WIRELINE
```

A flow's YAML `schedule:` block takes the same as `operation:` and `values:`.

An external trigger (a GitHub Actions job, or a system holding a key) fires the schedules it picks as their cron would,
with their operation and values, and its trace and the job's summary say what each delivery run delivered. It has no
verb here: the GUI's External triggers page keeps it
([External triggers](../../../../sqlflow/docs/reference/concepts/external-triggers.md)).

## Following runs

```bash
sqlflow runs list --kind delivery --status failed
sqlflow runs show <runId> --json
sqlflow runs trace <runId> --follow
sqlflow pipelines list --repo welldb --kind cache
```

- `--kind` filters by flow kind. The kinds OSDU Delivery adds are `delivery`, `retrieval`, `cache`, `assertion`,
  `dimension`, `inventory`, `removal` and `download`, beside the `pre` and `ing` flows that feed them.
- `runs show` prints SQLFlow's run header. The kind's own part of a run is in the `--json` form: `operation`,
  `requestedBy`, `values`, `payload` (including the central configuration the control plane attached, as `references` and
  `partitionReferences`) and `resultJson`, the result the kind recorded (for a delivery run, what it planned and
  delivered; see [Running an OSDU flow](run.md)). What happened to each record is in the ledger:
  [sqlflow records](records.md).

## Compute tasks of the module

A value check of a mapping across a scope, and the export of its issues, are queued as compute tasks, drained by the
nodes. They appear in SQLFlow's task listing under their operation names:

```bash
sqlflow datasources tasks --operation delivery-check-values
sqlflow datasources tasks --operation delivery-export-value-issues
sqlflow datasources task <id>
```

A removal of records from OSDU is not a compute task: it is a `remove` run of the flow, listed with its runs
([Removing records from OSDU](../concepts/removal-and-reversal.md#removing-records-from-osdu)).

## Search

`sqlflow search` prints SQLFlow's categories (objects, columns, definitions, files, flows, flow columns, executed SQL).
The control plane's combined search (`GET /api/v1/search/all`, the GUI's search box) also answers a `records` category from
the ledger, but the CLI does not print it, with or without `--json`. To find a record from a terminal, use
`sqlflow records list <flow.yaml> --search <term>` or `sqlflow records show <flow.yaml> --key <key>`, which read the
ledger directly ([sqlflow records](records.md)).

## Health

The readiness probe of an OSDU Delivery control plane includes a `module-databases` check beside the catalog's. It stays
unhealthy until the control plane has verified the `osdu` module database against its build, and holds the refusal when
the database is missing, behind or ahead (the control plane then stops, logging
`The control plane refuses to run: <reason>`). So `sqlflow health` reporting a failing `ready` under a passing `live`
can mean the module database rather than the catalog. A missing partition does not take the control plane out of
rotation: its `osdu-partitions` check is reported at `/health/startup`, beside the start-up pass, and stays unhealthy
until the partitions the control plane's configuration names are written, and then while the registry holds no
partition, since OSDU Delivery works in partitions; the log says
`OSDU Delivery has no partition to work against. Name at least one in the control plane's configuration: ...`
([configured partitions](../concepts/partitions.md#configured-partitions)). The probe's
body says only `Unhealthy`; the control plane's log names the reason, `sqlflow db status` says what to apply
([sqlflow db](db.md)), and `sqlflow partition list` shows what is registered.

## See also

- [Control-plane verbs](../../../../sqlflow/docs/reference/cli/control-plane.md): every remote verb, signing in, output and exit codes.
- [Running an OSDU flow](run.md): operations, values, payload and result of an OSDU run.
- [sqlflow config](config.md): the configuration a queued run carries.
- [Partitions](../concepts/partitions.md): the `partition` run value.
- [Control plane](../concepts/control-plane.md): the module's background services and settings on the control plane.
- [The delivery API](../concepts/api.md): the `/api/v1/delivery` routes the GUI uses.
