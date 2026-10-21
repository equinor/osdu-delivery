---
id: delivery-cli-template
title: "sqlflow template: a repository's template folders, and the saved templates of the library"
type: cli-command
summary: "Read a working copy's template folders, write a template into one from the OSDU data definitions or a file, and list, show or delete the saved templates."
keywords:
  - template
  - template folders
  - templates folder
  - template capture
  - template import
  - osdu schema
  - data definitions
  - kind version
  - schema bundle
  - mapping template pin
  - release tag
  - osdu.template
  - offline schema
  - template file
  - template of one's own
  - show --out
  - folders --for
cliCommand: template
related:
  - delivery-concept-templates
  - delivery-flow-mapping
  - delivery-guide-writing-a-mapping
  - delivery-cli-check
  - delivery-guide-getting-started
  - delivery-concept-gui
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryVerbs.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryCliModule.cs
  - osdu/src/SqlFlow.Delivery/Templates/TemplateFolders.cs
  - osdu/src/SqlFlow.Delivery/Templates/TemplateChain.cs
  - osdu/src/SqlFlow.Delivery/Templates/TemplateStore.cs
  - osdu/src/SqlFlow.Delivery/Templates/OsduDataDefinitions.cs
  - osdu/src/SqlFlow.Delivery/Templates/TemplateSources.cs
  - osdu/src/SqlFlow.Delivery/Templates/OsduTemplate.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/SchemaSnapshot.cs
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Configuration/DeliveryModuleOptions.cs
  - sqlflow/src/SqlFlow.Core/Runs/RunActors.cs
---

# sqlflow template

OSDU Delivery's command line is `sqlflow`: SQLFlow's CLI with the OSDU verbs added. `template` is one of those verbs. It
reads a repository's template folders, where the documents of a repository read the templates they fill, writes a
template into one, and lists, shows and deletes the saved templates of the library. What a template is, how a document's
template is settled from its folders, and how versions compare is in [Templates](../concepts/templates.md); this page is
the verb.

## Synopsis

```bash
sqlflow template folders [<dir>] [--for <document>]                                      [--json]
sqlflow template capture --kind <kind> [--release <tag>] [--out <file.json>]  [--db <conn-ref>] [--json]
sqlflow template import <schema.json> --kind <kind> [--release <tag>] [--out <file.json>] [--db <conn-ref>] [--json]
sqlflow template import --from-dir <dir> --kind <kind> [--out <file.json>]    [--db <conn-ref>] [--json]
sqlflow template list                                                         [--db <conn-ref>] [--json]
sqlflow template show --kind <kind> [--version <version>] [--out <file.json>] [--db <conn-ref>] [--json]
sqlflow template delete --kind <kind> --version <version>                     [--db <conn-ref>]
```

## Description

A template is one OSDU kind's JSON schema, bundled so that every `$ref` points into its own `definitions`. Its version is
its content: the first 16 hexadecimal characters of the hash of the canonical schema, so every copy of one content is the
same version. A repository keeps the templates its documents fill as files in its template folders, folders named
`templates`, and a document reads those on its path; `sqlflow check` and every run read the files there, and need no
database for them.

`folders` reads a working copy's template folders from disk, with no database: what a developer runs before committing a
template, or a mapping that names its template's kind alone. `capture` and `import` with `--out` write a template file
into a folder; given the module's database, they also save the template in the library. `list`, `show` and `delete` work
on the library, the saved templates of the module's database, which the Templates page, the explorer and the mapping
builder read and no repository document does, and need the catalog connection (`--db`, else `${env:SQLFLOW_CATALOG_DB}`), or `SQLFLOW_OSDU_DB` when the
`osdu` schema has a database of its own (see [sqlflow db](db.md)). The GUI's Templates page saves templates through the
control plane instead, into the same table.

Every template saved or written is checked the same way, whatever its source:

- the schema describes the kind asked for (its `x-osdu-schema-source`, when it declares one, is that kind; a schema that
  declares none is given it, so the file says which kind it holds);
- every reference is resolved inside the schema;
- it declares a `data` property, as every OSDU record does.

### folders

Without `--for`, every template folder under `<dir>` (by default the repository's top: the nearest folder holding `.git`
above the current folder, or the current folder), in path order, each file with the template it holds or why it holds
none. A folder named like a template folder but not exactly (`Templates`, or `template` holding `.json` files), and a template folder reached through a link,
are warned of on stderr, since no document reads them.

