---
id: delivery-cli-template-register
title: "sqlflow template register: register a repository's custom schemas with a partition's Schema service"
type: cli-command
summary: "Find every custom kind in a repository's template folders, show where each stands in OSDU, and register, update or publish it from an env file."
keywords:
  - template register
  - register schema
  - register custom schema
  - custom schema
  - custom kind
  - schema service
  - sync schemas with osdu
  - upload schema to osdu
  - publish schema
  - development schema
  - schema not found
  - schema-service editors
  - env file
  - .sqlflow/env
  - upsertSchema
cliCommand: template register
related:
  - delivery-cli-template
  - delivery-concept-templates
  - delivery-guide-linked-records
  - delivery-concept-dry-run
  - delivery-concept-partitions
  - delivery-cli-dev
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryTemplateRegister.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryVerbs.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryCliModule.cs
  - osdu/src/SqlFlow.Delivery.Cli/ModuleCommand.cs
  - osdu/src/SqlFlow.Delivery/Engine/Search/SchemaRegistrar.cs
  - osdu/src/SqlFlow.Delivery/Engine/Search/SchemaServiceReader.cs
  - osdu/src/SqlFlow.Delivery/Engine/Operations/PartitionConnection.cs
  - osdu/src/SqlFlow.Delivery/Templates/CustomTemplates.cs
  - osdu/src/SqlFlow.Delivery/Templates/TemplateFolders.cs
---

# sqlflow template register: register a repository's custom schemas with a partition's Schema service

