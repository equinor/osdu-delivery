---
id: delivery-concept-templates
title: "Templates: OSDU schemas a repository's template folders hold, settled for each document by the folders on its path"
type: concept
summary: "What a template is, the template folders a repository keeps them in, how a document's template is settled, the library, and comparing versions."
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
  - save template
  - download template
  - template file
  - library
related:
  - delivery-flow-mapping
  - delivery-cli-template
  - delivery-concept-preflight
  - delivery-flow-mapping-lookups
  - delivery-guide-writing-a-mapping
  - delivery-concept-lineage
  - delivery-concept-architecture
  - delivery-table-template
  - delivery-table-repository-template
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Templates/OsduTemplate.cs
  - osdu/src/SqlFlow.Delivery/Templates/TemplateChain.cs
  - osdu/src/SqlFlow.Delivery/Templates/TemplateFolders.cs
  - osdu/src/SqlFlow.Delivery/Templates/TemplateSource.cs
  - osdu/src/SqlFlow.Delivery/Templates/TemplateStore.cs
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
  - osdu/src/SqlFlow.Delivery/Engine/Assertions/AssertionTemplates.cs
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionBlueprint.cs
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryVerbs.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryTemplateEndpoints.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Configuration/DeliveryModuleOptions.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/DeliveryControlPlaneModule.cs
  - osdu/gui/src/features/delivery/TemplatesPage.tsx
  - osdu/gui/src/features/delivery/TemplatesBrowseTab.tsx
  - osdu/gui/src/features/delivery/MappingProposeSheet.tsx
  - osdu/gui/src/features/delivery/templateFormat.ts
  - osdu/samples/welldb/templates/osdu_wks_work-product-component--WellLog_1.4.0.json
  - osdu/samples/welldb/templates/osdu_wks_master-data--Wellbore_1.3.0.json
---

# Templates: OSDU schemas a repository's template folders hold, settled for each document by the folders on its path

A template is an OSDU schema laid out as the record of one kind, with a variable for every property the schema declares.
A [mapping](../flow/mapping.md) fills one template: it names the template's kind under `template`, and says for each
variable it fills where the value comes from. Nobody edits a template; it is the schema, and a version of it never
changes.

