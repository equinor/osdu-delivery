---
id: delivery-cli-partition
title: "sqlflow partition: list the OSDU partitions, register them by hand and choose the default a run uses"
type: cli-command
summary: "List the partition registry, and register, describe, make default and remove the OSDU partitions the control plane's configuration does not name."
keywords:
  - partition registry
  - register a partition
  - default partition
  - data-partition-id
  - partition add
  - partition remove
  - partition list
  - configured partition
  - set by configuration
  - registry-driven flow
  - osdu.partition
  - dev and test partitions
  - environments
  - who registered a partition
cliCommand: partition
related:
  - delivery-concept-partitions
  - delivery-cli-config
  - delivery-flow-delivery
  - delivery-flow-cache
  - delivery-concept-gui
  - delivery-concept-control-plane
  - cli-db
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryPartitionVerbs.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryCliModule.cs
  - osdu/src/SqlFlow.Delivery/Catalog/DeliveryPartitionRegistry.cs
  - osdu/src/SqlFlow.Delivery/Catalog/ConfiguredPartitions.cs
  - osdu/src/SqlFlow.Delivery/Model/RegisteredPartitions.cs
  - osdu/src/SqlFlow.Delivery/Model/DeclaredPartition.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/CacheScope.cs
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery.Data/Migrations/20260928053849_PartitionRegistry.cs
  - osdu/src/SqlFlow.Delivery.Data/Migrations/20261014052816_ConfiguredPartitions.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryPartitionEndpoints.cs
  - osdu/src/SqlFlow.Delivery/Catalog/DeliveryCatalogSync.cs
---

# sqlflow partition

OSDU Delivery's command line is `sqlflow`: SQLFlow's CLI with the OSDU verbs added. `partition` is one of those verbs.
It reads and keeps the partition registry: the list of OSDU data partitions (`dev`, `test`, ...) that a flow naming no
partitions serves, and the one marked default, which a run that names no partition runs in. A new environment is added
without changing any flow document.

