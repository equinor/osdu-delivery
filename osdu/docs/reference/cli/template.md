---
id: delivery-cli-template
title: "sqlflow template: a repository's template folders, and template files written into them"
type: cli-command
summary: "Read a working copy's template folders, and write a template file into one from the OSDU data definitions or a schema file; no database is needed."
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
  - offline schema
  - template file
  - template of one's own
  - download template
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
  - osdu/src/SqlFlow.Delivery/Templates/OsduDataDefinitions.cs
  - osdu/src/SqlFlow.Delivery/Templates/TemplateSources.cs
  - osdu/src/SqlFlow.Delivery/Templates/OsduTemplate.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/SchemaSnapshot.cs
---

# sqlflow template

OSDU Delivery's command line is `sqlflow`: SQLFlow's CLI with the OSDU verbs added. `template` is one of those verbs. It
reads a repository's template folders, where the documents of a repository read the templates they fill, and writes a
template file into one. What a template is, how a document's template is settled from its folders, and how versions
compare is in [Templates](../concepts/templates.md); this page is the verb.

## Synopsis

```bash
sqlflow template folders [<dir>] [--for <document>]                                  [--json]
sqlflow template capture --kind <kind> [--release <tag>] --out <file.json>           [--json]
sqlflow template import <schema.json> --kind <kind> [--release <tag>] --out <file.json> [--json]
sqlflow template import --from-dir <dir> --kind <kind> --out <file.json>             [--json]
```

## Description

A template is one OSDU kind's JSON schema, bundled so that every `$ref` points into its own `definitions`. Its version is
its content: the first 16 hexadecimal characters of the hash of the canonical schema, so every copy of one content is the
same version. A repository keeps the templates its documents fill as files in its template folders, folders named
`templates`, and a document reads those on its path; `sqlflow check` and every run read the files there. The database
keeps no template: a repository's sync records which file holds which template, and every reader reads the file.

Every subcommand works on files and needs no database. `folders` reads a working copy's template folders from disk: what
a developer runs before committing a template, or a mapping that names its template's kind alone. `capture` and `import`
write a template file into a folder, which the repository's next sync records once it is committed. The GUI's Templates
page downloads the same file from the OSDU data definitions.

Every template written is checked the same way, whatever its source:

- the schema describes the kind asked for (its `x-osdu-schema-source`, when it declares one, is that kind; a schema that
  declares none is given it, so the file says which kind it holds);
- every reference is resolved inside the schema;
- it declares a `data` property, as every OSDU record does.

### folders

Without `--for`, every template folder under `<dir>` (by default the repository's top: the nearest folder holding `.git`
above the current folder, or the current folder), in path order, each file with the template it holds or why it holds
none. A folder named like a template folder but not exactly (`Templates`, or `template` holding `.json` files), and a
template folder reached through a link, are warned of on stderr, since no document reads them.

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
schema it refers to (`../abstract/...`) from the same release: the file the Templates page's Browse OSDU tab downloads.

### What `import` accepts

- **A bundled file** (every `$ref` already points into its own `definitions`, the form `capture --out` writes) is written
  as it is, and reads nothing from the network.
- **A file as the data definitions publish it**, which refers to the shared schemas beside it, is bundled with those
  schemas read from `--release` (the newest when left out), exactly as `capture` bundles. A schema of one's own built on
  OSDU's shared schemas is written this way.
- **`--from-dir <dir>`** bundles the kind's schema from a local checkout of the data definitions, with no network
  access. `<dir>` is the checkout's `Generated` folder, the one holding the group folders such as `master-data`,
  `work-product-component` and `abstract`; the kind's file is read from `<group>/<Entity>.<version>.json` under it.
  A file not at its expected path is found by its file name anywhere under the folder.

## Arguments

| Argument | Required | Description |
| --- | --- | --- |
| `<subcommand>` | yes | One of `folders`, `capture`, `import` (case-insensitive). |
| `<dir>` | no | `folders`: the repository's top, the folder scanned or the one a document's walk stops at. |
| `<schema.json>` | `import` without `--from-dir` | The schema file to write as a template. `capture` takes no file and refuses one. |

## Options

| Flag | Type | Default | Description |
| --- | --- | --- | --- |
| `--for <document>` | path | none | `folders`: the mapping or flow whose template folders to read, and what each kind settles to for it. |
| `--kind <kind>` | OSDU kind | none | Required for `capture` and `import`: `authority:source:entityType:major.minor.patch`, such as `osdu:wks:work-product-component--WellLog:1.4.0`. |
| `--release <tag>` | release tag | the newest release | `capture`, and `import` of an unbundled file: the release of the data definitions to read. |
| `--out <file.json>` | path | none | Required for `capture` and `import`: the template file to write, a file of a template folder (the folders on the way are created). |
| `--from-dir <dir>` | path | none | `import` only: bundle from a local checkout instead of a file. |
| `--json` | flag | off | Writes JSON instead of text. |