A repository keeps the templates its documents fill in **template folders**, folders named `templates`, beside the
mappings and flows that read them or above them. A document reads the template folders on its path, and the one it
names settles to a version there ([Template folders](#template-folders)): a run, `sqlflow check`, lineage and the sync
all read the files. A template is put in a folder as a file: downloaded from the Templates page, written by `sqlflow
template capture --out`, or proposed with a mapping by the mapping builder. A template of one's own is a file like any
other ([Templates of one's own](#templates-of-ones-own)).

The **library** of saved templates (`[osdu].[Template]`) is for what reads templates where no repository is at hand: the
Templates page browses the OSDU data definitions, compares versions and saves them there, and the explorer, the mapping
builder and the MCP tools read it. It is not linked to the repositories: a sync saves nothing in it, nothing a repository
holds keeps a version from being deleted, and a version the library holds is never a document's template.

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
  folder, file or repository, is the same version, and the library saves it once.
- **The version identifies the schema exactly**, whatever release, file or folder it came from, so a record is always
  checked against the schema it was rendered for. Indentation, key order and line endings do not move it.
- **A render records it.** The template version enters every record's render context, and the ledger keeps the context
  of every render. Moving a mapping to another version, like changing the mapping itself, makes the next run read the
  whole scope again; each record's own document hash decides whether it is sent ([change detection](change-detection.md)).

## Template folders

A template folder is a folder named exactly `templates` (lower case, on every system: a folder named `Templates`, or
`template` holding `.json` files, is not read, and the sync warns of it), anywhere in a repository; a repository holds as many as its projects need. Its template
files are the `.json` files in it and in the folders under it, except:

- a folder named `templates` inside it, which is a template folder of its own;
- the folders a repository sync skips: a name starting with a dot, `bin`, `obj`, `node_modules` and `runs`;
- a link (a symbolic link or a junction), which is never followed, so a folder holds only what is in it; the sync warns
  of a template folder reached through one.

Other files (a `README.md`) are left alone. A folder is read for its first 1,000 `.json` files in path order, and a file
of more than 8 MB is not read.

A template file is a bundled record schema: every `$ref` resolved into its own `definitions`, a `data` property
declared, and its kind named in its own `x-osdu-schema-source`, as every schema of the OSDU data definitions names it.
The file's name is never read for the kind; the convention is the kind with each colon an underscore
(`osdu_wks_work-product-component--WellLog_1.4.0.json`), which is what a download, `capture --out` and the builder name
it. A file that is not one is reported by the sync with the reason, and holds no template:

| Problem | What is said |
| --- | --- |
| Not JSON, or not an object | `the schema is not valid JSON (...)`, `the schema is not a JSON object.` |
| No kind | `the schema names no kind. A template file declares the kind it holds in x-osdu-schema-source (...)` |
| Not a record kind | `x-osdu-schema-source '<value>' is not a record kind; it must be 'authority:source:entityType:major.minor.patch'.` |
| Not bundled | `the schema refers to '<ref>' outside itself. A template is saved from a bundled schema, where every reference is resolved into its definitions.` |
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
folder above them. A version saved in the library, or held by a folder off the document's path, is never its template.

A template that does not settle says where the document looked, what its path holds of the kind, which files on its path
could not be read (one of them may be the one meant), where else the repository holds it, and how to put it there:

```text
no template folder on the path of welldb/mappings/WellLog@1.4.0.yaml holds template
osdu:wks:work-product-component--WellLog:1.4.0 version 26a3c3441882db4f. On its path welldb/templates/
osdu_wks_work-product-component--WellLog_1.4.0.json holds version 0f21b6a0c3d89e14. It reads welldb/templates.
The repository holds version 26a3c3441882db4f of osdu:wks:work-product-component--WellLog:1.4.0 at
archive/templates/WellLog.json, which is not on its path. Put the template's file in one of them: the Templates page
downloads a saved version, and 'sqlflow template show --kind osdu:wks:work-product-component--WellLog:1.4.0 --version
26a3c3441882db4f --out welldb/templates/osdu_wks_work-product-component--WellLog_1.4.0.json' writes it from the saved
templates.
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
  and a folder that no longer holds it leaves the mapping unable to load until it does, whatever the library holds
  ([mapping](../flow/mapping.md)).
- **A search** names the schema of the kind it searches the same way, `schema.kind` and an optional `schema.version`,
  settled from the folders on the mapping's path ([mapping lookups](../flow/mapping-lookups.md)).
- **An assertion test** reads the template of its `kind` from the folders on its flow's path: the version its `template`
  names, the version the mapping it names fills (`template: WellLog@1.5.0`), or, naming neither, the one version its
  nearest folder holds. The records its known values reach are described by the newest version of their type the flow's
  folders give ([assertion](../flow/assertion.md#checked-against-the-template)).
- **A dimension** reads, for each kind its pattern matches among the templates its flow's folders hold, the one version
  of that kind they settle to, and describes the records it reaches by id by the newest version of their type
  ([dimension](../flow/dimension.md)).

The sync records, for every mapping, the version it settled to and the file it read (`TemplateVersion` and
`TemplatePath` of [`osdu.Mapping`](../tables/mapping.md)); a mapping whose template does not settle is invalid, keeps
the reference it is known by, and its message is the one above. A mapping that names a version keeps that pin while it
is invalid, so the library will not delete the version from under it.

## Where templates are read

| Who | Reads |
| --- | --- |
| A run: the render, its preflight, the validation gate; `sqlflow check`, `preview` and `values`; an assertion or dimension run | The files of the template folders on the document's path. A record rendered for an exact version no folder holds any longer is checked against that version where the library holds it, someone having saved it there (one content, wherever it is kept); otherwise it is `unverified`. |
| Lineage and the OSDU order it takes | The files, on the mapping's path, in the checkout being scanned ([lineage](lineage.md)). |
| The repository sync | Every template folder of the repository: each file is a row of [`osdu.RepositoryTemplate`](../tables/repository-template.md) with a copy of the template it holds, and every mapping is settled from them. |
| The control plane's pages about a repository's documents: an assertion flow's board, a dimension's blueprint and views, a source's interface order, the search terms, a mapping's properties, shape and coverage, the mapping builder opening a mapping, and `GET /api/v1/delivery/templates/resolve` | The files the repository's last sync recorded, settled by the same rule, each schema read from the copy that sync kept of the file. A row recorded before copies were kept says to sync the repository again until the next sync keeps its copy. |
| The explorer, the Templates page, the mapping builder's drafts, the MCP server's template tools | The library. |
| `sqlflow template folders` | The files, in a working copy, with no database ([template](../cli/template.md)). |

## The library: saved templates

The library is `[osdu].[Template]` ([table](../tables/template.md)): every version a person saved, from the Templates page
or by `sqlflow template capture` or `import`. A version's origin says where it came from: `OSDU data definitions <release>
(<commit>) Generated/<path>`, or `file <name>`. It serves what reads templates where no repository is at hand: the
explorer checks a record against a saved template, the mapping builder starts a draft from one, and the MCP server's
template tools lay one out.

It is not linked to the repositories. A repository sync saves nothing in it, and keeps the templates a repository's
folders hold as copies of its own, with each file's row ([`osdu.RepositoryTemplate`](../tables/repository-template.md)),
which only the pages about that repository's documents read. So the Templates page says nothing of which repositories hold
a version or which mappings fill it, and any saved version can be deleted (**Delete** on the Templates page,
`sqlflow template delete`, or `DELETE /api/v1/delivery/templates`, an author action): no repository document reads it. A
mapping drafted from a saved version is proposed with that version's file when the repository's folders do not hold it,
so the repository reads its own copy from then on.

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
   context and OSDU's description, read from the release and bundled; nothing is stored yet. The kind links to its file
   in the repository.
3. **Compares versions** of a kind ([below](#comparing-versions)).
4. **Saves it** (**Save template**, any signed-in user) in the library: exactly the schema that was shown. Its origin
   names the release, the first 12 characters of its commit and the file:
   `OSDU data definitions <release> (<commit>) Generated/<path of the kind's file>`. A saved version's **Download** gives
   the file a repository's template folder keeps.

`sqlflow template capture --kind <kind> [--release <tag>] --out <folder>/<file>` writes a kind's schema straight into a
template folder of a working copy, and saves it in the library when a module database is configured
([template](../cli/template.md)).

### A schema file

A schema the data definitions do not publish (a kind of your own, or a version not released yet) is imported as a
file, on the Templates page's **Import file** tab or with `sqlflow template import <schema.json> --kind <kind>`:

- A bundled file, every `$ref` pointing into its own `definitions` (or `$defs`), is saved as it is. That is the form
  `capture --out` writes, and the form a template folder keeps.
- A file as the data definitions publish it refers to the shared schemas beside it (`../abstract/...`). Those are read
  from a release (`--release`, the newest by default) and bundled in exactly as a capture bundles them; the origin then
  also names that release.
- `sqlflow template import --from-dir <dir> --kind <kind>` bundles the kind from a local checkout of the data definitions
  (its `Generated` folder).

A schema that names no kind in `x-osdu-schema-source` is given the kind it is imported as, so the template saved, and the
file `--out` writes, say which kind they hold. Whatever the source, a schema is saved only when it describes the kind
named, refers to nothing outside itself once bundled, and declares the `data` property every OSDU record carries:

| Problem | Message |
| --- | --- |
| Another kind | `<where>: the schema describes '<source>', not '<kind>'.` |
| A reference outside the file | `<where>: the schema refers to '<ref>' outside itself. A template is saved from a bundled schema, where every reference is resolved into its definitions.` |
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

The file `--out` writes is bundled, names its kind, and holds the version a mapping fills; a host with no module database
writes the file alone. A mapping that names the kind alone follows each edit of the file from the next sync and run, and
each edit is a version of its own, which the ledger records with every record it renders. Check it from the working copy
with `sqlflow template folders --for <mapping>` and `sqlflow check <flow>`, before a sync ever reads it.

## A template as a file

Any saved version can be written out as the file a template folder keeps:

- **Download** in a saved version's sheet on the Templates page (any signed-in user);
- `sqlflow template show --kind <kind> --version <v> --out <file.json>` ([template](../cli/template.md));
- `GET /api/v1/delivery/templates/schema?kind=&version=`, which answers it as a file to save
  (`Content-Disposition: attachment`), so a browser or `curl -OJ` saves it under its name;
- **Propose to repository** in the mapping builder, which writes the file the mapping fills into the repository's top
  template folder, beside the mapping, when no folder on the mapping's path holds it.

The file is the bundled schema, indented, with its keys in the order the schema was written. It is named after the kind,
each colon an underscore (`osdu_wks_work-product-component--WellLog_1.4.0.json`). Importing it saves the same version,
because a version is the hash of the canonical form, which indentation and key order do not change. A version saved
before a template named its kind in `x-osdu-schema-source` is written as it was saved, and `show --out` says that a
template folder will not read it: import that file again with `--out`, which writes it naming its kind, as a new version.

## Comparing versions

Before moving a mapping to a newer version of its kind, compare the two. On the Browse OSDU tab, **Compare versions**
picks two versions of one kind, each from any release of the data definitions
(`GET /api/v1/delivery/templates/osdu/compare?fromRelease=&fromKind=&toRelease=&toKind=`). The verdict comes first: the
same file, files that differ only in the version identifiers each carries (`x-osdu-schema-source` and `$id`), whether
the two save as the same template version, and how many variables changed of each impact:

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

Moving a mapping to the newer version: put the newer file in the template folder its mapping reads, fix what the
comparison calls breaking, and check it ([check](../cli/check.md)). A mapping that names its version is moved by naming
the new one (give it a new mapping version when both are to run side by side, each pinning its own); one that names the
kind alone moves with the file. Run the flow's `plan` operation to see which records would be delivered again before
delivering ([running an OSDU flow](../cli/run.md)).

## Upgrading an estate whose templates were saved only in the library

A mapping used to read its template from the library. A repository now has to hold the templates its documents read:
until its folders hold them, its mappings are invalid with the message above, and its assertion and dimension flows read
no template. Write each version a mapping pins into a template folder on its path, from the library the estate already
has, and commit it:

```bash
sqlflow template show --kind osdu:wks:work-product-component--WellLog:1.4.0 --version 26a3c3441882db4f \
  --out welldb/templates/osdu_wks_work-product-component--WellLog_1.4.0.json
```

`sqlflow template folders --for welldb/mappings/WellLog@1.4.0.yaml` says what a mapping now reads before the repository is
synced.

## Where to work with templates

| Task | GUI | CLI | API (`/api/v1/delivery`) |
| --- | --- | --- | --- |
| See what a working copy's template folders hold, and what a document settles to | | `sqlflow template folders [<dir>] [--for <document>]` | |
| See what a synced document settles to | A mapping's properties | | `GET /templates/resolve?repoId=&path=&kind=[&version=]` |
| List saved versions, with the folders holding and the mappings filling each | Templates, Saved | `sqlflow template list` | `GET /templates` |
| Lay one version out as variables | Saved tab, a row | `sqlflow template show --kind <kind> [--version <v>]` | `GET /templates/detail?kind=&version=` |
| Write a version into a template folder | Saved tab, a row: Download; the builder's Propose to repository | `sqlflow template show ... --out`, `capture ... --out`, `import ... --out` | `GET /templates/schema?kind=&version=` (a file to save) |
| List releases, list a release's record kinds | Browse OSDU | | `GET /templates/osdu/releases`, `GET /templates/osdu/schemas?release=` |
| Read a kind from a release | Browse OSDU | | `GET /templates/osdu/schema?release=&kind=` |
| Lay out a schema without saving it | Browse OSDU, Import file | | `POST /templates/preview` |
| Compare two versions of a kind | Browse OSDU, Compare versions | | `GET /templates/osdu/compare` |
| Read the release list again and download a release | Sync with the repository | | `POST /templates/osdu/sync` |
| Save a version in the library | Save template | `sqlflow template capture`, `sqlflow template import` | `POST /templates` |
| Delete a version from the library | Delete | `sqlflow template delete` | `DELETE /templates?kind=&version=` |

Reading is open to any signed-in user; syncing the data definitions is an operate action; saving and deleting change
the library, so they are author actions ([authentication and identity](authentication-and-identity.md)). The CLI's
`folders`, and `capture` and `import` with `--out`, need no database; its other forms need the module's database
(`--db <conn-ref>`, or the catalog variable). The mapping builder starts a mapping from a saved template
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
- [`sqlflow template`](../cli/template.md): folders, capture, import, list, show and delete.
- [Preflight](preflight.md): checking a mapping against its template.
- [Writing a mapping](../guides/writing-a-mapping.md): from a template to a mapping.
- [`osdu.RepositoryTemplate`](../tables/repository-template.md) and [`osdu.Template`](../tables/template.md): what the sync records and the library.
