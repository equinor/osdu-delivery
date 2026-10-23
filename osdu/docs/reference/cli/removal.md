---
id: delivery-cli-removal
title: "sqlflow removal: a removal flow's runs, what each read, found removed and took out of OSDU"
type: cli-command
summary: "sqlflow removal runs and run: a removal flow's runs in a partition from a terminal, with what each read, found removed, held for approval and took out."
keywords:
  - sqlflow removal
  - removal runs
  - removal flow
  - keys found removed
  - guard held
  - removal tally
  - "--removal"
cliCommand: removal
related:
  - delivery-flow-removal
  - delivery-cli-records
  - delivery-cli-run
  - delivery-table-removal-run
  - cli-run
  - concept-cli-conventions
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryRemovalVerbs.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryCliModule.cs
  - osdu/src/SqlFlow.Delivery.Cli/CliPartitions.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SourceRemovals.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Removal.cs
  - sqlflow/src/SqlFlow.Cli/Hosting/CliVerbContext.cs
---

# sqlflow removal: a removal flow's runs, what each read, found removed and took out of OSDU

`sqlflow removal` reads the runs of a [removal flow](../flow/removal.md) where an operator already is, without a control
plane to reach: a removal flow's runs in a partition, newest first, and one run with every count it kept. It is one of the
verbs OSDU Delivery adds to SQLFlow's command line (`SqlFlow.Delivery.Cli.Host`, published as `sqlflow`). It reads the
module's database only and sends nothing to OSDU.

The deletions a removal flow found are the delivery flow's, so they are read through that flow:
[`sqlflow records removed`](records.md#records-removed) lists them, and
[`sqlflow records lift-removal`](records.md#records-lift-removal) delivers records again. A removal itself is a run, and so
is a plan of one:

```bash
sqlflow run flows/welldb-wellbore-05-removal.yaml --set partition=dev
sqlflow run flows/welldb-wellbore-05-removal.yaml --set partition=dev --operation plan
sqlflow run flows/welldb-wellbore-05-removal.yaml --set partition=dev --payload '{"entries":["welldb-wellbore-03-delivery"]}'
```

## Synopsis

```bash
sqlflow removal runs <removal.yaml> [--partition <id>] [--removal <name>] [--max <n>]
sqlflow removal run  <partition> <number>
```

Every form also takes `--db <conn-ref>`, `--json` and `-v`.

| Argument or option | Meaning |
| --- | --- |
| subcommand | `runs` or `run`. Anything else prints `ERROR  say what to show: runs or run.` and the usage, exit 1. |
| `<removal.yaml>` | The removal flow whose runs are listed. |
| `--partition <id>` | The partition whose runs are listed. A partition named here is read as it is, even one taken out of the registry, since the runs its removals kept stay readable. Without it, the partition a run would take. |
| `--removal <name>` | One removal of the flow, by name, ignoring case; without it, every removal's runs. A name the flow does not declare is refused, naming the ones it does. |
| `--max <n>` | How many runs, from 1 to 200; 20 without it. |
| `<partition> <number>` | One run, by its partition and its number, as `runs` prints it. |
| `--db <conn-ref>` | The module database when the `osdu` schema lives in the catalog's database; `SQLFLOW_OSDU_DB` names it otherwise. Without one: `Removal runs live in the module's database. Run 'sqlflow removal' with --db <conn-ref>, or set the catalog variable.` |
| `--json` | One JSON document on stdout instead of the text form. Keys are sorted and properties without a value are left out. |

## removal runs

The removal flow's runs in the partition, newest first, each in three lines: its number, status, operation, removal and
delivery flow, when it started and who ran it; what it read and found; and what taking the deletions out came to:

```text
welldb-wellbore-05-removal in dev: 2 run(s)
      14  guarded     remove  welldb-wellbore-03-delivery -> welldb-wellbore-03-delivery  2026-10-23T02:00:00Z  by schedule:welldb-removal
          2210 source key(s), 0 listed, 2480 in the table, 2450 compared; 640 found removed (640 held), 0 came back, 0 lifted
          0 removed from OSDU, 0 already gone, 0 settled, 0 kept out; 2 referenced, 0 shared, 1 busy, 0 failed
          640 key(s) found removed wait for an operator's approval, since the guard allows at most 20% of the 2450 compared; nothing of them was taken out or kept out. Approve them on the removal flow's page, or correct the source.
      13  completed   remove  welldb-wellbore-03-delivery -> welldb-wellbore-03-delivery  2026-10-22T02:00:00Z  by tahir
          ...
```

The status is `running`, `completed`, `guarded` (its guard held what it found for an operator's approval) or `failed`, with
why on the fourth line. With no run yet it says
`none yet. A removal is a run: sqlflow run <removal.yaml> --set partition=<id>`.

`--json`: `title` and `runs`, each with `partition`, `removalRunId`, `flow`, `removal`, `deliveryFlow`, `ledgerFlowId`,
`runId`, `operation`, `status`, `actor`, `startedUtc`, `completedUtc`, `sourceReadUtc`, the counts of
[`osdu.RemovalRun`](../tables/removal-run.md) (`rowsRead`, `sourceKeys`, `listedKeys`, `ingestionKeys`, `compared`,
`detected`, `awaitingApproval`, `approved`, `removed`, `alreadyGone`, `settled`, `keptOut`, `referenced`, `shared`,
`busy`, `failed`, `cameBack`, `lifted`), `guardPercent`, `guardRecords`, `approvedBy` and `error`.

## removal run

One run, by its partition and number, in the same form. A number the partition holds no run of prints
`No removal run <n> in partition '<partition>'.`, exit 1.

## Exit behavior

| Exit code | Condition |
| --- | --- |
| 0 | The runs were printed. |
| 1 | A usage error; the flow or its partition could not be settled; no module database; a removal or run that does not exist. Errors print one `ERROR  <message>` line on stderr, credentials redacted. |
| 130 | Interrupted with Ctrl+C. |

## See also

- [Removal flow](../flow/removal.md): what a run reads, finds, holds for approval and takes out.
- [`sqlflow records`](records.md): the deletions a removal flow found, and delivering records again.
- [Running an OSDU flow](run.md): the `remove` and `plan` operations and the payload a removal flow takes.
- [CLI conventions](../../../../sqlflow/docs/reference/concepts/cli-conventions.md): options, streams and exit codes shared by every command.