```text
templates
  templates/osdu_wks_master-data--Wellbore_1.3.0.json  osdu:wks:master-data--Wellbore:1.3.0  58d6bdbd9d066a06
welldb/templates
  welldb/templates/osdu_wks_work-product-component--WellLog_1.4.0.json  osdu:wks:work-product-component--WellLog:1.4.0  26a3c3441882db4f
  welldb/templates/notes.json  holds no template: the schema names no kind. A template file declares the kind it holds in x-osdu-schema-source (...)
```

With `--for <document>`, the template folders that document reads, nearest first, with their files, and the version
each kind they hold settles to for a document that names the kind alone, or why it does not settle. `<dir>`, given, is
the repository's top the walk stops at.

```text
welldb/mappings/WellLog@1.4.0.yaml reads 2 template folder(s), nearest first:
welldb/templates
  welldb/templates/osdu_wks_work-product-component--WellLog_1.4.0.json  osdu:wks:work-product-component--WellLog:1.4.0  26a3c3441882db4f
templates
  templates/osdu_wks_master-data--Wellbore_1.3.0.json  osdu:wks:master-data--Wellbore:1.3.0  58d6bdbd9d066a06
A kind named without a version settles to:
  osdu:wks:master-data--Wellbore:1.3.0  58d6bdbd9d066a06  templates/osdu_wks_master-data--Wellbore_1.3.0.json
  osdu:wks:work-product-component--WellLog:1.4.0  26a3c3441882db4f  welldb/templates/osdu_wks_work-product-component--WellLog_1.4.0.json
```

With `--json`, `folders` prints `{ root, folders: [{ path, files: [{ path, kind, version, problem }] }], warnings }`, and
`folders --for` prints `{ document, looked, folders, kinds: [{ kind, version, file, problem }] }`.

### Where `capture` reads from

`capture` reads the OSDU data definitions, the Open Group's public repository of the OSDU schemas
(`https://community.opengroup.org/osdu/data/data-definitions`), through its GitLab API. A release is a tag of that
repository named `v` and a dotted version; `--release` names one exactly, and without it the newest release is used. The
first time a release is read, its whole `Generated` folder is downloaded as one archive and unpacked into a local copy,
and every later read of that release comes from disk:

| Item | Value |
| --- | --- |
| Local copy | `sqlflow/osdu-data-definitions` under the user's temp folder, one folder per release commit. A control plane on the same machine shares it unless `Osdu:SchemaRepository:CacheDirectory` moves its own. A release is read from disk only while every file its download listed in `release-files.json` is there; one a temp cleanup emptied is downloaded again and put back whole. |
| Release list | Kept on disk beside the copy and read again from the repository when it is older than a day. A release name the list does not hold makes it read the list again when the list is over a minute old. When the repository cannot be reached, a list read before is used. |
| Download timeout | 15 minutes per release; 2 minutes per page of the tag list. |
| Repository | Always the public one. The mirror settings (`Osdu:SchemaRepository:ApiUrl`, `WebUrl`) apply to the control plane only. |

The kind's file is the one under `Generated` whose `x-osdu-schema-source` is the kind. It is bundled with every shared
schema it refers to (`../abstract/...`) from the same release. The saved template's origin records the release, the
first 12 characters of its commit and the file:
`OSDU data definitions <release> (<commit>) Generated/<path>`.

### What `import` accepts

- **A bundled file** (every `$ref` already points into its own `definitions`, the form `capture --out` writes) is saved as
  it is, and reads nothing from the network. Its origin is `file <name>`.
- **A file as the data definitions publish it**, which refers to the shared schemas beside it, is bundled with those
  schemas read from `--release` (the newest when left out), exactly as `capture` bundles. Its origin is
  `file <name>, references from OSDU data definitions <release> (<commit>)`. A schema of one's own built on OSDU's shared
  schemas is imported this way.
- **`--from-dir <dir>`** bundles the kind's schema from a local checkout of the data definitions, with no network
  access. `<dir>` is the checkout's `Generated` folder, the one holding the group folders such as `master-data`,
  `work-product-component` and `abstract`; the kind's file is read from `<group>/<Entity>.<version>.json` under it.
  A file not at its expected path is found by its file name anywhere under the folder. Its origin is
  `data definitions under <full path>`.

## Arguments

| Argument | Required | Description |
| --- | --- | --- |
| `<subcommand>` | yes | One of `folders`, `capture`, `import`, `list`, `show`, `delete` (case-insensitive). |
| `<dir>` | no | `folders`: the repository's top, the folder scanned or the one a document's walk stops at. |
| `<schema.json>` | `import` without `--from-dir` | The schema file to save. `capture` takes no file and refuses one. |

