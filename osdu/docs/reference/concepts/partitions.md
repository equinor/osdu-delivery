---
id: delivery-concept-partitions
title: "OSDU partitions: configured per deployment, delivering the same flows to dev, test and prod, and how a run picks one"
type: concept
summary: "How a deployment configures its OSDU partitions, how a flow names them or follows the registry, how a run binds to one, and what each partition keeps."
keywords:
  - data partition
  - data-partition-id
  - partition registry
  - configured partitions
  - "osdu:partitions"
  - osdu__partitions
  - set by configuration
  - register a partition
  - default partition
  - partitions key
  - keepledger
  - registry-driven flow
  - multiple environments
  - "--set partition"
  - "partition=*"
  - per-partition ledger
  - per-partition cache
  - per-partition configuration
related:
  - delivery-cli-partition
  - delivery-cli-config
  - delivery-flow-delivery
  - delivery-flow-cache
  - delivery-concept-ledger
  - delivery-concept-partition-cache
  - delivery-concept-gui
  - delivery-cli-run
  - delivery-concept-control-plane
  - delivery-guide-deployment
sourceRefs:
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryEndpoints.cs
  - osdu/src/SqlFlow.Delivery/Model/DeclaredPartition.cs
  - osdu/src/SqlFlow.Delivery/Model/RegisteredPartitions.cs
  - osdu/src/SqlFlow.Delivery/Model/SourceDefinition.cs
  - osdu/src/SqlFlow.Delivery/Model/FlowDefinition.cs
  - osdu/src/SqlFlow.Delivery/Model/CacheDefinition.cs
  - osdu/src/SqlFlow.Delivery/Model/AssertionFlowDefinition.cs
  - osdu/src/SqlFlow.Delivery/Model/DimensionFlowDefinition.cs
  - osdu/src/SqlFlow.Delivery/Model/InventoryFlowDefinition.cs
  - osdu/src/SqlFlow.Delivery/Model/DeliveryDestination.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/CacheScope.cs
  - osdu/src/SqlFlow.Delivery/Documents/DeliveryDocumentLoader.cs
  - osdu/src/SqlFlow.Delivery/Engine/DeliveryExecutor.cs
  - osdu/src/SqlFlow.Delivery/Engine/CacheExecutor.cs
  - osdu/src/SqlFlow.Delivery/Engine/RetrievalExecutor.cs
  - osdu/src/SqlFlow.Delivery/Engine/PartitionLedgers.cs
  - osdu/src/SqlFlow.Delivery/Engine/DeliveryRunPayload.cs
  - osdu/src/SqlFlow.Delivery/Catalog/DeliveryPartitionRegistry.cs
  - osdu/src/SqlFlow.Delivery/Catalog/DeliveryConfigStore.cs
  - osdu/src/SqlFlow.Delivery/Catalog/DeliveryCatalogSync.cs
  - osdu/src/SqlFlow.Delivery/Catalog/ConfiguredPartitions.cs
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery.Data/Migrations/20261014052816_ConfiguredPartitions.cs
  - osdu/src/SqlFlow.Delivery.Cli/CliPartitions.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Configuration/DeliveryModuleOptions.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Background/CentralConfigurationStep.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Background/PartitionReadiness.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Background/RepositoryResync.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryPartitionEndpoints.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryConfigEndpoints.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/WorkbenchPartition.cs
  - osdu/src/SqlFlow.Delivery/Engine/Operations/PartitionConnection.cs
  - osdu/gui/src/features/delivery/DeliveryPartitionsPage.tsx
  - osdu/gui/src/features/delivery/PartitionNotice.tsx
  - osdu/gui/src/features/delivery/useInterfaceChoice.ts
  - osdu/gui/src/module.tsx
  - osdu/docs/census/keys.delivery.json
  - osdu/docs/census/keys.cache.json
---

# Partitions

