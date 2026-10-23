---
id: delivery-cli-config
title: "sqlflow config: the central configuration the control plane gives the runs of OSDU flows"
type: cli-command
summary: "List, set and remove the values a flow's ${env:NAME} references resolve to, for the whole control plane, one repository or one partition."
keywords:
  - central configuration
  - config set
  - config effective
  - config list
  - "${env:name}"
  - environment reference
  - per-partition values
  - repository override
  - osdu.configproperty
  - endpoint per partition
  - legal tag and acl groups
  - node environment
  - config list filters
  - repository not registered
  - configured value
  - set by configuration
  - "osdu:partitions properties"
cliCommand: config
related:
  - delivery-concept-partitions
  - delivery-cli-partition
  - delivery-concept-environment-variables
  - delivery-concept-control-plane
  - delivery-cli-run
  - concept-connections-and-secrets
  - cli-control-plane
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryConfigVerbs.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryCliModule.cs
  - osdu/src/SqlFlow.Delivery/Catalog/DeliveryConfigStore.cs
  - osdu/src/SqlFlow.Delivery/Catalog/ConfiguredPartitions.cs
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Background/ConfiguredRunDispatcher.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Background/CentralConfigurationReferences.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryConfigEndpoints.cs
  - sqlflow/src/SqlFlow.Core/Secrets/SuppliedReferences.cs
  - osdu/src/SqlFlow.Delivery/Engine/DeliveryRunPayload.cs
  - osdu/src/SqlFlow.Delivery/Model/DeliveryDestination.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/CacheScope.cs
  - sqlflow/src/SqlFlow.Cli/Hosting/CliVerbContext.cs
  - sqlflow/src/SqlFlow.Cli/Program.cs
---

# sqlflow config

OSDU Delivery's command line is `sqlflow`: SQLFlow's CLI with the OSDU verbs added. `config` is one of those verbs. It
reads and writes the central configuration: named values the control plane attaches to the delivery, cache, retrieval, assertion, dimension and inventory runs it queues, so a
flow that names `${env:OSDU_URL}` or `${env:OSDU_LEGAL_TAG}` gets its value from one place instead of from the
environment of whichever node picks the run up.

## Synopsis

```bash
sqlflow config list      [--repo <id>] [--partition <id>] [--db <conn-ref>] [--json]
sqlflow config effective --repo <id> [--partition <id>] [--db <conn-ref>] [--json]
sqlflow config set <name> --value <value> [--repo <id>] [--partition <id>] [--description <text>] [--db <conn-ref>] [--json]
sqlflow config remove <name> [--repo <id>] [--partition <id>] [--db <conn-ref>]
```

## Description

A property is a name and a value, kept in `[osdu].[ConfigProperty]` in the module's database. The name is the one a flow
spells in `${env:NAME}`. The value is a non-secret value (an endpoint URL, a legal tag, an entitlements group) or a
`${env:...}` or `${keyvault:vault/secret}` reference that the node resolves. The verb talks to the database directly, not
to the control plane, so it needs the catalog connection (`--db`, else `${env:SQLFLOW_CATALOG_DB}`), or `SQLFLOW_OSDU_DB`
when the `osdu` schema has a database of its own (see [sqlflow db](db.md)).

A property is set at one of four scopes:

| Scope | Options | Applies to |
| --- | --- | --- |
| The control plane | neither option | Every repository's runs. |
| One repository | `--repo <id>` | The runs of that repository's flows; wins over the control plane's value of the same name. |
| One partition | `--partition <id>` | Runs bound to that OSDU partition; wins over every value set for no partition. |
| One repository and partition | both | Runs of that repository's flows bound to that partition; wins over everything else. |

For a run bound to partition P, each name takes the first value set at: the repository for P, the control plane for P,
the repository, the control plane. A name none of them sets is resolved from the node's own environment, so an estate can
hold some values centrally and leave the rest on its nodes. A value set for a partition always wins over one set for no
partition, which is what keeps an estate-wide `OSDU_URL` from sending a run of `test` to the `dev` platform. A run of a
flow whose partition is its `data-partition-id` header is bound to no partition and takes the repository's and the
control plane's values only. How runs are bound to partitions is in [Partitions](../concepts/partitions.md).

### Values the control plane's configuration sets