OSDU storage takes a record of a kind the Schema service does not hold, but the indexer flags it `schema not found`, so
search, the explorer and assertions never find it by its data. A custom kind (a [template of one's
own](../concepts/templates.md#templates-of-ones-own)) therefore has to be registered in every partition its records go
to, and registered again each time its file changes while it is in development. `sqlflow template register` does that
from the repository itself: it finds every custom kind its template folders hold, reads where each stands in the
partition, shows what it would write, and writes what the developer chooses, reading each write back. It reads its
connection from an env file, `.sqlflow/env` by default, and needs no database and no control plane.

It never writes on its own. A sync or a run registers nothing ([decision 0052](../../wiki/decisions/0052-custom-schemas-registered-by-a-command.md)):
a schema is shared by everything in the partition, and the Schema service has no way to remove one.

## Synopsis

```bash
sqlflow template register [<dir>] [--env-file <path>] [--partition <id>] [--kind <kind>... | --all]
                          [--publish] [--dry-run | --confirm <partition>] [--json]
```

`<dir>` is the repository whose template folders are read: by default the repository above the current folder (the
nearest folder holding `.git`), else the current folder.

## What a run does

1. **Reads the env file** for the connection and the partition ([below](#the-env-file)).
2. **Finds every custom kind** the repository's template folders hold: a template whose kind's authority is not `osdu`
   ([which kinds it takes](#which-kinds-it-takes)). A repository holding none says so and asks nothing of OSDU.
3. **Reads where each stands** in the partition's Schema service: every version of each kind's family
   (`GET /api/schema-service/v1/schema` filtered by authority, source and entity type), then the kind's own schema
   (`GET /schema/{id}`), compared with the file ([where a kind stands](#where-a-kind-stands)).
4. **Shows the plan**: for each kind its file and template version, what OSDU holds, every property that differs, notes,
   what can be done and what is proposed.
5. **Asks**, unless `--dry-run` or `--confirm` was given ([choosing](#choosing-what-to-write)).
6. **Writes** each chosen kind with `PUT /api/schema-service/v1/schema`, reading the file again first, then **reads it
   back** and says whether OSDU now holds the file's schema in the status asked ([what it writes](#what-it-writes)).

Every request carries a correlation id under the operation `schemaregister`, printed before the writes, to find them in
the platform's logs.

## The env file

The run's input is an env file of `KEY=VALUE` lines, the file `sqlflow dev setup` writes: the one `--env-file` names,
else the nearest `.sqlflow/env` above the current folder, else above the repository. It reads six variables, the five a
[partition's connection](../concepts/explorer.md#how-it-reaches-osdu) is made of and the partition:

| Variable | Is |
| --- | --- |
| `OSDU_URL` | The platform's base URL, without a path. |
| `OSDU_TOKEN_URL` | The token endpoint the client signs in at (OAuth 2.0 client credentials). |
| `OSDU_SCOPE` | The scope the token is asked for. |
| `OSDU_CLIENT_ID` | The client (application) id. |
| `OSDU_CLIENT_SECRET` | The client secret, or a `${keyvault:vault/secret}` reference to it. |
| `OSDU_DATA_PARTITION` | The partition written in, unless `--partition` names one. |

- **The file decides.** A variable the file sets is used even where the process environment holds another value, and
  the run says so on stderr (`NOTE   OSDU_URL is set to another value in the process environment ...`), naming the
  variable and never a value. A variable the file does not set is read from the process environment, and the header
  says which.
- **A value may be a reference.** A value written as `${keyvault:vault/secret}` (or `${env:OTHER}`) is resolved by the
  command's secret providers when the connection opens. Values do not chain: `${env:OTHER}` is read from the process
  environment, not from the file.
- **Nothing is written back or set.** The run reads the file and changes no environment variable.
- **No value is ever printed.** Messages name variables and line numbers only; a line that is not `KEY=VALUE` is
  refused by its number, and a name set twice is noted, the first being used, as every command uses it.

Without any file the process environment alone is read, as in a CI job whose variables come from its secrets.

## Which kinds it takes

A kind is custom when its authority is not `osdu` (`welldb:wks:work-product-component--WellLogDetail:1.0.0`): the OSDU
data definitions publish `osdu` kinds only, which the platform holds in its shared schemas. Every file holding a custom
kind is read; copies holding the same schema are one entry.

- **The latest version of each family is proposed.** A family is a kind without its version
  (`welldb:wks:master-data--Facility`). Older versions are listed with where they stand and can be chosen, but are
  proposed only with `--all`.
- **`--kind` takes exactly the kinds named** (repeat it, or separate kinds with commas), and refuses an `osdu` kind or one
  no template folder holds.
- **Two files holding different schemas of one kind** (or naming it in different cases, which OSDU reads as two kinds)
  are never written: the plan names both files, and the developer keeps one or gives the other a version of its own.

## Where a kind stands

| State | OSDU holds | Can | Proposed |
| --- | --- | --- | --- |
| Not registered | No schema of the kind. | register, publish | register |
| In step, in development | The file's schema, still changeable. | publish | nothing |
| In step, published or obsolete | The file's schema, final. | nothing | nothing |
| Differs, in development | Another schema of the kind, which a write replaces. | update, publish | update |
| Differs, published or obsolete | Another schema of the kind, which never changes. | nothing: give the file's kind a higher version | nothing |
| Differs from the platform's shared schema | The kind in the platform's shared schemas. | nothing: give the kind an authority or version of its own | nothing |
| Two schemas in the repository | (not read) | nothing: keep one | nothing |
| File cannot be sent | (not read) | nothing: bundle it again | nothing |
| OSDU could not be read | (unknown) | nothing | nothing |

The schema OSDU holds is compared with the file by their [template versions](../concepts/templates.md#template-versions),
the hash of the canonical form, since the service stores a schema re-serialised (members holding null dropped, keys
reordered). When they differ, every property that differs is listed from OSDU's schema to the file's, with its impact
(breaking, additive or wording, as [comparing versions](../concepts/templates.md#comparing-versions) rates them), or,
when no property differs, the JSON paths that do (an `x-osdu` key, a definition).

A note under a kind says:

- that OSDU holds a newer version of the family than any the repository holds;
- that a property changes type, which OSDU's indexer cannot change in place: the kind's index keeps the old mapping until
  an administrator reindexes the kind with `force_clean`;
- that the status could not be read, in which case the service itself refuses a write to a published schema.

## What it writes

| Action | Requests | Leaves |
| --- | --- | --- |
| register | `PUT /schema` in `DEVELOPMENT` | The file's schema, in development, in the partition's own scope (`INTERNAL`). |
| update | `PUT /schema` in `DEVELOPMENT` | The schema in development replaced with the file's. |
| publish | `PUT /schema` in `PUBLISHED`; a kind not registered is registered in `DEVELOPMENT` first | The file's schema, published: it never changes again. |

The body is the Schema service's `SchemaRequest`: `schemaInfo` with the kind's identity in parts (`authority`, `source`,
`entityType`, and `schemaVersionMajor`, `schemaVersionMinor` and `schemaVersionPatch` as numbers) and the status, and
`schema`, the file's bundled schema as a JSON object. A published or obsolete schema is never written, and nothing is
ever deleted: the Schema service has no route that removes a schema.

Before a file is sent it is checked as the service checks it: a local reference must end in the name of a definition at
the top of the schema (the service reads `#/definitions/X/properties/Y` as definition `Y`), and a reference to another
schema is refused, since a template file holds none. A file that fails says which references and is not sent.

After each write the kind is read back. **OK** means OSDU holds the file's schema in the status asked. A refusal is
printed in the service's own words with what it means:

| OSDU answers | Means |
| --- | --- |
| `401 User is unauthorized to perform this action` | The client may read schemas but not write them: it is not in `service.schema-service.editors` in the partition. The service answers 401, not 403, for a missing group. |
| `400 ... Breaking changes were found; analysed versions ...` | The service compares a schema with the nearest versions of its family in the same major version, of any status, and refuses a breaking change between them; give the kind a higher major version. |
| `400 Only schema in developement stage can be updated` | OSDU holds the schema published or obsolete now. |
| `400 Update/Create failed because schema id is present in another tenant` | The platform's shared schemas hold the kind. |

A write whose answer was lost on the way (the request repeated after a gateway timeout, then refused as no longer in
development) reads back as done, and says what the service answered. A write the service accepted that cannot be read
back is reported as **SENT**, not OK.

## Choosing what to write

Asked at a terminal, or with answers piped in, the run asks once:

```text
Write the proposed in partition dev? [y]es, [n]o, [c]hoose kind by kind:
```

`y` (or Enter) writes the proposal, `n` writes nothing, and `c` asks for each kind that can be written, offering its
actions by their first letter with the proposed one in brackets. A publish is never written on one keystroke: the word
`publish` is typed back, and any other answer keeps the schema in development. Input that ends before a question is
answered writes nothing.

`--confirm <partition>` writes the proposal without asking: the partition typed back, as `sqlflow records adopt` takes
it, so a script cannot write in another partition than the one it names. With `--publish`, the proposal for each kind
taken is to publish it, and `--confirm` publishes without the typed word. `--dry-run` shows the plan and writes nothing.

## Options

| Option | Default | Description |
| --- | --- | --- |
| `<dir>` | the repository above the current folder | The repository whose template folders are read. |
| `--env-file <path>` | the nearest `.sqlflow/env` | The env file the connection and the partition are read from. |
| `--partition <id>` | `OSDU_DATA_PARTITION` | The partition written in. |
| `--kind <kind>` | the latest version of each family | Takes exactly these kinds; repeatable, or comma-separated. |
| `--all` | off | Takes every custom kind, older versions included. |
| `--publish` | off | Proposes publishing every kind taken that can be published. |
| `--dry-run` | off | Shows the plan and writes nothing. |
| `--confirm <partition>` | none | Writes the proposal without asking, in the partition typed back. |
| `--json` | off | Prints one JSON document; needs `--dry-run` or `--confirm`, since it asks nothing. |

## Output

```text
Custom schemas of /src/welldb-estate, against OSDU partition dev
  environment  /src/welldb-estate/.sqlflow/env
  platform     https://osdu-dev.example.com/
  templates    6 in 2 template folder(s), 2 of a custom kind

  1  welldb:wks:master-data--Facility:1.1.0  latest
     file      facility/templates/welldb_wks_master-data--Facility_1.1.0.json, template version f449a245d7003e1a
     OSDU      OSDU holds no schema of this kind. Storage takes its records all the same, but the indexer cannot ...
     can       register, publish
     proposed  register

  2  welldb:wks:work-product-component--WellLogDetail:1.0.0  latest
     file      welllog/templates/welldb_wks_work-product-component--WellLogDetail_1.0.0.json, template version a0d9b9260e8ea252
     OSDU      OSDU holds another schema of this kind in development: 1 property differs from it to the file (1 breaking).
     changes   osdu.data.Depth: type number -> string (breaking)
     note      The type of osdu.data.Depth changes. OSDU's indexer cannot change the type of a field it has indexed, ...
     can       update, publish
     proposed  update

Proposed in partition dev: register 1, update 2.
register adds a schema in development, update replaces the one in development, publish makes it final.
Write the proposed in partition dev? [y]es, [n]o, [c]hoose kind by kind: y

Writing 2 schema(s) in partition dev (correlation id osdu-delivery-schemaregister-...):
  OK      1  welldb:wks:master-data--Facility:1.1.0
          Registered welldb:wks:master-data--Facility:1.1.0 in development in partition dev.
  OK      2  welldb:wks:work-product-component--WellLogDetail:1.0.0
          Replaced the schema of welldb:wks:work-product-component--WellLogDetail:1.0.0 in development in partition dev.

2 written, 0 failed.
```

Warnings about the template folders (`WARN`) and the env file (`NOTE`) go to stderr. With `--json` the run prints
`{ repository, environment, partition, platform, correlationId, written, message, warnings, kinds }`, each kind with
`number`, `kind`, `family`, `version`, `latest`, `files`, `templateVersion`, `state`, `status`, `scope`,
`serviceTemplateVersion`, `why`, `changes` (`count`, `breaking`, `additive`, `wording`, `listed`), `otherDifferences`,
`newer`, `notes`, `actions`, `selected`, `proposed`, `chosen` and `outcome` (`action`, `done`, `verified`, `message`,
`steps` of `status` and `httpStatus`, `stateAfter`, `statusAfter`).

## Exit codes

| Code | When |
| --- | --- |
| 0 | Everything chosen was written and read back as asked, or nothing needed writing. A run a person answered is 0 whatever they left out. |
| 1 | A usage error, an env file or connection that cannot be used, a Schema service that cannot be read, or a write that failed. |
| 2 | `--dry-run` or `--confirm`, and a kind taken is left out of step: one a write would change, or one that needs a person (published and differs, shared, two schemas, a file that cannot be sent, unreadable). |

A CI job can run `--dry-run` to fail while a custom kind is out of step with the partition, and `--confirm <partition>` to
bring development schemas in step after a merge.

## Errors

| Message | Cause |
| --- | --- |
| `--env-file names '<path>', which is not a file.` | The file named is not there. |
| `<file>(<line>): expected KEY=VALUE (or a # comment).` | A line of the env file is not a setting. |
| `No partition to register in: name it with --partition, or set OSDU_DATA_PARTITION in <where>.` | No partition was given. |
| `The connection to OSDU needs <names>, which <where> does not set.` | A connection variable is missing. |
| `OSDU_URL is not an absolute http or https URL: ...` | The base URL is not a URL. |
| `OSDU's Schema service could not be read in partition <id> at <url>: ...` | The first read failed: the sign-in, the network path, or `service.schema-service.viewers`. One message stops the run. |
| `--confirm names '<x>', and the schemas would be written in partition <id>: ...` | The partition typed back is not the one written in. |
| `--kind <kind> is an OSDU kind, ...` / `--kind names <kind>, which no template folder under <dir> holds; ...` | `--kind` names a kind the run does not take. |
| `--dry-run writes nothing and --confirm writes without asking: give one of them.` | Both were given. |
| `--json writes one JSON document and asks nothing: ...` | `--json` without `--dry-run` or `--confirm`. |

## Examples

See where every custom kind stands in the partition `.sqlflow/env` names, writing nothing:

```bash
sqlflow template register --dry-run
```

Register the repository's custom kinds in `dev` from another env file, answering at the terminal:

```bash
sqlflow template register ~/src/welldb-estate --env-file ~/env/dev.env
```

In a CI job, after a merge, bring every development schema in step without asking:

```bash
sqlflow template register --confirm dev --json > register.json
```

Publish one kind once it is final:

```bash
sqlflow template register --kind welldb:wks:master-data--Facility:1.1.0 --publish
```

## Related

- [`sqlflow template`](template.md): the template folders, and writing a template file into one.
- [Templates](../concepts/templates.md): templates of one's own and linked templates.
- [Linked records](../guides/linked-records.md): the custom kind beside the canonical one.
- [Dry run](../concepts/dry-run.md#the-schema-service): a record held to the Schema service's schema.