An OSDU platform keeps its data in partitions: every request names one in its `data-partition-id` header, and every
record id starts with one. One OSDU Delivery catalog can deliver the same flows and the same mappings to several
partitions (`dev`, `test`, `prod`), and the partition is what ties a run together: a run for `test` reads the cache built
for `test`, sends `data-partition-id: test`, mints ids that start with `test:`, keeps its records in a ledger of their
own, and resolves the `test` values of the central configuration first. A deployment names its partitions, and the values
their runs resolve, in the control plane's configuration ([configured partitions](#configured-partitions)). This page
explains how a flow says which partitions it works in, how a run picks one, and what each partition keeps apart.

## Three ways a flow names its partitions

| Form | How it is written | Serves |
| --- | --- | --- |
| **Header** | `data-partition-id` in `target.headers` (a delivery flow) or `source.headers` (a cache, retrieval, assertion, dimension or inventory flow), a literal id or a `${env:...}` reference. | The one partition the header names, resolved on the host that runs the flow. |
| **Hard-coded** | `partitions:` lists them, and the header is left out. | The partitions listed, whether or not they are registered. |
| **Registry-driven** | Neither `partitions` nor the header. | Every partition registered with the catalog: the ones the control plane's configuration names ([configured partitions](#configured-partitions)), and any registered by hand ([sqlflow partition](../cli/partition.md)). The same documents then deploy to every environment, and a new environment is a partition named in the deployment's configuration rather than an edit to every flow. |

A flow that names its partitions, hard-coded or through the registry, is said to work in partitions. A header flow
behaves as flows did before partitions existed: one partition, its ledger under the flow's own name.