## Options

| Flag | Type | Default | Description |
| --- | --- | --- | --- |
| `--for <document>` | path | none | `folders`: the mapping or flow whose template folders to read, and what each kind settles to for it. |
| `--kind <kind>` | OSDU kind | none | Required for `capture`, `import`, `show` and `delete`: `authority:source:entityType:major.minor.patch`, such as `osdu:wks:work-product-component--WellLog:1.4.0`. |
| `--release <tag>` | release tag | the newest release | `capture`, and `import` of an unbundled file: the release of the data definitions to read. |
| `--out <file.json>` | path | none | `capture`, `import` and `show`: also write the template's bundled schema to this file, a file of a template folder (folders are created). `capture` and `import` write the schema they save, `show` the saved version, as the Templates page downloads it. With `--out`, `capture` and `import` need no database: without one they write the file alone. |
| `--from-dir <dir>` | path | none | `import` only: bundle from a local checkout instead of a file. |
| `--version <version>` | template version | the most recently saved | `show`: the version to lay out. `delete`: required. |
| `--db <conn-ref>` | connection reference | `${env:SQLFLOW_CATALOG_DB}` | The catalog database, which holds the `osdu` schema unless `SQLFLOW_OSDU_DB` names its own. |
| `--json` | flag | off | Writes JSON instead of text (not for `delete`). |

## Behavior and output

### capture and import

Both print one line: `saved template <kind> version <version>` for a new version, or
`template <kind> version <version> was already saved` when the content is a saved version. With `--json` they print the
template as `{ kind, version, capturedUtc, capturedBy, origin, outcome }`, where `outcome` is `created` or `unchanged`.
With `--out` the file is written before the template is saved, and noted on stderr (`Wrote the bundled schema of template
<kind> version <version> to <path>`), so the file is there even when the save then fails. On a host with no module
database, `--out` writes the file and prints `wrote template <kind> version <version>; it is not saved in the library,
since no module database is configured` (`--json`: `{ kind, version, outcome: "written" }`). The template records who
saved it as `cli:<user>@<machine>`.

### list

Every saved version, by kind and newest first:

```text
osdu:wks:master-data--Wellbore:1.3.0  58d6bdbd9d066a06  saved 2026-10-09 07:52:10Z by cli:alex@WS-0142  (OSDU data definitions <release> (<commit>) Generated/master-data/Wellbore.1.3.0.json)
osdu:wks:work-product-component--WellLog:1.4.0  26a3c3441882db4f  saved 2026-10-09 07:53:44Z by repository sync  (template folder file welldb/templates/osdu_wks_work-product-component--WellLog_1.4.0.json of repository <id>)
```

With none saved it prints `no templates saved yet`. `--json` prints the array of `{ kind, version, capturedUtc,
capturedBy, origin }`.

### show

One version laid out as a template: a header line `<kind> version <version>: <n> variable(s)`, then one line per variable
with its path, its shape and its notes. A shape is the value's type, `list of <type>`, `object`, `list of objects` or
`whole <type>`. The notes say `required` when the schema requires it, `written by OSDU Delivery` (the record id and kind)
or `set by OSDU` (what OSDU sets when it stores the record) for a variable no mapping fills, `points to <entity types>`
for a reference, and `unit <context>` for a value with a unit. With `--json` each variable is
`{ path, shape, type, itemType, required, role, relationships, unitContext, description }`, where `role` is `Mapping`,
`Engine` or `Osdu`.

