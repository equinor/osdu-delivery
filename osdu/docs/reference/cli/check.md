---
id: delivery-cli-check
title: "sqlflow check: the preflight of a delivery flow, its mapping, template and cache, without sending anything"
type: cli-command
summary: "Check a delivery flow before it runs: the mapping against the template its folders hold and the cache it reads, the route, and with --connect its tables."
keywords:
  - check
  - preflight
  - delivery preflight
  - check a mapping
  - pinned template
  - cache version
  - "--connect"
  - ingestion tables
  - interface order
  - waits for
  - dry run
  - before delivering
  - what the next run reads
cliCommand: check
related:
  - delivery-concept-preflight
  - delivery-cli-validate
  - delivery-cli-preview
  - delivery-cli-values
  - delivery-flow-delivery
  - delivery-flow-interfaces
  - delivery-cli-db
  - concept-cli-conventions
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryCliModule.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryVerbs.cs
  - osdu/src/SqlFlow.Delivery.Cli/CliPartitions.cs
  - osdu/src/SqlFlow.Delivery/Engine/FlowRuntime.cs
  - osdu/src/SqlFlow.Delivery/Engine/RenderResolver.cs
  - osdu/src/SqlFlow.Delivery/Engine/RouteChecks.cs
  - osdu/src/SqlFlow.Delivery/Validation/Preflight.cs
  - osdu/src/SqlFlow.Delivery/Validation/PayloadRoots.cs
  - osdu/src/SqlFlow.Delivery/Model/SourceDefinition.cs
  - osdu/src/SqlFlow.Delivery/Model/InterfaceOrder.cs
  - osdu/src/SqlFlow.Delivery/Documents/MappingCatalog.cs
  - osdu/src/SqlFlow.Delivery/Documents/DeliveryLayout.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/RenderContext.cs
  - osdu/src/SqlFlow.Delivery/Hosting/OsduModuleDatabase.cs
  - sqlflow/src/SqlFlow.Cli/Hosting/CliModuleSet.cs
  - sqlflow/src/SqlFlow.Cli/Program.cs
---

# sqlflow check: the preflight of a delivery flow, its mapping, template and cache, without sending anything