A hard-coded delivery flow, whose `dev` partition keeps the ledger the flow kept while it delivered through a header, with
an hourly schedule that delivers to `dev` (the well log flow's keys, as they would read):

```yaml
# flows/welldb-welllog-03-delivery.yaml, had it delivered through a header before it named its partitions
partitions:
  - name: dev
    keepLedger: true      # the records this flow delivered before it named partitions went to dev
  - test
target:
  endpoint: ${env:OSDU_URL}          # no data-partition-id: every run sets it to the partition it targets
schedule:
  cron: "0 * * * *"
  timezone: UTC
  values:
    partition: dev
    logSource: WIRELINE
```

A registry-driven delivery flow, serving every registered partition, with `dev` keeping its earlier ledger:

```yaml
# flows/welldb-wellbore-03-delivery.yaml made registry-driven: its partitions line taken out, and
keepLedger: dev           # only for a registry-driven flow; a hard-coded one marks the entry instead
target:
  endpoint: ${env:OSDU_URL}          # resolved with the run's partition's own configuration values first
```

A cache flow that builds the lookup cache of two partitions, one type only for `test`:

```yaml
# flows/welldb-lookups-00-cache.yaml, made to name its partitions, with the curve lookup built for test only
partitions: [dev, test]
source:
  connection: ${env:OSDU_DATA_DB}
types:
  - table: OsduData.silver.UnitAlias
    key: source_unit
    fields: [osdu_unit]
  - table: OsduData.silver.CurveDictionary
    key: mnemonic
    fields:
      - log_curve_type_id
      - { column: unit, as: curve_unit }
    partitions: [test]    # built for test only
  - dictionary: sampling-domain
    name: SamplingDomain
```

Each example is an excerpt: the rest of each file (source, render, auth) is as [Delivery flow](../flow/delivery.md) and
[Cache flow](../flow/cache.md) show it, and each file, written so, passes `sqlflow validate`.

### Rules for a flow that works in partitions

| Rule | Detail |
| --- | --- |
| Literal names | A partition is its data-partition-id written literally: letters, digits, underscore, hyphen and dot, at most 200 characters. A reference is refused, because the name is the key the partition's cache, ledger and runs are kept under: `partitions[0] is the reference '${env:OSDU_DATA_PARTITION}'. A partition is named literally, by its data-partition-id: ...` |
| Count | At least one and at most 64 per flow, none twice ignoring case. A name YAML would read as a boolean or a decimal number is written in quotes. |
| No header | `target.headers` (or `source.headers`) may not name `data-partition-id`: `target.headers names 'data-partition-id', and the flow names its partitions: every run sets the header to the partition it targets. Remove the header.` |
| No `dataPartition` | `render.parameters` may not set `dataPartition`: every id a run mints is minted in the partition it targets. |
| No `partition` parameter | `parameters` may not declare `partition`, the run value that names the partition. |
| No cache pin across partitions | `render.cacheVersion` may not pin a version when the flow names more than one partition or follows the registry, because a version is a version of one partition's cache. |
| `keepLedger` | On a hard-coded delivery flow, `keepLedger: true` on at most one entry. On a registry-driven one, `keepLedger: <partition>` at the top; refused beside `partitions` and on a header flow. |

### By flow kind

| Kind | Names its partitions with | A run naming `*` | Each partition keeps |
| --- | --- | --- | --- |
| Delivery | `partitions` (a name, or `{ name, keepLedger }`), or top-level `keepLedger` when registry-driven | refused: a delivery acts in one partition | its own ledger |
| Cache | `partitions`; `types[].partitions` narrows a type to some of them (any partitions, for a registry-driven flow) | builds every partition the flow serves, one after another | its own cache |
| Assertion | `partitions` | refused: a report describes one partition | its own report history |
| Dimension | `partitions` | refused | its own dimensions |
| Inventory | `partitions` | refused | its own inventories |
| Retrieval | the `data-partition-id` header only | a run that names any partition is refused | its runs, in the partition its header resolves to |

## The partition registry

The registry is the table `[osdu].[Partition]`: the partitions registered with the catalog, and the one marked default.
A deployment fills it from the control plane's configuration, which the control plane writes into it when it starts
([configured partitions](#configured-partitions)). A partition can also be registered by hand, with
[sqlflow partition](../cli/partition.md) or `POST /api/v1/delivery/partitions`: what a workstation against a local catalog,
or an estate whose registry is kept by hand, does. The GUI's Partitions page (in the menu's OSDU group) lists the
registry and registers nothing. The first partition registered becomes the default, and the default can be removed only
when it is the last. Removing a partition deletes nothing kept under it. What the configuration sets (a configured
partition, its description, the default it names, its values) is changed only in the configuration.

The registry settles only what a document leaves open: which partitions a registry-driven flow serves, and which
partition a run that names none runs in. A repository sync describes each registry-driven flow in the partitions
registered at that moment (its interfaces and its cache types, one per partition), and warns about a registry-driven cache
flow while no partition is registered:
`cache flow '<name>' builds a cache for every partition registered with the catalog, and none is registered yet, so it builds none. Name one in the control plane's configuration (Osdu:Partitions:<name>), or register one with 'sqlflow partition add <name>'.`

## Configured partitions

A deployment names the OSDU partitions it delivers to in the control plane's configuration, under `Osdu:Partitions`, one
child per data-partition-id, with the values the runs bound to each resolve their flows' `${env:NAME}` references with.
The control plane registers them when it starts, so a deployment comes up with its partitions and their endpoints,
credentials, legal tag and access groups in place, and a new environment is a few settings rather than a registration
and a dozen `sqlflow config set` commands.

| Setting | Meaning |
| --- | --- |
| `Osdu:Partitions:<data-partition-id>:Default` | `true` on the partition a run that names none runs in. With several partitions, exactly one says so; one partition alone is the default without saying so. |
| `Osdu:Partitions:<data-partition-id>:Description` | What the partition is for, shown wherever partitions are listed; at most 400 characters. |
| `Osdu:Partitions:<data-partition-id>:Properties:<NAME>` | A value of the [central configuration](../cli/config.md) for the runs bound to the partition, written at the control plane's scope for it (no repository): `OSDU_URL`, `OSDU_TOKEN_URL`, `OSDU_SCOPE`, `OSDU_CLIENT_ID`, `OSDU_CLIENT_SECRET`, `OSDU_ACL_OWNER`, `OSDU_ACL_VIEWER`, `OSDU_LEGAL_TAG`, or any other name a flow spells. |
| `Osdu:Properties:<NAME>` | A value of the central configuration set for no partition, written at the control plane's scope: what every run of every kind resolves with, a pre-ingestion or ingestion flow's connections (`OSDU_DATA_DB`) and a storage account's location as much as a delivery's platform, so the deployment's nodes need no environment of their own. |

As environment variables, which is how the shipped deployment templates give them (`__` for each `:`), two partitions
with `dev` the default:

```text
# The control plane's environment.
Osdu__Partitions__dev__Default=true
Osdu__Partitions__dev__Description=Development platform
Osdu__Partitions__dev__Properties__OSDU_URL=https://osdu-dev.example.com
Osdu__Partitions__dev__Properties__OSDU_TOKEN_URL=https://login.example.com/oauth2/token
Osdu__Partitions__dev__Properties__OSDU_SCOPE=https://osdu-dev.example.com/.default
Osdu__Partitions__dev__Properties__OSDU_CLIENT_ID=welldb-delivery
Osdu__Partitions__dev__Properties__OSDU_CLIENT_SECRET=${keyvault:welldb-vault/osdu-dev-client-secret}
Osdu__Partitions__dev__Properties__OSDU_LEGAL_TAG=dev-welldb-public
Osdu__Partitions__test__Description=Acceptance testing platform
Osdu__Partitions__test__Properties__OSDU_URL=https://osdu-test.example.com
Osdu__Partitions__test__Properties__OSDU_CLIENT_SECRET=${keyvault:welldb-vault/osdu-test-client-secret}
Osdu__Properties__OSDU_DATA_DB=${keyvault:welldb-vault/osdu-data-db}
```

A name a partition's values leave out resolves as every reference does: the repository's or the control plane's value
for no partition, then the node's environment ([sqlflow config](../cli/config.md)). The client secret stays in the
vault: the value is a `${keyvault:<vault>/<secret>}` reference, carried unresolved in the run's payload, which the node
resolves with its own identity when a run needs it, as the control plane does for the reads a person waits on. A flow's
source connection is the exception: a partition's own value does not reach it, and the control plane's reads of a
flow's ingestion tables take it from the control plane's environment
([a flow's source connection](environment-variables.md#a-flows-source-connection)).

`OSDU_URL`, `OSDU_TOKEN_URL`, `OSDU_SCOPE`, `OSDU_CLIENT_ID` and `OSDU_CLIENT_SECRET` are also the partition's own
connection to OSDU: the explorer reads the partition through them, resolved the same way but with no repository's
values, and needs no flow to do so ([the explorer](explorer.md#how-it-reaches-osdu)).

### What start checks

The control plane checks the configuration as it composes, and refuses to start on the first setting it cannot take,
naming the setting:

| Rule | Detail |
| --- | --- |
| Names | Each child is a data-partition-id written literally: letters, digits, underscore, hyphen and dot, at most 200 characters. Two names that differ only in case are one partition, refused as named twice. |
| Count | At most 64 partitions, and at most 64 values each: what one run carries. |
| One default | With several partitions, exactly one has `Default` set to `true`. |
| Value names | As a flow spells them in `${env:NAME}`: a letter or underscore, then letters, digits and underscores, at most 64 characters. Two that differ only in case are refused, since the central configuration holds them as one. |
| Values | Trimmed, then not empty (leave the setting out instead), at most 1,000 characters, no control character. |
| Secrets | A value whose name ends in `SECRET`, `PASSWORD` or `TOKEN`, or names an API key (`APIKEY`, `API_KEY`), is exactly one `${keyvault:<vault>/<secret>}` or `${env:NAME}` reference. A connection string does not write out its password, secret or key: it names it as a reference (`Password=${keyvault:<vault>/<secret>}`) or connects with an identity (`Authentication=Active Directory Default`). The refusal names the setting and never repeats the value. |
| Values set for no partition | `Osdu:Properties` is checked by the same rules, at most 64 values. |

For example:
`Osdu:Partitions:dev:Properties:OSDU_CLIENT_SECRET holds a value where a secret's reference belongs. Keep the secret in a key vault and set the reference: ${keyvault:<vault>/<secret>}, which the node resolves when a run needs it.`
and
`Osdu:Partitions names 2 partitions and none is the default; set Osdu:Partitions:<name>:Default to true on the one a run that names no partition runs in.`

### What start writes

The control plane's start-up pass has a step of the module's, `osdu-configuration`, after the module database is verified
against the build ([the start-up pass](../../../../sqlflow/docs/reference/concepts/control-plane.md#the-start-up-pass)). It
writes the configuration in one transaction:

- It registers each partition the registry does not hold, recorded as registered by `configuration`.
- It takes over each partition registered before (by hand, or by an earlier configuration), keeping the registry's
  spelling of its name, the one its caches and ledgers are kept under, and gives it the configured description (none,
  when the configuration gives none).
- It makes the configured default the default, taking the mark from a partition chosen by hand.
- It sets each value for its partition at the control plane's scope, taking over a value set there by hand, and setting
  back one changed since.
- It sets each value of `Osdu:Properties` for no partition at the control plane's scope, the same way.
- It then reads, once, every Key Vault secret those values name (`${keyvault:<vault>/<secret>}`) with the control
  plane's own identity, and the step fails naming each reference it cannot read and the settings that name it, never a
  value. A secret not yet put in the vault is reported by the start-up pass (`/health/startup`,
  `GET /api/v1/bootstrap`), and the step succeeds on a later attempt once it is there.
- It marks each partition and value it wrote as configured, the `IsConfigured` column of `[osdu].[Partition]` and
  `[osdu].[ConfigProperty]` (migration `ConfiguredPartitions`, module version 1.44.0).

Writing the same configuration again changes nothing, and the control plane logs what a pass changed. A step that fails
(a lost connection) is tried again with the start-up pass's backoff until it completes, and `/health/startup` names it meanwhile. A partition it registers makes every enabled
repository due for a sync at once, as registering one by hand does, so the registry-driven flows are described in it.

Nothing is removed. A partition or a value the configuration stops naming is released: it stays registered (or set),
no longer marked configured, and from then on is an admin's to keep, describe, make the default or remove like any
partition registered by hand. A configuration that names no partition registers none and leaves the registry as it is,
releasing what an earlier configuration named.

### Changed only in the configuration

What the configuration sets would be set back at the next start, so everything else refuses to change it, naming the
setting to change instead:

| Change | Refused when |
| --- | --- |
| Describe (`sqlflow partition describe`, `PUT /partitions/{name}`) | the configuration names the partition |
| Remove (`sqlflow partition remove`, `DELETE /partitions/{name}`) | the configuration names the partition |
| Make another partition the default (`sqlflow partition default`, `POST /partitions/{name}/default`, and registering with `--default` or `isDefault`) | a configured partition is the default |
| Set or remove a value (`sqlflow config set` and `remove`, `PUT` and `DELETE /config/{name}`) | the configuration sets that name for that partition, or for no partition (`Osdu:Properties`), at the control plane's scope |

The API answers `409 Set by configuration` with the message; the command line prints it as `ERROR  <message>` and exits
1. For example
`Partition 'dev' is set by the control plane's configuration, which describes it: change Osdu:Partitions:dev:Description there.`
and
`'OSDU_URL' in partition 'dev' is set by the control plane's configuration (Osdu:Partitions:dev:Properties:OSDU_URL); change it there, and the control plane writes it when it next starts.`
A configured partition is taken out of the registry in two steps: out of the configuration, then, once the control plane
has started without it, removed as an ordinary registered partition.

What stays open: registering another partition by hand (not as the default while the configuration names one), and
setting values the configuration does not set, among them a repository's own values for a configured partition, which
win over the configured ones.

### Readiness

OSDU Delivery works in partitions: every delivery, cache, retrieval and record read happens in one. The control plane's
start-up probe (`/health/startup`, beside the start-up pass) therefore carries an `osdu-partitions` check, unhealthy until
the configured partitions are written, and then unhealthy while the registry holds no partition at all, configured or
registered by hand. It is not part of readiness (`/health/ready`): a control plane waiting for its partitions, its
configuration's client secret not yet in the vault for instance, keeps serving the API, so a person can sign in, read
why (`GET /api/v1/bootstrap`) and register one. The control plane logs the reason:
`OSDU Delivery has no partition to work against. Name at least one in the control plane's configuration: Osdu:Partitions:<data-partition-id>, with the values its runs resolve under Osdu:Partitions:<data-partition-id>:Properties (OSDU_URL, OSDU_TOKEN_URL, OSDU_SCOPE, OSDU_CLIENT_ID, OSDU_CLIENT_SECRET as a ${keyvault:...} reference). The control plane reports its start-up as not done (/health/startup) until one is registered.`
A partition registered by hand (`sqlflow partition add`) completes it without a restart.

## The partition a run targets

A run of a flow that works in partitions targets exactly one partition (or, for a cache refresh, every one in turn). It
names it in the run value `partition`:

| Where the run starts | How it names the partition |
| --- | --- |
| The GUI | The partition picked in the title bar's switcher. Every run started from the GUI acts in it, and a flow that does not serve it refuses to start, saying which partitions it serves. |
| `sqlflow run`, `sqlflow trigger` | `--set partition=<id>` ([Running an OSDU flow](../cli/run.md)). |
| A schedule | `values: { partition: <id> }` in the YAML `schedule:` block, or `--set partition=<id>` on `sqlflow schedules create`. |
| The module's own verbs | `--partition <id>` on `check`, `preview`, `values`, `records`, `cache`, `config`, `assertions`, `dimensions` and `inventory list`; the other `inventory` subcommands take the partition as an argument. |

The partition is settled the same way on every host:

| The run names | Hard-coded `partitions` | Registry-driven | Header |
| --- | --- | --- | --- |
| a partition | that one, which the flow has to list; it need not be registered | that one, which has to be registered | refused |
| none | the registry's default when the flow lists it, else the flow's only partition, else refused | the registry's default, else refused | the header's |
| `*` (cache refresh only) | every partition listed, one after another | every registered partition, one after another | refused |

The refusals name what would settle it, for example
`Flow 'welldb-welllog-03-delivery' serves dev, test and no partition is the default; name the one this run or request targets, or make one the default.`,
`Partition 'prod' is not registered with the catalog, so nothing runs in it. Name it in the control plane's configuration (Osdu:Partitions:prod), or register it with 'sqlflow partition add prod'; the registered partitions are dev, test.`,
and for a header flow
`Flow '<name>' names no partitions; it delivers to the partition its target.headers name, so a run or request cannot target '<id>'. ...`.
The registry is read only when the document leaves the partition open: always for a registry-driven flow, and for a
hard-coded one only when the run names no partition and the flow lists several, to find the default. A run of a
hard-coded flow that names its partition, or of one that lists a single partition, never reads it.

The partition is taken off the run's values before the flow's parameters are read, so naming it moves neither a
watermark nor a submission's scope. The one exception is a header flow written before partitions existed that declares a
parameter called `partition`: it keeps the value as its own.

The verbs that only read what a partition kept (`records`, `assertions`, `dimensions`) bind a named partition as it is,
even one taken out of the registry, so its records and reports stay readable. `check`, `preview`, `values` and `cache`
settle it as a run would.

## What a bound run is

Bound to its partition, a run is the flow as if it had been written for that partition alone:

- **The header.** Every request carries `data-partition-id: <partition>`.
- **The ids.** The mapping's `dataPartition` is the partition, written literally, so every id and reference it mints
  starts with it. The flow kind supplies it to a mapping that declares the parameter; a flow that works in partitions
  never reads `${env:OSDU_DATA_PARTITION}` for it. A flow bound by its `data-partition-id` header mints its ids in its
  header's partition (the header as written), never in `OSDU_DATA_PARTITION`, which only a flow bound to no partition
  reads. A header flow whose header and `OSDU_DATA_PARTITION` once disagreed has records whose ids were claimed in the
  environment's partition; their next render meets the [changed id hold](change-detection.md#a-record-keeps-the-osdu-id-it-claimed).
- **The cache.** The mapping reads that partition's cache ([The partition cache](partition-cache.md)).
- **The ledger.** The run keeps its records in the partition's own ledger (below).
- **The configuration.** Every `${env:NAME}` the flow names resolves from the partition's values of the central
  configuration first (the ones its `Osdu:Partitions:<name>:Properties` settings give it, and any set with
  [sqlflow config](../cli/config.md)), so one document reaches a different endpoint, legal tag and access groups per
  partition.

A run logs the partition it was bound to, and everything downstream (planning, rendering, the routes, the cache capture,
the preflight) works on the bound flow.

## A ledger per partition

Each partition a flow works in keeps a ledger of its own, named `<ledger>@<partition>`
(`welldb-welllog-03-delivery@test`) and keyed apart from every other, so the same source row delivered to `dev` and to
`test` is two records, each with its own history. The partition marked `keepLedger` keeps the ledger the flow kept before
it worked in partitions, under its old name and identity, so moving a flow from a header to partitions keeps every
record it delivered.

Two guards keep the ledgers apart, checked when a whole run starts:

- When no partition keeps the flow's earlier ledger and that ledger holds records delivered to a partition the flow
  serves, the run is refused, because that partition's new, empty ledger would deliver them all again as new. The message
  counts the records per partition and ends with what to write: `Mark the partition they were delivered to with
  keepLedger: true under partitions (<partition>). Nothing ran.`, or for a registry-driven flow
  `Name the partition they were delivered to at the top of the flow: keepLedger: <partition>. Nothing ran.`
- When the partition that keeps the earlier ledger finds records in it delivered to another partition, the run is
  refused rather than treat them as its own.

How the ledger keys every row by partition is in [The delivery ledger](ledger.md).

## In the GUI and the API

The partition is chosen in one place in the GUI: the title bar's switcher, which lists every registered partition and
every partition something is still kept under (marked unregistered), starts at the registry's default and remembers the
choice. Every OSDU page is read in it and every run started from the GUI writes to it; no page has a partition picker of
its own. Every call the GUI makes carries it in the `X-Osdu-Partition` header ([The GUI](gui.md)). When the title
bar's partition is one a delivery flow does not deliver to, the flow's own tabs say so and name where it delivers rather
than show another partition's ledger
([when the title bar's partition is not the flow's](gui.md#when-the-title-bars-partition-is-not-the-flows)).

The Partitions page (in the menu's OSDU group) lists the registry and what each partition holds; it registers nothing, since the
configuration names the partitions. A configured partition carries a **configured** badge, whose tooltip names its
setting (`Osdu:Partitions:<name>`), and has no actions. A registered partition the configuration does not name keeps
**Make default** (hidden while the configuration names the default), **Describe** and **Remove** for an admin. With no
partition at all, the page says **No partition to work against** and how to name one in the configuration.

The flow-level routes of the delivery API take `?partition=` and settle it as a run does, answering
`400 Partition required` or `400 No such partition` when it settles none; the reads across flows take `?partition=`, else
the `X-Osdu-Partition` header, else read every partition ([The delivery API](api.md)). The partition and configuration
listings mark each row `configured`, and a change of what the configuration sets answers `409 Set by configuration`.

A route that reads what a ledger kept (a flow's records and their history, its issues, artifacts, reversals and
interfaces) still binds a partition taken out of the registry, when the flow follows the registry and the ledger
directory keeps a ledger of one of the flow's interfaces in that partition: removing a partition leaves its ledgers in
place, so what was delivered there stays readable. Nothing is queued in it this way; a run settles its partition from
the registry, and asks for the partition to be registered again.

## See also

- [sqlflow partition](../cli/partition.md): listing the registry, and keeping partitions registered by hand.
- [sqlflow config](../cli/config.md): values set for one partition.
- [The control plane](control-plane.md): the `Osdu:Partitions` settings beside the module's others, and its readiness.
- [Deploying OSDU Delivery](../guides/deployment.md): the partitions in every deployment's configuration.
- [Delivery flow](../flow/delivery.md) and [Cache flow](../flow/cache.md): the `partitions` and `keepLedger` keys in context.
- [The delivery ledger](ledger.md): how every ledger row is keyed by its partition.
- [The partition cache](partition-cache.md): one cache per partition.
- [Running an OSDU flow](../cli/run.md): `--set partition=<id>` and the other run values.