With `--out <file.json>`, `show` also writes the saved version to the file, before it lays the version out, and notes
`Wrote the bundled schema of template <kind> version <version> to <path>` on stderr, so stdout and `--json` are as without
it. The file is the bundled schema, indented, with its keys in the order the schema was written: what the Templates page
downloads, and what `capture --out` writes. Importing it saves nothing new
([a template as a file](../concepts/templates.md#a-template-as-a-file)). A version saved before a template named its kind
is written as saved, with a note that no template folder reads it, and how to write it naming its kind.

### delete

Deletes one version from the library and prints `deleted template <kind> version <version>`. Nothing a repository holds
refuses it: a repository's documents read their templates from its template folders, never from the library.

### Errors

| Message | Cause |
| --- | --- |
| `Kind '<kind>' must be 'authority:source:entityType:major.minor.patch'.` | `capture` or `import` with a `--kind` that is not an OSDU kind. |
| `'sqlflow template capture' takes no flow: it saves the kind's schema from the OSDU data definitions.` | A file was given to `capture`. |
| `--for names '<path>', which is not a file; name the mapping or flow whose template folders to read.` | `folders --for` with a path that is not a file. |
| `Saved templates live in the module's database. Run 'sqlflow template' with --db <conn-ref>, or set the catalog variable; 'sqlflow template folders' reads a repository's template folders without one.` | `list`, `show`, `delete`, or `capture` or `import` without `--out`, on a host with no module database. |
| `The OSDU data definitions have no release '<tag>'; the latest is <tag>.` | `--release` names no release tag. |
| `The OSDU data definitions have no Generated/<path> at <release>.` | The release publishes no schema for the kind. |
| `The OSDU data definitions at <url> could not be reached for <what>: <cause>` | No network path to the repository. Use `import --from-dir` or a bundled file instead. |
| `<where>: the schema describes '<other kind>', not '<kind>'.` | The file is another kind's schema. |
| `<where>: the schema refers to '<ref>' outside itself. A template is saved from a bundled schema, where every reference is resolved into its definitions.` | A reference was left unresolved. |
| `<where>: the schema declares no 'data' property, so it does not describe an OSDU record.` | The file is not a record schema (an abstract building block, for example). |
| `The data definitions folder '<dir>' does not exist.` / `No schema file '<path>' under '<dir>'.` | `--from-dir` names the wrong folder. |
| `There is no saved template for '<kind>'.` / `There is no template <kind> version <version>.` | `show` or `delete` of something not saved. |
| `The bundled schema of template <kind> version <version> could not be written to '<path>': <reason>` | `--out` names a file that cannot be written: a folder on the way is a file, the disk is full, or the account may not write there. |

Each is printed as `ERROR  <message>` on stderr; a missing `--kind`, file or `--version` is a usage error followed by the
verb's usage lines.

## Examples

Write the schema a WellLog mapping fills into the template folder beside its project, from the newest release, in a
working copy with no database:

```bash
sqlflow template capture --kind osdu:wks:work-product-component--WellLog:1.4.0 --out welldb/templates/osdu_wks_work-product-component--WellLog_1.4.0.json
```

```text
wrote template osdu:wks:work-product-component--WellLog:1.4.0 version <16 hexadecimal characters>; it is not saved in the library, since no module database is configured
```

See what the mapping now reads, before committing:

```bash
sqlflow template folders --for welldb/mappings/WellLog@1.4.0.yaml
```

Bundle a template of one's own, built on OSDU's shared schemas, into the folder:

```bash
sqlflow template import drafts/Facility.1.0.0.json --kind acme:wks:master-data--Facility:1.0.0 --out welldb/templates/acme_wks_master-data--Facility_1.0.0.json
```

Write the saved versions an estate's mappings pinned before template folders into its repository, from its database:

```bash
sqlflow template show --kind osdu:wks:master-data--Wellbore:1.3.0 --version 58d6bdbd9d066a06 --out templates/osdu_wks_master-data--Wellbore_1.3.0.json --db '${env:SQLFLOW_CATALOG_DB}'
```

Bundle from a local checkout of the data definitions, and remove a version nothing holds or fills any more:

```bash
sqlflow template import --from-dir ~/src/data-definitions/Generated --kind osdu:wks:master-data--Wellbore:1.3.0 --out templates/osdu_wks_master-data--Wellbore_1.3.0.json
sqlflow template delete --kind osdu:wks:master-data--Wellbore:1.3.0 --version 58d6bdbd9d066a06
```

## Exit behavior

| Exit code | Meaning |
| --- | --- |
| 0 | The subcommand did what it was asked, including a save of a version that was already saved, and a `folders` listing that found files holding no template (those are listed). |
| 1 | A usage error or a refusal from the table above, printed as `ERROR  <message>`, or a database connection that could not be resolved. |
| 130 | Interrupted with Ctrl+C. |

## See also

- [Templates](../concepts/templates.md): template folders, how a document's template is settled, the library, versions and comparing them.
- [The mapping document](../flow/mapping.md): the `template` key a mapping names its template with.
- [Writing a mapping](../guides/writing-a-mapping.md): from a template to a valid mapping.
- [sqlflow check](check.md): checks a flow's mapping against the template it fills.
- [The GUI](../concepts/gui.md): the Templates page, which browses the data definitions, saves templates through the control plane and downloads a saved one as a file.