`sqlflow check` answers "would this delivery flow render?" before anything is planned or sent. It loads the flow, the
mapping it pins, the template that mapping fills (from the template folders on the mapping's path) and the version of the
partition cache it reads, builds the render context, and runs the preflight gate a run runs. It sends nothing to OSDU and
writes nothing anywhere. Use it after editing a flow, a mapping or a template folder, and in a CI job that has the module
database.

The command line is `sqlflow`: OSDU Delivery's CLI is SQLFlow's CLI with the OSDU verbs added (`check`, `preview`,
`values`, `records`, `config`, `partition`, `cache`, `template`, `assertions`, `dimensions`, `inventory`). The options
every command shares (`--db`, `--json`, `-v`, the git-ignored `.sqlflow/env` file, where an option may sit on the line)
are SQLFlow's, described in [CLI conventions](../../../../sqlflow/docs/reference/concepts/cli-conventions.md).

## Synopsis

```bash
sqlflow check <flow.yaml> [--interface <name>] [--partition <id>] [--set name=value]... [--connect]
              [--db <conn-ref>] [--json] [-v|--verbose]
```

## Arguments

| Argument | Required | Description |
| --- | --- | --- |
| `<flow.yaml>` | yes | A delivery flow (`flowType: delivery`), in the single form or as a source with interfaces. Without it the verb prints `ERROR  name the flow document to check.` and its usage, and exits 1. |

## Options

| Option | Type | Default | Description |
| --- | --- | --- | --- |
| `--interface <name>` | string | every interface | Check one interface of a source (names compare ignoring case). A name the document does not declare fails with `Flow '<name>' has no interface '<x>'; it declares <names>.` On a flow in the single form any name fails with `Flow '<name>' declares no interfaces, so it has no interface '<x>'.` |
| `--partition <id>` | string | as a run settles it | The partition to check a flow that works in partitions in. Without it: the registry's default when the flow serves it, else the flow's only listed partition. A flow whose partition is its `target.headers.data-partition-id` takes none. |
| `--set name=value` | repeatable | the declared defaults | A value for one of the flow's own `parameters`; a name set more than once is given the list of its values, which a parameter `source.record.scope` binds reads as one scope. A required parameter with no value fails with `<file>: parameter '<name>' is required. Supply it with --set <name>=value or the run's values.`; a name the flow does not declare fails with `<file>: parameter '<name>' is not declared under parameters.` The partition is not a parameter here: name it with `--partition`. |
| `--connect` | switch | off | Also open the flow's ingestion tables on this machine, through the flow's own `source.connection`, and report what a run would read now (see below). Needs that reference to resolve here. |
| `--db <conn-ref>` | string | `${env:SQLFLOW_CATALOG_DB}` | The catalog database, which holds the `osdu` schema. See [The osdu module database in sqlflow db](db.md). |
| `--json` | switch | off | Print the facts as one JSON document on stdout instead of the text form. |

An option the verb does not read is refused before anything runs, with the nearest known option when one is a near
miss: `ERROR  '<option>' is not an option this command reads. Did you mean '<nearest>'? sqlflow check --help lists them all.`
(exit 1).

## What it checks

For each interface it checks (the one flow of the single form, every interface of a source, or the one `--interface`
names), in this order:

1. **The flow and its partition.** The document loads through the same loader `validate` and `run` use, and is bound to
   its partition as a run binds it. Errors are the partition errors a run gives, for example
   `Flow '<name>' serves dev, test and no partition is the default; name the one this run or request targets, or make one the default.`
   when the flow names several partitions, none is named and the registry has no default.
2. **The flow's parameters**, from `--set` and the declared defaults.
3. **The mapping** the flow pins (`render.mapping`, or `interfaces.<name>.mapping`), read from the repository's
   `mappings/` folder: `render.mappings` when the flow names one, else the nearest `mappings` directory walking up from
   the flow file. The file is `<Name>@<version>.yaml` (or `.yml`, or `<Name>/<version>.yaml`), and its own name and
   version must agree with the file name.
4. **The template** the mapping fills, read from the [template folders](../concepts/templates.md#template-folders) on
   the mapping's path: the version `template.version` names, or, when it names the kind alone, the one version the
   nearest folder holding the kind holds. A template no folder there holds fails with
   `<file>: mapping <Name@version> cannot render: no template folder on the path of <mapping file> holds template <kind> version <version>. ...`,
   the rest naming the folders it read and how to put the file in one
   ([sqlflow template](template.md)); `sqlflow template folders --for <mapping file>` lists what the mapping reads.
5. **The cache version** of the partition the flow delivers to, when the mapping reads the cache: the current version,
   or the one `render.cacheVersion` pins. A mapping that only searches reads no cached record and is checked against the
   partition's system properties alone; a mapping that does neither is checked with no cache at all. A partition whose
   cache holds no version yet fails with
   `<file>: mapping <Name@version> reads the cache of partition '<id>', which holds no version yet. Refresh a cache flow that builds it (one naming '<id>' under partitions, or whose source.headers.data-partition-id is '<id>') to capture one.`
   ([The partition cache](../concepts/partition-cache.md)).
6. **The preflight gate**: the mapping against the template and the cache, its searches against the templates they
   pin, every required property covered, and the mapping's parameters against the values the flow supplies. Any error
   fails the check, listing every error at once:
   `<file>: preflight failed with <n> error(s):` followed by one indented `- <message>` line per error. What each check means is on
   [The preflight gate](../concepts/preflight.md).
7. **The route**: the flow's route can deliver the kind the mapping renders (a DSPDM row only by the `dspdm` route, an
   Energistics object only by the `etp` route, and so on; [Routes](../flow/routes.md)).
8. **The order of a source's interfaces**, worked out as a run works it out: from each interface's `after:` and from the
   relationships the mappings fill, read from the templates. Interfaces that wait for each other in a way nothing cuts
   fail the check, naming them ([Interfaces](../flow/interfaces.md)).

What `check` does not do: it sends no request to OSDU (no probe of the services or the credentials, no legal tag
check) and it reads no row. Without `--connect` it does not open the tables either; with it, it opens them as a run
does, through the planner, and refuses a mapping column the tables lack, naming it, as a run would. A DDMS a flow
names by its registration is read from the Register service when the flow runs, not by `check`.

## What it needs

The templates are read from the repository's files. Caches and the partition registry live in the module database, so
`check` needs it for a mapping that reads a cache and for a flow that leaves its partitions to the registry:
`--db <conn-ref>` (or `SQLFLOW_CATALOG_DB`), the catalog's database, which holds the `osdu` schema.
Without either it stops at the first thing it has to read from there:

```text
ERROR  <file>: mapping WellLog@1.4.0 reads the cache of partition 'dev', and caches live in the module's database, which this host was started without. The osdu schema lives in the catalog's database: start the host with the catalog's connection (SQLFLOW_CATALOG_DB, or --db on the command line).
```

The check that needs no database is [`sqlflow validate`](validate.md).

## Output

### A flow in the single form

One block, `OK` on its first line (values vary):

```text
OK  welldb-wellbore-03-delivery (<flow id>)
    mapping     Wellbore@1.0.0
    template    osdu:wks:master-data--Wellbore:1.3.0 version 58d6bdbd9d066a06 (welldb/templates/osdu_wks_master-data--Wellbore_1.3.0.json)
    cache       none (the mapping reads nothing from a cache)
    context     <first 16 characters of the render context's hash>
    mappings    <absolute path of the mappings directory>
    source      ${env:OSDU_DATA_DB} OsduData.silver.Wellbore
    payloads    none (the flow streams no payload files)
    tables      not read (run with --connect to open the ingestion tables)
```

| Line | Says |
| --- | --- |
| `OK  <flow> (<flow id>)` | The flow and its ledger identity in the partition checked. |
| `ddms` | On a route that reaches a DDMS only: which collection of which DDMS the records go to, or why no DDMS the flow reaches takes them. |
| `mapping`, `template` | The pinned mapping, and the template it fills with the template folder file it was read from; `, the version its folder holds` follows when the mapping names the kind alone. |
| `cache` | `partition <id> version <version> (<n> type(s))`, or `none (the mapping reads nothing from a cache)`. |
| `searches` | Only for a mapping that searches the platform: `exact, then regardless of case where one record answers (...)` or `exact only (...)`, with the partition's system properties that decide it (`<service> <name> <state>`). |
| `context` | The render context hash: the mapping, the template, the cache version and the parameters a record renders with. |
| `mappings` | The mappings directory the flow resolved. |
| `source` | The connection reference and the record table, with `(+<n> dataset(s))` for child tables. |
| `payloads` | The roots the flow's payload files may sit under, or `none (the flow streams no payload files)`. |
| `tables` | `not read (...)` without `--connect`; with it, the lines below. |

### With --connect

The tables are opened as a run opens them, and the mapping is held against them as a run holds it (every dataset
column, label column and child dataset it reads), so a mapping that reads a column the table lacks fails the check,
naming the column; so does a provenance column the flow names that the table lacks. The `tables` line is replaced by
what the next run would read now, against the watermark the ledger holds for the flow's scope, and why:

```text
    scope       <parameter values, name=value;...> (<full | incremental since <time> | ...>)
    reads       <why: no watermark yet, the rules moved, or the rows changed after <time> (an overlap of <n>s)>
    window      <everything | after <time>> through <time>
    candidates  <n> record(s) in the window; changes: <True | False>
    key         <key column> <sql type>, ...
    table       record: <n> column(s)
    table       <child dataset>: <n> column(s)
    issue       <what the preflight says of the mapping against the tables without stopping a run>
```

The `reads` line says why the next run reads what it reads: `no ledger keeps a watermark here, so a run reads every row
in scope`, `no whole-scope plan of scope <scope> has completed yet, so
the next run reads every row in scope`, `scope <scope> was last planned with other rules than <mapping> renders with
now (its mapping, template or parameters changed), so the next run reads every row in scope`, or `scope <scope> was
planned through <time>; the next run reads the rows changed after <time> (an overlap of <n>s)`. Each `issue` line is a
warning the preflight raises against the tables, as a run logs it. Nothing is planned, rendered or delivered.

### A source with interfaces

A first line gives the order the interfaces run in, then one block per interface checked. Each block adds the
interface's ledger, its route and why, its wave, and what it waits for:

```text
welldb-03-delivery: 2 of 2 interface(s) checked, in the order they run: wellbores then welllogs
OK  welldb-03-delivery/wellbores (<flow id>)
    ledger      welldb-03-delivery/wellbores@dev
    route       storage: interfaces.wellbores declares no files and no bulk, so each record is written through the storage service
    wave        1
    waits for   nothing
    mapping     Wellbore@1.0.0
    ...
OK  welldb-03-delivery/welllogs (<flow id>)
    ledger      welldb-03-delivery/welllogs@dev
    route       storage: interfaces.welllogs declares no files and no bulk, so each record is written through the storage service
    wave        2
    waits for   wellbores: interfaces.welllogs.after names it
    mapping     WellLog@1.0.0
    ...
```

A `waits for` line names an interface this one waits for and why: an `after:` that names it, or the properties its
mapping fills that refer to the kind the other delivers. A `not waited` line names a reference the order leaves out of
a cycle, and why.

### --json

stdout carries one JSON document; keys are sorted and properties without a value are left out.

| Key | Holds |
| --- | --- |
| `flow`, `flowId` | The flow's name and its ledger identity; an interface's name is under `interface`. |
| `mapping` | The pinned mapping, `Name@version`. |
| `template` | `kind` and `version` of the template the mapping fills, `file` (the template folder file it was read from) and `versionNamed` (false when the mapping names the kind alone). |
| `renderContext` | The render context: `mapping`, `schema`, `parameters`, `cacheVersion`, and when present `mappingFingerprint`, `cache` and `systemProperties`. |
| `source` | `connection`, `record` (the record table), `key`, `datasets` (each `name` and `object`) and `payloadRoots`. |
| `mappings` | The mappings directory. |
| `cache` | `partition`, `version` and `types` of the cache version read; absent when the mapping reads no cache. |
| `ddms` | Where a DDMS route's records go; absent on other routes. |
| `read` | With `--connect`: `scope`, `selection` (what the next run reads, a full read when the rules moved included), `why`, `watermark`, `window` (`from`, `to`), `candidates`, `hasChanges`, `keyColumns` (each `name`, `type`), `systemColumns`, `columns` per dataset and `issues` (the preflight's warnings against the tables). |
| `interface`, `ledger`, `route`, `after` | For an interface: its name, its ledger name, `route` (`name`, `reason`) and the names its `after:` lists. |

For a source with interfaces the document is `{ "flow", "order", "interfaces": [...] }`, one object per interface
checked, each with the keys above plus `wave`, `waitsFor` and `notWaitedFor` (each an `interface`, its `origin`,
`after` or `schema`, and `why`).

## Examples

Check the wellbore delivery flow in the `dev` partition:

```bash
sqlflow check flows/welldb-wellbore-03-delivery.yaml --partition dev --db '${env:SQLFLOW_CATALOG_DB}'
```

Check it and open its ingestion tables, as JSON for a script:

```bash
sqlflow check flows/welldb-wellbore-03-delivery.yaml --partition dev --connect --json
```

Check one interface of a source:

```bash
sqlflow check flows/welldb-03-delivery.yaml --partition dev --interface welllogs
```

A flow that names two partitions, checked without naming one, when no partition is the registry's default:

```text
ERROR  Flow 'welldb-wellbore-03-delivery' serves dev, test and no partition is the default; name the one this run or request targets, or make one the default.
```

## Exit behavior

| Exit code | Condition |
| --- | --- |
| 0 | Every interface checked passed; the text or JSON answer is on stdout. `-h`/`--help` also exits 0. |
| 1 | A usage error (no flow named, an option the verb does not read); the flow, its mapping, template or cache did not load; the preflight found an error; the route cannot deliver the kind; the interfaces cannot be ordered; or `--connect` could not open the tables. One `ERROR  <message>` line on stderr, credentials redacted. |
| 130 | Interrupted with Ctrl+C. |

## See also

- [The preflight gate](../concepts/preflight.md): what each check of a mapping against its template and cache means.
- [sqlflow validate](validate.md): the offline check of the documents, no database needed.
- [sqlflow preview](preview.md): render one record of the flow.
- [sqlflow values](values.md): what the mapping makes of every row of the scope.
- [Delivery flow](../flow/delivery.md) and [Interfaces](../flow/interfaces.md): the documents `check` reads.
- [CLI conventions](../../../../sqlflow/docs/reference/concepts/cli-conventions.md): options, streams and exit codes shared by every command.