## Behavior and output

### capture and import

Both write the template's bundled schema to `--out` (indented, with its keys in the order the schema was written) and
print one line, `wrote template <kind> version <version> to <full path>`. With `--json` they print
`{ kind, version, file, outcome: "written" }`. A file written outside a folder named `templates` is noted on stderr
(`<path> is not in a folder named templates, so no document reads it there; move it into a template folder on the path
of the documents that fill it.`), since no document would read it there. Writing the same schema again writes the same
version: a version is the hash of the canonical form, which indentation and key order do not change.

### Errors

| Message | Cause |
| --- | --- |
| `name the template file to write with --out, in a template folder of the repository (<folder>/templates/<file>.json): a template is a file the repository holds, which its next sync records, and the database keeps none.` | `capture` or `import` without `--out`. |
| `Kind '<kind>' must be 'authority:source:entityType:major.minor.patch'.` | `capture` or `import` with a `--kind` that is not an OSDU kind. |
| `'sqlflow template capture' takes no flow: it writes the kind's schema from the OSDU data definitions.` | A file was given to `capture`. |
| `'<verb>' is not a template subcommand: folders, capture or import.` | Another subcommand, such as the `list`, `show` and `delete` of earlier releases, which kept templates in the database. |
| `--for names '<path>', which is not a file; name the mapping or flow whose template folders to read.` | `folders --for` with a path that is not a file. |
| `The OSDU data definitions have no release '<tag>'; the latest is <tag>.` | `--release` names no release tag. |
| `The OSDU data definitions have no Generated/<path> at <release>.` | The release publishes no schema for the kind. |
| `The OSDU data definitions at <url> could not be reached for <what>: <cause>` | No network path to the repository. Use `import --from-dir` or a bundled file instead. |
| `<where>: the schema describes '<other kind>', not '<kind>'.` | The file is another kind's schema. |
| `<where>: the schema refers to '<ref>' outside itself. A template file holds a bundled schema, where every reference is resolved into its definitions.` | A reference was left unresolved. |
| `<where>: the schema declares no 'data' property, so it does not describe an OSDU record.` | The file is not a record schema (an abstract building block, for example). |
| `The data definitions folder '<dir>' does not exist.` / `No schema file '<path>' under '<dir>'.` | `--from-dir` names the wrong folder. |
| `The bundled schema of template <kind> version <version> could not be written to '<path>': <reason>` | `--out` names a file that cannot be written: a folder on the way is a file, the disk is full, or the account may not write there. |

Each is printed as `ERROR  <message>` on stderr; a missing `--kind`, `--out` or file is a usage error followed by the
verb's usage lines.

## Examples

Write the schema a WellLog mapping fills into the template folder beside its project, from the newest release:

```bash
sqlflow template capture --kind osdu:wks:work-product-component--WellLog:1.4.0 --out welldb/templates/osdu_wks_work-product-component--WellLog_1.4.0.json
```

```text
wrote template osdu:wks:work-product-component--WellLog:1.4.0 version <16 hexadecimal characters> to /work/repo/welldb/templates/osdu_wks_work-product-component--WellLog_1.4.0.json
```

See what the mapping now reads, before committing:

```bash
sqlflow template folders --for welldb/mappings/WellLog@1.4.0.yaml
```

Bundle a template of one's own, built on OSDU's shared schemas, into the folder:

```bash
sqlflow template import drafts/Facility.1.0.0.json --kind acme:wks:master-data--Facility:1.0.0 --out welldb/templates/acme_wks_master-data--Facility_1.0.0.json
```

Bundle from a local checkout of the data definitions, with no network access:

```bash
sqlflow template import --from-dir ~/src/data-definitions/Generated --kind osdu:wks:master-data--Wellbore:1.3.0 --out templates/osdu_wks_master-data--Wellbore_1.3.0.json
```

## Exit behavior

| Exit code | Meaning |
| --- | --- |
| 0 | The subcommand did what it was asked, including a `folders` listing that found files holding no template (those are listed). |
| 1 | A usage error or a refusal from the table above, printed as `ERROR  <message>`. |
| 130 | Interrupted with Ctrl+C. |

## See also

- [Templates](../concepts/templates.md): template folders, how a document's template is settled, what the sync records, versions and comparing them.
- [The mapping document](../flow/mapping.md): the `template` key a mapping names its template with.
- [Writing a mapping](../guides/writing-a-mapping.md): from a template to a valid mapping.
- [sqlflow check](check.md): checks a flow's mapping against the template it fills.
- [The GUI](../concepts/gui.md): the Templates page, which lists the synced repositories' template files and downloads a kind from the OSDU data definitions as a file.
