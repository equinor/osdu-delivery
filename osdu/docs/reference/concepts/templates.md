---
id: delivery-concept-templates
title: "Templates: OSDU schemas a repository's template folders hold, settled for each document by the folders on its path"
type: concept
summary: "Templates as files of a repository's template folders: what the sync records, how a document's template is settled, downloading one, comparing versions."
keywords:
  - template
  - template folder
  - templates folder
  - osdu schema
  - template version
  - kind alone
  - nearest folder
  - data definitions
  - capture
  - import
  - pin
  - template variables
  - compare versions
  - breaking change
  - kind
  - custom template
  - template of one's own
  - sqlflow template
  - osdu:schemarepository
  - download template
  - template file
  - repository sync
  - browse osdu
related:
  - delivery-flow-mapping
  - delivery-cli-template
  - delivery-concept-preflight
  - delivery-flow-mapping-lookups
  - delivery-guide-writing-a-mapping
  - delivery-concept-lineage
  - delivery-concept-architecture
  - delivery-table-repository-template
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Templates/OsduTemplate.cs
  - osdu/src/SqlFlow.Delivery/Templates/TemplateChain.cs
  - osdu/src/SqlFlow.Delivery/Templates/TemplateFolders.cs
  - osdu/src/SqlFlow.Delivery/Templates/TemplateSource.cs
  - osdu/src/SqlFlow.Delivery/Templates/TemplateSources.cs
  - osdu/src/SqlFlow.Delivery/Templates/OsduDataDefinitions.cs
  - osdu/src/SqlFlow.Delivery/Templates/TemplateComparison.cs
  - osdu/src/SqlFlow.Delivery/Templates/TemplatePath.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/SchemaSnapshot.cs
  - osdu/src/SqlFlow.Delivery/Json/CanonicalJson.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/RenderContext.cs
  - osdu/src/SqlFlow.Delivery/Engine/RenderResolver.cs
  - osdu/src/SqlFlow.Delivery/Engine/FlowRuntime.cs
  - osdu/src/SqlFlow.Delivery/Rendering/MappingRenderer.cs
  - osdu/src/SqlFlow.Delivery/Documents/MappingMapper.cs
  - osdu/src/SqlFlow.Delivery/Documents/MappingLineage.cs
  - osdu/src/SqlFlow.Delivery/Documents/DeliveryDocumentLoader.cs
  - osdu/src/SqlFlow.Delivery/Catalog/DeliveryCatalogSync.cs
  - osdu/src/SqlFlow.Delivery/Catalog/SyncedTemplates.cs
  - osdu/src/SqlFlow.Delivery/Catalog/RepositoryTemplates.cs
  - osdu/src/SqlFlow.Delivery/Catalog/RepositoryFolders.cs
  - osdu/src/SqlFlow.Delivery/Engine/Assertions/AssertionTemplates.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionBlueprint.cs
  - osdu/src/SqlFlow.Delivery/Engine/Search/ExplorerChecks.cs
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryVerbs.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryTemplateEndpoints.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Configuration/DeliveryModuleOptions.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/DeliveryControlPlaneModule.cs
  - osdu/gui/src/features/delivery/TemplatesPage.tsx
  - osdu/gui/src/features/delivery/TemplatesBrowseTab.tsx
  - osdu/gui/src/features/delivery/MappingBuilderPage.tsx
  - osdu/gui/src/features/delivery/templateFormat.ts
  - osdu/samples/welldb/templates/osdu_wks_work-product-component--WellLog_1.4.0.json
  - osdu/samples/welldb/templates/osdu_wks_master-data--Wellbore_1.3.0.json
---

# Templates: OSDU schemas a repository's template folders hold, settled for each document by the folders on its path

A template is an OSDU schema laid out as the record of one kind, with a variable for every property the schema declares.
A [mapping](../flow/mapping.md) fills one template: it names the template's kind under `template`, and says for each
variable it fills where the value comes from. Nobody edits a template; it is the schema, and a version of it never
changes.