In a deployment, a partition's values usually come from the control plane's configuration rather than from this verb:
`Osdu:Partitions:<partition>:Properties:<NAME>` (as an environment variable,
`Osdu__Partitions__<partition>__Properties__<NAME>`), which the control plane writes at its own scope for that partition
when it starts ([configured partitions](../concepts/partitions.md#configured-partitions)). Such a value is the
configuration's:

- `list` marks it `(configured)` after its scope, and `--json` gives it `"isConfigured": true`.
- `set` and `remove` of it (the same name, that partition, no `--repo`) are refused, naming the setting to change; the
  control plane writes the new value when it next starts.
- A value the configuration stops setting stays, no longer marked, and is kept with this verb from then on.

The values set for no partition come from the control plane's configuration the same way, under
`Osdu:Properties:<NAME>` (`Osdu__Properties__<NAME>`): what every run of every kind resolves with, so a deployment gives
its pre-ingestion and ingestion flows their connections (`OSDU_DATA_DB`) where it gives its delivery flows their platform.
They are the configuration's too, marked and refused here in the same way.

Everything else stays this verb's: a repository's value (which wins over the configured one in its scope), and any name
the configuration does not set.

### Which runs read it

- **Runs the control plane queues.** A run triggered from the GUI or with `sqlflow trigger`, a schedule fire, and every
  member of a run group is queued carrying the values set for no partition, whatever its kind: a pre-ingestion or
  ingestion flow reads its connections from them as a delivery flow reads its platform. A run of a flow kind of the
  module (delivery, cache, retrieval, assertion, dimension and inventory, a delivery flow's and an inventory's remove run
  included) carries every partition's own values besides, and the node resolves with the values of the partition the run
  binds to over the ones set for none.
- **Compute tasks.** Every compute task carries the values set for no partition. The module's node task, a value check
  (`delivery-check-values`), carries the effective values of the one partition it acts in.
- **What the control plane runs while a person waits**: a probe of the target, a record read back from OSDU, a preview,
  a scope's values. These resolve with the same values.
- **Not a workstation.** `sqlflow run`, `check`, `preview` and `values` on a workstation do not read the central
  configuration: they resolve every reference from the workstation's environment and its `.sqlflow/env` file.

A run that already carries values in its payload (`references` or `partitionReferences`) keeps them: a caller that
composed the payload deliberately is not overruled. Nothing is attached when the configuration is empty.

### How a value is resolved

Only `${env:NAME}` references are looked up in the configuration. A `${keyvault:...}` reference written in a flow is
always resolved by the node directly. A property whose value is itself a reference is resolved once, by the node's own
resolver: `OSDU_CLIENT_SECRET = ${keyvault:welldb-vault/osdu-client-secret}` makes the node read the vault, and a value of
`${env:OTHER}` is read from the node's environment even when `OTHER` is also a property (properties do not chain). A
property whose value is its own reference (`OSDU_URL = ${env:OSDU_URL}`) is handed back to the node's environment.
A run's trace names where each reference it resolves took its value, the configuration or the node's environment,
never the value.

The OSDU flow kind supplies four mapping parameters a flow does not set under `render.parameters`: `dataPartition`,
`aclOwner`, `aclViewer` and `legalTag`, which default to `${env:OSDU_DATA_PARTITION}`, `${env:OSDU_ACL_OWNER}`,
`${env:OSDU_ACL_VIEWER}` and `${env:OSDU_LEGAL_TAG}`. Those names are therefore natural properties. A flow bound to a
partition is given `dataPartition` as that partition, written literally, and never reads `OSDU_DATA_PARTITION`; a
flow whose header names its partition is given its header's value (`target.headers.data-partition-id`, as written).
Only a flow bound to no partition reads `OSDU_DATA_PARTITION`.

### Limits

| Item | Rule |
| --- | --- |
| Name | A letter or underscore, then letters, digits and underscores; at most 64 characters. A run matches it to a flow's `${env:NAME}` exactly, case included. |
| Value | Trimmed; then not blank, at most 1,000 characters, no control characters. |
| Partition | A data-partition-id written literally: letters, digits, underscore, hyphen and dot, at most 200 characters. It need not be registered. |
| Description | Trimmed; at most 400 characters, the width of its column, refused before anything is saved. |
| Per run | At most 64 properties in any one scope, and values for at most 64 partitions. A configuration beyond either, or one too large for a run's payload, refuses the run with a message naming the flow; nothing is queued. |

The command does not inspect a value for secrets, but no run carries one: a property whose name ends in `SECRET`,
`PASSWORD`, `TOKEN`, `APIKEY` or `API_KEY` must be a whole reference, and a connection string must not write out its
password, secret or key, or every run that would carry it is refused with a message naming the property. Point a property
at a secret with a `${keyvault:...}` or `${env:...}` reference instead. The control plane's configuration inspects its
values the same way at start.

## Arguments

| Argument | Required | Description |
| --- | --- | --- |
| `<subcommand>` | yes | One of `list`, `effective`, `set`, `remove` (case-insensitive). |
| `<name>` | `set`, `remove` | The property's name, as a flow spells it in `${env:NAME}`. |

## Options

| Flag | Type | Default | Description |
| --- | --- | --- | --- |
| `--repo <id>` | repository id | none (the control plane's own) | The repository the property is set for, or read for, by its id (a GUID), not its name. `sqlflow repos show <name>` prints it. Anything that is not a GUID is refused. `set` refuses an id the catalog does not hold, reading the catalog through `--db`, else `${env:SQLFLOW_CATALOG_DB}`, even when `SQLFLOW_OSDU_DB` gives the module a database of its own; `list` and `remove` do not look it up, so a deleted repository's properties can still be found and removed. |
| `--partition <id>` | data-partition-id | none (no particular partition) | The partition the property is set for, or read for. |
| `--value <value>` | string | none | `set` only, required: the value or reference. |
| `--description <text>` | string | none | `set` only: what the property is for, trimmed. Replaced by every `set`, and cleared when left out. |
| `--db <conn-ref>` | connection reference | `${env:SQLFLOW_CATALOG_DB}` | The catalog database, which holds the `osdu` schema unless `SQLFLOW_OSDU_DB` names its own. |
| `--json` | flag | off | Writes one JSON document to stdout instead of text (not for `remove`). |

## Behavior and output

### list

`list` prints every property of every scope, ordered by name, then repository, then partition. `--repo` narrows it to
that repository's properties in every partition, `--partition` to that partition's at every scope, and both to that
repository's properties in that partition.

```text
OSDU_CLIENT_SECRET = ${keyvault:welldb-vault/osdu-client-secret}  [(all repositories)]  set by cli:ops@build-agent at 2026-10-09 07:40:52Z
OSDU_LEGAL_TAG = welldb-test-legal  [0b5e7f3a-2c41-4d8e-9a6b-1f0c2d3e4a5b, partition test]  set by cli:ops@build-agent at 2026-10-09 07:41:12Z
OSDU_URL = https://osdu.example.com  [(all repositories)]  set by cli:ops@build-agent at 2026-10-09 07:40:03Z
OSDU_URL = https://osdu-test.example.com  [(all repositories), partition test]  (configured)  set by configuration at 2026-10-09 07:30:11Z
```

`(configured)` marks a value the control plane's configuration sets, which it records as set by `configuration`. With
nothing set at all it prints
`no configuration property is set; every reference a flow names is resolved on the node that runs it`; with nothing
for the filter asked, `no configuration property is set for repository <id> in partition <p>` (naming only the filters
given). With `--json` it prints an array of
`{ name, value, repoId, partition, description, isConfigured, updatedUtc, updatedBy }`.

### effective

`effective` answers the question an operator usually asks: what does a run of this repository's flows get? It merges the
control plane's values with the repository's over them, and with `--partition`, that partition's values (the control
plane's, then the repository's) over both. It prints one `NAME = value` line per name, ordered by name; `--json` prints
one object of name to value. It needs `--repo`; without it the command stops with
`name the repository with --repo <id>: what a run is given depends on which estate it belongs to.`

### set

`set` creates the property at the scope given, or replaces the value and description already there, and prints
`<name> set` (with `--json`, the stored property). The value and the description are trimmed before they are checked.
The command records `cli:<user>@<machine>` as who set it; a property set through the API records the signed-in user.
A value the control plane's configuration sets is refused.

### remove

`remove` deletes the property at exactly the scope given (the same `--repo` and `--partition` it was set with) and prints
`<name> removed`, or `<name> was not set` when nothing was there. Both exit 0: removing what is absent leaves the same
state. A value the control plane's configuration sets is refused, and stays.

### Errors

| Message | Cause |
| --- | --- |
| `'<name>' does not name a configuration property: a property is named as a flow spells it in ${env:NAME}, which is a letter or underscore followed by letters, digits and underscores, at most 64 characters.` | The name breaks the name rule. |
| `'<name>' needs a value; remove the property instead of setting it to nothing.` | The value is blank once trimmed. |
| `the value of '<name>' is longer than 1000 characters (<n>) or holds a control character; a property holds an identifier, a URL or a ${env:...} or ${keyvault:...} reference.` | The value breaks the value rule. |
| `the description of '<name>' is <n> characters; a property's description is at most 400.` | The description is too long once trimmed. |
| `'<partition>' is not a data-partition-id a property can be set for: letters, digits, underscore, hyphen and dot, at most 200 characters.` | `--partition` is a reference or holds other characters. |
| `--repo '<text>' is not a repository id (a GUID; 'sqlflow repos show <name>' prints it).` | `--repo` is not a GUID. |
| `No repository <id> is registered with the catalog, so a configuration property set for it would never be read by a run. 'sqlflow repos list' and the Repositories page show the registered repositories and their ids.` | `set --repo` names a repository the catalog does not hold. |
| `--repo is checked against the catalog's repositories, and the catalog connection <ref> did not resolve: ... Name it with --db <conn-ref>, or set SQLFLOW_CATALOG_DB.` | `set --repo` with no catalog connection to check it in. |
| `'<name>' in partition '<partition>' is set by the control plane's configuration (Osdu:Partitions:<partition>:Properties:<name>); change it there, and the control plane writes it when it next starts.` | `set` or `remove` of a value the configuration sets. |
| `name the property to set.` / `give the value with --value <value>.` / `name the property to remove.` | A required argument is missing; the verb's usage follows. |
| `use 'config list', 'config effective --repo <id>', ...` | No subcommand, or one that is not listed; the verb's usage follows. |

Each is printed as `ERROR  <message>` on stderr. A usage error is followed by the verb's usage lines.

## The same through the API

The control plane serves the same table: `GET /api/v1/delivery/config` (every property, with each repository named;
`repoId` and `partition` filter it as `list`'s options do), `PUT /api/v1/delivery/config/{name}` and
`DELETE /api/v1/delivery/config/{name}` (both admin, with optional `repoId` and `partition` query parameters), and
`GET /api/v1/delivery/config/effective/{repoId}` (optional `partition`). The command and the API go through the same
store checks: the API answers 400 `Configuration property refused` with the same message, 404 `Repository not
found` for a `PUT` naming a repository the catalog does not hold, and 409 `Set by configuration` for a `PUT` or `DELETE`
of a value the control plane's configuration sets. `DELETE` does not look the repository up. The listing marks each
property `configured`. The GUI has no page for it. See [The delivery API](../concepts/api.md).

## Examples

On a workstation, or where the control plane's configuration does not give them, point the whole control plane at one
OSDU platform, and the `test` partition at another:

```bash
sqlflow config set OSDU_URL --value https://osdu.example.com --description "dev platform"
sqlflow config set OSDU_URL --value https://osdu-test.example.com --partition test
sqlflow config set OSDU_TOKEN_URL --value https://login.example.com/oauth2/token
```

Hold the client secret centrally by reference only; each node reads the vault with its own identity:

```bash
sqlflow config set OSDU_CLIENT_SECRET --value '${keyvault:welldb-vault/osdu-client-secret}'
```

Give one repository its own legal tag and access groups in `test`, then check what a run bound to `test` is given:

```bash
REPO=$(sqlflow repos show welldb | jq -r .repo.id)
sqlflow config set OSDU_LEGAL_TAG --value welldb-test-legal --repo "$REPO" --partition test
sqlflow config set OSDU_ACL_OWNER --value data.welldb.owners@test.example.com --repo "$REPO" --partition test
sqlflow config effective --repo "$REPO" --partition test
```

```text
OSDU_ACL_OWNER = data.welldb.owners@test.example.com
OSDU_CLIENT_SECRET = ${keyvault:welldb-vault/osdu-client-secret}
OSDU_LEGAL_TAG = welldb-test-legal
OSDU_TOKEN_URL = https://login.example.com/oauth2/token
OSDU_URL = https://osdu-test.example.com
```

Against an explicit catalog connection reference:

```bash
sqlflow config list --db '${env:SQLFLOW_CATALOG_DB}' --json
```

## Exit behavior

| Exit code | Meaning |
| --- | --- |
| 0 | The subcommand did what it was asked, including `remove` of a property that was not set. |
| 1 | A usage error, a refused name, value, description, partition or repository, a value the control plane's configuration sets, or a database connection that could not be resolved, printed as `ERROR  <message>`. |
| 130 | Interrupted with Ctrl+C. |

## See also

- [Partitions](../concepts/partitions.md): how a run is bound to the partition whose values it reads first, and the
  partitions and values the control plane's configuration names.
- [sqlflow partition](partition.md): the partition registry.
- [Environment variables](../concepts/environment-variables.md): the references an estate's flows name, and where each tier reads them.
- [Running an OSDU flow](run.md): `--set` values and the run payload.
- [Connections and secrets](../../../../sqlflow/docs/reference/concepts/connections-and-secrets.md): how SQLFlow resolves `${env:...}` and `${keyvault:...}`.
- [Control-plane verbs](../../../../sqlflow/docs/reference/cli/control-plane.md): `sqlflow repos show` and the other remote verbs.