A deployment names its partitions in the control plane's configuration (`Osdu:Partitions:<data-partition-id>`, with the
values their runs resolve), and the control plane registers them when it starts
([configured partitions](../concepts/partitions.md#configured-partitions)); `partition list` shows them marked
`(configured)`. Registering by hand is for where nothing configures them: a workstation working against a local catalog,
or an estate whose registry is kept by hand.

## Synopsis

```bash
sqlflow partition list                                          [--db <conn-ref>] [--json]
sqlflow partition add <name> [--description <text>] [--default] [--db <conn-ref>] [--json]
sqlflow partition describe <name> --description <text>          [--db <conn-ref>] [--json]
sqlflow partition default <name>                                [--db <conn-ref>] [--json]
sqlflow partition remove <name>                                 [--db <conn-ref>]
```

## Description

The registry is the table `[osdu].[Partition]` in the module's database: one row per partition, with what it is for, who
registered it and when, a flag for the one default, and a flag for each partition the configuration names. The verb works on the database directly, not through the control
plane, so it needs the catalog connection (`--db`, else `${env:SQLFLOW_CATALOG_DB}`), whose database holds the `osdu`
schema (see [sqlflow db](db.md)).

What the registry decides, and what it does not:

- **A registry-driven flow serves every registered partition.** A delivery, cache, assertion, dimension or inventory
  flow that names neither `partitions` nor a `data-partition-id` header serves the partitions registered now, and a run
  of it has to name one of them (or take the default).
- **A run that names no partition runs in the default**, when the flow serves it; for a flow that names exactly one
  partition, that one.
- **A hard-coded partition need not be registered.** A flow that lists its partitions under `partitions`, or names one in
  its `data-partition-id` header, works in them whether or not they are registered.

The full model (binding a run to a partition, per-partition ledgers and caches, the `*` value of a cache refresh) is in
[Partitions](../concepts/partitions.md).

Rules the registry keeps:

| Rule | Detail |
| --- | --- |
| Names | A partition is named by its data-partition-id, written literally: letters, digits, underscore, hyphen and dot, at most 200 characters. A `${env:...}` or `${keyvault:...}` reference is refused, because the name is the key the partition's cache, ledgers and runs are kept under. |
| Case | Names compare regardless of case: `Dev` and `dev` are one partition. |
| One default | At most one partition is the default, which a filtered unique index enforces. The first partition registered becomes the default. |
| Removal | Removing a partition deletes nothing kept under it: its caches, ledgers and runs stay. The default can be removed only when it is the last partition, so a registry that holds partitions always has a default. |
| Description | At most 400 characters. |
| Configured | A partition the control plane's configuration names is described, made the default and removed only there: `describe` and `remove` of it are refused, and so is `default` (or `add --default`) while a configured partition is the default. Each refusal names the setting to change. |

A partition the configuration stops naming stays registered, no longer marked configured, and from then on is kept here
like any other. Taking a configured partition out of the registry is therefore two steps: out of the configuration,
then, once the control plane has started without it, `partition remove`.

A run reads the registry when it starts. The rows a repository sync writes per partition (a registry-driven flow's
interfaces and cache types, which the GUI and the API read) follow the registry at each sync. After a change here, run
`sqlflow db sync` or press Sync now on the Repositories page; a registration or removal through the API (the Partitions
page's Remove among them), and a partition the configuration registers at start, make every repository due at once
instead.

When the registry was added, the migration registered every partition the catalog already kept data under (every
cache's partition and every partition a delivery interface was described in) and made it the default when there was
exactly one.

## Arguments

| Argument | Required | Description |
| --- | --- | --- |
| `<subcommand>` | yes | One of `list`, `add`, `describe`, `default`, `remove` (case-insensitive). |
| `<name>` | all but `list` | The partition, by its data-partition-id, as runs name it. |

## Options

| Flag | Type | Default | Description |
| --- | --- | --- | --- |
| `--description <text>` | string | none | `add`: what the partition is for. `describe`: required; the new text, and an empty value (`--description ""`) clears it. |
| `--default` | flag | off | `add` only: make the new partition the default, taking the mark from the one that had it. Refused while a configured partition is the default. |
| `--db <conn-ref>` | connection reference | `${env:SQLFLOW_CATALOG_DB}` | The catalog database, which holds the `osdu` schema. |
| `--json` | flag | off | Writes the partition (or for `list`, the array) as JSON instead of text. `remove` prints text either way. |

## Behavior and output

### list

Every registered partition, ordered by name:

```text
dev  (default)  (configured)  Development platform  [registered by configuration at 2026-10-09 07:30:11Z]
sandbox  Scratch partition for mapping trials  [registered by cli:ops@build-agent at 2026-10-09 07:31:40Z]
test  (configured)  Acceptance testing platform  [registered by configuration at 2026-10-09 07:30:11Z]
```

`(default)` marks the default and `(configured)` a partition the control plane's configuration names; a partition the
configuration registered was registered by `configuration`. With an empty registry it prints
`no partition is registered; a flow that names no partitions has none to run in. Name the deployment's partitions in the control plane's configuration (Osdu:Partitions), or register one with 'sqlflow partition add <name>'.`
With `--json` each partition is `{ name, description, isDefault, isConfigured, createdUtc, createdBy, updatedUtc, updatedBy }`.

This lists the registry only. The GUI's Partitions page and `GET /api/v1/delivery/partitions` also list the partitions
something is still kept under (a cache, a flow that hard-codes one, a ledger) after it was removed, marked unregistered,
with what each holds.

### add

Registers the partition and prints `<name> registered`, or `<name> registered, and is the default` when it took the
default (with `--default`, or because it is the first). A second line reminds that each repository describes its
registry-driven flows in the registered partitions at its next sync. `--default` is refused while a configured partition
is the default. A partition registered here and later named by the configuration is taken over at the control plane's
next start, keeping its name as registered.

### describe

Sets what the partition is for and prints `<name> described`. Refused for a configured partition, whose description is
`Osdu:Partitions:<name>:Description`.

### default

Makes the partition the default and prints `<name> is the default`. The partition that had the mark loses it in the same
transaction. Refused while a configured partition is the default: the configuration names the default
(`Osdu:Partitions:<name>:Default`), and would set it back at the next start.

### remove

Takes the partition out of the registry and prints
`<name> removed; its caches, ledgers and runs stay, and no registry-driven flow runs in it until it is registered again`,
followed by the sync reminder. A partition that was not registered prints `<name> was not registered` and exits 0.
After removal, the partition's records stay readable (`sqlflow records ... --partition <name>` and the GUI read them
when the partition is named), and a run of a registry-driven flow in it is refused until it is registered again. A flow
that hard-codes the partition is not affected. A configured partition is refused: take it out of the configuration
first.

The command records `cli:<user>@<machine>` (the account it runs as, on that machine) as who registered or changed a
partition; the Partitions page and the API record the signed-in user, and the configuration records `configuration`.
Registering, describing, changing the default and removing are admin actions in the API; the Partitions page offers
all but registering, for the partitions the configuration does not name.

### Errors

| Message | Cause |
| --- | --- |
| `Partition '<name>' is registered already.` | `add` of a name the registry holds, in any case. |
| `The partition is the reference '<value>'. A partition is named literally, by its data-partition-id: ...` | `add` of a `${env:...}` or `${keyvault:...}` reference. |
| `The partition '<value>' is not a data-partition-id: letters, digits, underscore, hyphen and dot, at most 200 characters.` | `add` of a name with other characters, or a longer one. |
| `A partition's description is at most 400 characters; this one is <n>.` | `--description` too long. |
| `Partition '<name>' is not registered with the catalog, so nothing runs in it. Name it in the control plane's configuration (Osdu:Partitions:<name>), or register it with 'sqlflow partition add <name>'; the registered partitions are <list>.` | `describe` or `default` of a partition the registry does not hold. With an empty registry the message ends after `'sqlflow partition add <name>'`. |
| `Partition '<name>' is the default, which a run that names no partition runs in; make another partition the default before removing it.` | `remove` of the default while other partitions are registered. |
| `Partition '<name>' is set by the control plane's configuration, which describes it: change Osdu:Partitions:<name>:Description there.` | `describe` of a configured partition. |
| `The default partition is '<configured>', which the control plane's configuration makes the default; to make '<name>' the default, configure it and set Osdu:Partitions:<name>:Default to true there instead.` | `default`, or `add --default`, while a configured partition is the default. |
| `Partition '<name>' is set by the control plane's configuration (Osdu:Partitions:<name>). Take it out of the configuration; once the control plane has started without it, it is an ordinary registered partition an admin can remove.` | `remove` of a configured partition. |
| `The partition registry changed while this change was made (<cause>); nothing was changed. Try again.` | Two changes at the same moment (two registrations of one name, or two changes of the default); the database refused the second. |
| `name the partition to register: its data-partition-id, as runs name it.` | `add` without a name; the verb's usage follows. Each other subcommand has its own line of this kind. |
| `give what the partition is for with --description <text>; an empty one clears it.` | `describe` without `--description`. |
| `use 'partition list', 'partition add <name> [--description <text>] [--default]', ...` | No subcommand, or one that is not listed; the verb's usage follows. |

Each is printed as `ERROR  <message>` on stderr.

## Examples

A deployment's control plane names its two partitions in its configuration, and registers them when it starts
([configured partitions](../concepts/partitions.md#configured-partitions)):

```text
Osdu__Partitions__dev__Default=true
Osdu__Partitions__dev__Description=Development platform
Osdu__Partitions__dev__Properties__OSDU_URL=https://osdu-dev.example.com
Osdu__Partitions__test__Description=Acceptance testing platform
Osdu__Partitions__test__Properties__OSDU_URL=https://osdu-test.example.com
```

Moving its default is then a change of the configuration, not of the registry:

```bash
sqlflow partition default test
```

```text
ERROR  The default partition is 'dev', which the control plane's configuration makes the default; to make 'test' the default, configure it and set Osdu:Partitions:test:Default to true there instead.
```

On a workstation against a local catalog, where nothing configures them, register the two environments by hand, with
`dev` the default:

```bash
sqlflow partition add dev --description "Development platform"
sqlflow partition add test --description "Acceptance testing platform"
sqlflow db sync ./welldb --repo welldb
```

```text
dev registered, and is the default
each repository describes its registry-driven flows in the registered partitions at its next sync ('sqlflow db sync', or Sync now on the Repositories page)
test registered
each repository describes its registry-driven flows in the registered partitions at its next sync ('sqlflow db sync', or Sync now on the Repositories page)
```

Move the default to `test`, then retire `dev` without losing what was delivered there:

```bash
sqlflow partition default test
sqlflow partition remove dev
```

Read the registry from a script:

```bash
sqlflow partition list --json --db '${env:SQLFLOW_CATALOG_DB}'
```

## Exit behavior

| Exit code | Meaning |
| --- | --- |
| 0 | The subcommand did what it was asked, including `remove` of a partition that was not registered. |
| 1 | A usage error or a refusal from the table above, printed as `ERROR  <message>`, or a database connection that could not be resolved. |
| 130 | Interrupted with Ctrl+C. |

## See also

- [Partitions](../concepts/partitions.md): configured partitions, hard-coded and registry-driven flows, the partition a run binds to, and what each partition keeps of its own.
- [sqlflow config](config.md): values set for one partition, such as its endpoint and legal tag.
- [The control plane](../concepts/control-plane.md): the `Osdu:Partitions` settings and the readiness check that needs a partition.
- [Delivery flow](../flow/delivery.md) and [cache flow](../flow/cache.md): the `partitions` and `keepLedger` keys.
- [The GUI](../concepts/gui.md): the title bar's partition switcher and the Partitions page.
- [sqlflow db](../../../../sqlflow/docs/reference/cli/db.md): `db sync`, which describes registry-driven flows in the registered partitions.