A repository keeps the templates its documents fill as files in **template folders**, folders named `templates`, beside
the mappings and flows that read them or above them. A document reads the template folders on its path, and the one it
names settles to a version there ([Template folders](#template-folders)). The repository is the only place a template
lives: its sync records which file holds which template ([What the sync records](#what-the-sync-records)), and whoever
reads a template (a run, `sqlflow check`, lineage, the GUI, the MCP server) reads the file. The database keeps no copy
of any template. Adding a template is committing its file to a template folder, and removing one is deleting the file;
the next sync records either. A file is downloaded from the OSDU data definitions on the Templates page, or written by
`sqlflow template capture --out`; a template of one's own is a file like any other
([Templates of one's own](#templates-of-ones-own)).

```yaml
template:
  kind: osdu:wks:work-product-component--WellLog:1.4.0
  version: 26a3c3441882db4f    # optional: left out, the mapping fills the one version its nearest folder holds
```

## What a template holds

Every property of the schema becomes a variable, named by its path in the record and prefixed with `osdu.`; `[]` marks
the step into the items of an array of objects. Each variable carries the schema's type, whether the schema requires
it in the object that holds it, the entity types it points to (`x-osdu-relationship`), its pattern, its unit context
(`x-osdu-frame-of-reference`), its title and OSDU's description. A few variables of the WellLog 1.4.0 template:

| Variable | Shape | What the schema says |
| --- | --- | --- |
| `osdu.id` | string | written by OSDU Delivery |
| `osdu.kind` | string | required; written by OSDU Delivery |
| `osdu.acl.owners` | list of string | required |
| `osdu.legal.legaltags` | list of string | required |
| `osdu.tags` | object with free keys | each key takes a string; a mapping fills `osdu.tags.<name>` |
| `osdu.data.Name` | string | |
| `osdu.data.WellboreID` | string | points to `master-data--Wellbore` |
| `osdu.data.TopMeasuredDepth` | number | unit context `UOM:length` |
| `osdu.data.VerticalMeasurement.VerticalMeasurement` | number | inside an object |
| `osdu.data.Curves` | list of objects | a `$forEach` or a list of objects fills it |
| `osdu.data.Curves[].CurveUnit` | string | points to `reference-data--UnitOfMeasure` |
| `osdu.data.ExtensionProperties` | open object | a mapping lays out anything inside it |

A variable takes one of these shapes:

| Shape | Filled by |
| --- | --- |
| A value (text, number, integer, boolean) | One value node or literal. |
| A list of values | A literal list, a value node (one value becomes a list of one), or a [list of values](../flow/mapping-values.md#lists-of-values). |
| An object | The nodes of the properties it holds, or a `$value` written whole. |
| A list of objects | A `$forEach` over a child dataset, or a [list of objects](../flow/mapping-values.md#lists-of-objects). |
| Whole | An object or list the schema does not break into properties (a list of a choice of shapes, such as `meta` or `data.GeoContexts`): a literal written whole, or a cached field holding one. An object with free keys (`tags`) takes entries under any key, and an [open object](../flow/mapping-values.md#open-objects) (`data.ExtensionProperties`) takes anything laid out inside it. |

Some variables are not a mapping's to fill. `osdu.id` and `osdu.kind` are written by OSDU Delivery (the id from the
mapping's key, the kind from the template), and `osdu.version`, `osdu.createTime`, `osdu.createUser`, `osdu.modifyTime`
and `osdu.modifyUser` are set by OSDU when the record is stored. A list of objects inside the items of another array is
listed for reference and cannot be filled, since a path steps into one array at most. The walk follows nested objects
twelve levels deep.

## Template versions

A template's version is its content: the first 16 hexadecimal characters of the SHA-256 of the bundled schema written
as canonical JSON (keys in ordinal order, null properties left out, numbers in their shortest form, no whitespace;
the full rules are in [change detection](change-detection.md#the-canonical-json-a-hash-is-computed-over)). Three
consequences follow.

- **A version never changes.** A file whose content changes holds another version; every copy of one content, in any
  folder, file or repository, is the same version.
- **The version identifies the schema exactly**, whatever release, file or folder it came from, so a record is always
  checked against the schema it was rendered for. Indentation, key order and line endings do not move it.
- **A render records it.** The template version enters every record's render context, and the ledger keeps the context
  of every render. Moving a mapping to another version, like changing the mapping itself, makes the next run read the
  whole scope again; each record's own document hash decides whether it is sent ([change detection](change-detection.md)).

## Template folders

A template folder is a folder named exactly `templates` (lower case, on every system: a folder named `Templates`, or
`template` holding `.json` files, is not read, and the sync warns of it), anywhere in a repository; a repository holds
as many as its projects need. Its template files are the `.json` files in it and in the folders under it, except:

- a folder named `templates` inside it, which is a template folder of its own;
- the folders a repository sync skips: a name starting with a dot, `bin`, `obj`, `node_modules` and `runs`;
- a link (a symbolic link or a junction), which is never followed, so a folder holds only what is in it; the sync warns
  of a template folder reached through one.

Other files (a `README.md`) are left alone. A folder is read for its first 1,000 `.json` files in path order, and a file
of more than 8 MB is not read.

A template file is a bundled record schema: every `$ref` resolved into its own `definitions`, a `data` property
declared, and its kind named in its own `x-osdu-schema-source`, as every schema of the OSDU data definitions names it.
The file's name is never read for the kind; the convention is the kind with each colon an underscore
(`osdu_wks_work-product-component--WellLog_1.4.0.json`), which is what a download and `capture --out` name it. A file
that is not one is reported by the sync with the reason, and holds no template:

| Problem | What is said |
| --- | --- |
| Not JSON, or not an object | `the schema is not valid JSON (...)`, `the schema is not a JSON object.` |
| No kind | `the schema names no kind. A template file declares the kind it holds in x-osdu-schema-source (...)` |
| Not a record kind | `x-osdu-schema-source '<value>' is not a record kind; it must be 'authority:source:entityType:major.minor.patch'.` |
| Not bundled | `the schema refers to '<ref>' outside itself. A template file holds a bundled schema, where every reference is resolved into its definitions.` |
| A missing definition | `the schema refers to definition '<name>', which it does not contain.` |
| No `data` | `the schema declares no 'data' property, so it does not describe an OSDU record.` |
| Too large | `the file is <n> bytes, and a template file holds one bundled schema of at most 8388608 bytes` |

### What a document reads

A document (a mapping, an assertion flow, a dimension flow) reads the template folders on its path: the folder named
`templates` beside it and the one in every folder above it, nearest first, up to the top of its repository. The top is
the folder the sync reads, or, for a working copy, the nearest folder holding `.git` (a clone's folder, or a worktree's
file); with neither, the walk stops sixteen folders up, as the walk to a flow's `mappings` folder does. A folder beside the
repository, or in a sibling project's folder, is never read.

```text
templates/                                        osdu_wks_master-data--Wellbore_1.3.0.json
welldb/templates/                                 osdu_wks_work-product-component--WellLog_1.4.0.json
welldb/mappings/WellLog@1.4.0.yaml                reads welldb/mappings/templates, welldb/templates, templates
welldb/assertions/welldb-welllog-assertions.yaml  reads welldb/assertions/templates, welldb/templates, templates
```

The template a document names settles by one rule:

| The document names | It reads |
| --- | --- |
| A kind and a version | That version, from the nearest folder on its path that holds it. A folder nearer to it holding another version of the kind does not stop the walk. |
| A kind alone | The nearest folder on its path holding the kind. That folder must hold one version of it: two files of one version are one version, and two versions are refused, naming both, rather than one being guessed. A folder further up is not looked at once one holds the kind. |

So a repository of several projects keeps each project's templates beside them, and two projects may hold different
versions of one kind without a document of either reading the other's; a template every project fills sits once, in a
folder above them. A version held by a folder off the document's path, or by another repository, is never its template.

A template that does not settle says where the document looked, what its path holds of the kind, which files on its path
could not be read (one of them may be the one meant), where else the repository holds it, and how to put it there:

```text
no template folder on the path of welldb/mappings/WellLog@1.4.0.yaml holds template
osdu:wks:work-product-component--WellLog:1.4.0 version 26a3c3441882db4f. On its path welldb/templates/
osdu_wks_work-product-component--WellLog_1.4.0.json holds version 0f21b6a0c3d89e14. It reads welldb/templates.
The repository holds version 26a3c3441882db4f of osdu:wks:work-product-component--WellLog:1.4.0 at
archive/templates/WellLog.json, which is not on its path. Put the file holding version 26a3c3441882db4f in one of them:
copy it from where the repository holds it, or, for a version a release of the OSDU data definitions publishes, download
it on the Templates page or write it with 'sqlflow template capture --kind osdu:wks:work-product-component--WellLog:1.4.0
--release <tag> --out welldb/templates/osdu_wks_work-product-component--WellLog_1.4.0.json'.
```

```text
welldb/templates, the nearest template folder on the path of welldb/mappings/WellLog@1.4.0.yaml holding
osdu:wks:work-product-component--WellLog:1.4.0, holds 2 versions of it: 26a3c3441882db4f
(welldb/templates/WellLog.json), 0f21b6a0c3d89e14 (welldb/templates/WellLog-next.json). Name the version to use, or keep
one version of the kind in the folder.
```

## How documents name their template

- **A mapping** names its template under `template`: its `kind`, and its `version` when it means one exactly. Without a
  version it fills the one version its nearest folder holding the kind holds, and follows the file: a changed file is a
  new version, which the next sync records and the next run renders against. With a version it fills exactly that one,
  and a folder that no longer holds it leaves the mapping unable to load until it does ([mapping](../flow/mapping.md)).
- **A search** names the schema of the kind it searches the same way, `schema.kind` and an optional `schema.version`,
  settled from the folders on the mapping's path ([mapping lookups](../flow/mapping-lookups.md)).
- **An assertion test** reads the template of its `kind` from the folders on its flow's path: the version its `template`
  names, the version the mapping it names fills (`template: WellLog@1.5.0`), or, naming neither, the one version its
  nearest folder holds. The records its known values reach are described by the newest version of their type the flow's
  folders give ([assertion](../flow/assertion.md#checked-against-the-template)).
- **A dimension** reads, for each kind its pattern matches among the templates its flow's folders hold, the one version
  of that kind they settle to, and describes the records it reaches by id by the newest version of their type
  ([dimension](../flow/dimension.md)).

## What the sync records

A repository sync scans every template folder of the repository it reads and records each file as one row of
[`osdu.RepositoryTemplate`](../tables/repository-template.md): its path, the template folder it is in, the kind and
version it holds or why it holds none, and when a sync first and last saw it. That row is all the database keeps of a
template: an index of which file holds what, never the schema. A file added to a folder is a new row at the next sync,
a file changed has its row's version moved, and a file deleted has its row removed, so the index is always what the
repository's last sync read.

The same sync settles every mapping against the files it is scanning, and records the version each settled to and the
file it read (`TemplateVersion` and `TemplatePath` of [`osdu.Mapping`](../tables/mapping.md)). A mapping whose template
does not settle is invalid, keeps the reference it is known by, and its message is the one above.

## Where templates are read

Every reader reads the file. What tells readers apart is where the file is:

| Who | Reads |
| --- | --- |
| A run: the render, its preflight, the validation gate; `sqlflow check`, `preview` and `values`; an assertion or dimension run | The files of the template folders on the document's path, in the checkout the run reads. A record rendered for an exact version is checked against that version while a folder on the mapping's path holds it, and is `unverified` once none does. |
| Lineage and the OSDU order it takes | The files, on the mapping's path, in the checkout being scanned ([lineage](lineage.md)). |
| The repository sync | Every template folder of the repository it is reading, as [above](#what-the-sync-records). |
| The control plane's pages about a repository's documents: an assertion flow's board, a dimension's blueprint and views, a source's interface order, the search terms, a mapping's properties, shape and coverage, the mapping builder, and `GET /api/v1/delivery/templates/resolve` | The files the repository's last sync recorded, settled by the same rule, each read from the repository's synced folder. |
| The Templates page, the explorer's checks against a repository's template, and the MCP server's template tools | Every file every synced repository's last sync recorded, each read from its repository's synced folder. A kind named without a version (the explorer's default) is the version the repository synced most recently holds. |
| `sqlflow template folders` | The files, in a working copy, with no database ([template](../cli/template.md)). |

A repository's synced folder is found the way a node finds a run's checkout: a source that reads a local path is read
where it lives; any other repository is read in the folder its last sync read when this host sees it (the control plane
that synced it, or a node on the same machine), and otherwise the commit its last sync pulled is checked out from its
remote, with its source's credential, into the cache a run's commit is checked out into. A file read this way must
still hold what the sync recorded: one that changed since is refused, saying to sync the repository again, and one whose
repository cannot be had on this host says why. A host with no catalog, such as the command line without `--db`, reads
templates only from a working copy.

## Where templates come from

### The OSDU data definitions

The canonical OSDU schemas are the OSDU data definitions, the Open Group's public repository
(<https://community.opengroup.org/osdu/data/data-definitions>). A release is one of its version tags (`v` and a dotted
version); the newest is the default everywhere. A release's `Generated/SchemaStatus.json` lists every kind it publishes,
each with its status (published, in development or obsolete). Only record schemas are offered: the abstract building
blocks, the manifest and the content schemas declare no `data`, so no template can be laid out from them. A kind's
schema is the file that declares it, bundled with every shared schema it refers to.

The control plane keeps a local copy of the repository on disk. The first time a release is read, its whole `Generated`
folder is downloaded as one archive through the repository's GitLab API and unpacked, and every file of it is read from
disk from then on, across restarts. The release list is kept beside it and read again when it is older than
`RefreshMinutes`, and whenever someone presses **Sync with the repository** (any signed-in user). The newest release is
downloaded when the control plane starts. The schemas are public, so no flow, credential or node is involved.

A release is on disk only while every file its download unpacked is there: the download lists them, with their lengths,
in `release-files.json` beside the release's `Generated` folder. The local copy lives under the temp folder by default,
where a cleanup can delete the files and leave the folders standing. A release found short of a file is downloaded again
and put back whole, by the next read that needs it or by **Sync with the repository**, which looks at the disk each time;
the files still there are left as they are. A copy unpacked before the list was kept is downloaded once more to write it.

On the GUI's Templates page, the **Browse OSDU** tab:

1. **Picks a release and searches the record kinds it publishes.**
2. **Looks at a schema**: a kind laid out as a template, every variable with its type, requiredness, relationships, unit
   context and OSDU's description, read from the release and bundled. The kind links to its file in the repository.
3. **Compares versions** of a kind ([below](#comparing-versions)).
4. **Downloads it** (**Download**, on its row or in its sheet, any signed-in user) as the file a template folder keeps:
   the kind's schema bundled with every shared schema it refers to, naming its kind, under the kind's file name
   (`GET /api/v1/delivery/templates/osdu/file?release=&kind=`, answered as a file to save). Nothing is kept: the
   template is the repository's once the file is committed to a template folder and the repository syncs.

`sqlflow template capture --kind <kind> [--release <tag>] --out <folder>/<file>` writes the same file into a template
folder of a working copy ([template](../cli/template.md)).

### A schema file

A schema the data definitions do not publish (a kind of your own, or a version not released yet) is written into a
template folder with `sqlflow template import <schema.json> --kind <kind> --out <file>`:

- A bundled file, every `$ref` pointing into its own `definitions` (or `$defs`), is written as it is. That is the form
  `capture --out` writes, and the form a template folder keeps.
- A file as the data definitions publish it refers to the shared schemas beside it (`../abstract/...`). Those are read
  from a release (`--release`, the newest by default) and bundled in exactly as a capture bundles them.
- `sqlflow template import --from-dir <dir> --kind <kind> --out <file>` bundles the kind from a local checkout of the
  data definitions (its `Generated` folder).

A schema that names no kind in `x-osdu-schema-source` is given the kind it is imported as, so the file says which kind it
holds. Whatever the source, a schema is written only when it describes the kind named, refers to nothing outside itself
once bundled, and declares the `data` property every OSDU record carries:

| Problem | Message |
| --- | --- |
| Another kind | `<where>: the schema describes '<source>', not '<kind>'.` |
| A reference outside the file | `<where>: the schema refers to '<ref>' outside itself. A template file holds a bundled schema, where every reference is resolved into its definitions.` |
| A missing definition | `<where>: the schema refers to definition '<name>', which it does not contain.` |
| No `data` | `<where>: the schema declares no 'data' property, so it does not describe an OSDU record.` |
| Not JSON | `<where>: the schema is not valid JSON (...).` |
| A kind that is not a record kind | `Kind '<kind>' must be 'authority:source:entityType:major.minor.patch'.` |

## Templates of one's own

A template of one's own is a file in a template folder like any other: a kind under an authority of your own
(`acme:wks:master-data--Facility:1.0.0`), or an OSDU kind's schema changed for a project. Write the schema, referring to
OSDU's shared schemas as the data definitions do if it builds on them, and bundle it into the folder:

```bash
sqlflow template import drafts/Facility.1.0.0.json --kind acme:wks:master-data--Facility:1.0.0 \
  --out welldb/templates/acme_wks_master-data--Facility_1.0.0.json
```

The file `--out` writes is bundled, names its kind, and holds the version a mapping fills. A mapping that names the kind
alone follows each edit of the file from the next sync and run, and each edit is a version of its own, which the ledger
records with every record it renders. Check it from the working copy with `sqlflow template folders --for <mapping>` and
`sqlflow check <flow>`, before a sync ever reads it.

## A template as a file

A template file is the bundled schema, indented, with its keys in the order the schema was written, named after the
kind, each colon an underscore (`osdu_wks_work-product-component--WellLog_1.4.0.json`). It is written:

- by **Download** on the Templates page's Browse OSDU tab, from a release of the data definitions;
- by **Download** in a repository template's sheet on the Templates page, which gives the schema the repository's file
  holds, written as a template file under the file's own name, to keep in another repository's template folder
  (`GET /api/v1/delivery/templates/schema?repoId=&path=`, answered as a file to save, so a browser or `curl -OJ` saves it);
- by `sqlflow template capture --out` and `sqlflow template import --out` ([template](../cli/template.md)).

Writing the same schema twice writes the same version, because a version is the hash of the canonical form, which
indentation and key order do not change.

## Comparing versions

Before moving a mapping to a newer version of its kind, compare the two. On the Browse OSDU tab, **Compare versions**
picks two versions of one kind, each from any release of the data definitions
(`GET /api/v1/delivery/templates/osdu/compare?fromRelease=&fromKind=&toRelease=&toKind=`). The verdict comes first: the
same file, files that differ only in the version identifiers each carries (`x-osdu-schema-source` and `$id`), whether
the two are the same template version, and how many variables changed of each impact:

| Impact | Means | Changes of this impact |
| --- | --- | --- |
| Breaking | A mapping written for the older version can stop rendering, or render a record the newer version does not accept. | A variable removed; its type or shape, format, pattern, free-key type, unit context or writer changed; a list of objects that became nested inside another array; a property that became required; an entity type it no longer points to; a new required property inside an object the older version already has. |
| Additive | Something a mapping may now use. | A new optional variable (or a new required one inside a new object); a property no longer required; a new entity type it points to. |
| Wording | Only how it reads. | A title or a description. |

Every variable that differs is listed with each field that changed and its value on both sides. The two schema files
are shown as the release publishes them, and so is every shared schema they refer to that differs, paired by name
across versions (`AbstractFacility` 1.0.0 against 1.1.0): a kind's own file can be the same in two releases while a
schema it refers to changed, and that is where its template's changes come from. A release's statuses are shown but
judge nothing.

Moving a mapping to the newer version: download the newer file into the template folder its mapping reads, fix what the
comparison calls breaking, and check it ([check](../cli/check.md)). A mapping that names its version is moved by naming
the new one (give it a new mapping version when both are to run side by side, each pinning its own); one that names the
kind alone moves with the file. Run the flow's `plan` operation to see which records would be delivered again before
delivering ([running an OSDU flow](../cli/run.md)).

## Upgrading an estate that saved templates in the database

Templates used to be saved in the module's database as well, in a library (`[osdu].[Template]`) the Templates page, the
explorer and the mapping builder read, and the sync kept a copy of each template folder file's schema on its row. Module
schema 1.54.0 drops both: the migration `TemplatesReadFromRepositoryFiles` deletes the library's table and the copies,
and every reader reads the files. Before upgrading, make sure every version the estate's documents pin is a file in a
template folder of a synced repository, since a version saved only in the library is gone after the migration:

- a version a release of the OSDU data definitions publishes is downloaded on Browse OSDU, or written with
  `sqlflow template capture --kind <kind> --release <tag> --out <folder>/<file>.json`;
- a version of one's own is read from the control plane before it is upgraded, with
  `GET /api/v1/delivery/templates/schema?kind=<kind>&version=<version>`, and written into a folder with
  `sqlflow template import <file> --kind <kind> --out <folder>/<file>.json`, which names its kind in the file.

A pin keeps its version when the file holds the same schema. `sqlflow template folders --for <mapping>` says what a
mapping reads before the repository is synced.

## Where to work with templates

| Task | GUI | CLI | API (`/api/v1/delivery`) |
| --- | --- | --- | --- |
| See what a working copy's template folders hold, and what a document settles to | | `sqlflow template folders [<dir>] [--for <document>]` | |
| See what a synced document settles to | A mapping's properties | | `GET /templates/resolve?repoId=&path=&kind=[&version=]` |
| List every synced repository's template files | Templates, Repositories | | `GET /templates` |
| Lay one file's template out as variables | Repositories tab, a row | | `GET /templates/detail?repoId=&path=[&scope=]` |
| Copy a repository's template file | Repositories tab, a row: Download | | `GET /templates/schema?repoId=&path=` (a file to save) |
| List releases, list a release's record kinds | Browse OSDU | | `GET /templates/osdu/releases`, `GET /templates/osdu/schemas?release=` |
| Read a kind from a release | Browse OSDU, View | | `GET /templates/osdu/schema?release=&kind=` |
| Download a kind as a template file | Browse OSDU, Download | `sqlflow template capture --kind <kind> --out <file>` | `GET /templates/osdu/file?release=&kind=` (a file to save) |
| Write a schema of one's own as a template file | | `sqlflow template import <schema.json> --kind <kind> --out <file>` | |
| Lay out a schema without keeping it | Browse OSDU, View | | `POST /templates/preview` |
| Compare two versions of a kind | Browse OSDU, Compare versions | | `GET /templates/osdu/compare` |
| Read the release list again and download a release | Sync with the repository | | `POST /templates/osdu/sync` |
| Add or remove a template | Commit its file to, or delete it from, a template folder, and sync the repository | | |

Reading is open to any signed-in user, and syncing the data definitions is an operate action
([authentication and identity](authentication-and-identity.md)). The CLI's `template` subcommands read and write files
and need no database. The mapping builder starts a mapping from a template file a repository holds
([writing a mapping](../guides/writing-a-mapping.md)).

## Configuration: Osdu:SchemaRepository

The control plane's local copy of the data definitions is configured in the section `Osdu:SchemaRepository`. Point both
URLs at a mirror when community.opengroup.org is out of reach.

| Key | Default | Meaning |
| --- | --- | --- |
| `ApiUrl` | `https://community.opengroup.org/api/v4/projects/osdu%2Fdata%2Fdata-definitions/` | The GitLab API URL of the data definitions project. An absolute http or https URL. |
| `WebUrl` | `https://community.opengroup.org/osdu/data/data-definitions/` | The project's web page, which the Templates page links schema files on. |
| `CacheDirectory` | `sqlflow/osdu-data-definitions` under the temp folder | Where the local copy lives. |
| `RefreshMinutes` | `1440` (a day) | How old the release list may be before it is read again; 1 to 10080. |
| `DownloadTimeoutMinutes` | `15` | How long downloading one release may take; 1 to 240. |
| `WarmOnStart` | `true` | Read the release list, and download the newest release when it is not on disk, when the control plane starts. |

The section is validated when the control plane starts, and a value outside its range stops it with the setting named
(`Osdu:SchemaRepository:RefreshMinutes must be between 1 and 10080 (a week).`). The data definitions answering with an
error make the Templates page say so (a 502, `OSDU data definitions unavailable`); a release or kind they do not hold is
a 404 (`The OSDU data definitions have no release '<tag>'; the latest is <tag>.`).

## Related

- [Mapping](../flow/mapping.md): the `template` block and what a mapping fills.
- [`sqlflow template`](../cli/template.md): folders, capture and import.
- [Preflight](preflight.md): checking a mapping against its template.
- [Writing a mapping](../guides/writing-a-mapping.md): from a template to a mapping.
- [`osdu.RepositoryTemplate`](../tables/repository-template.md): what the sync records of each template folder file.
