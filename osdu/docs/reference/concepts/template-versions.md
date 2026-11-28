---
id: delivery-concept-template-versions
title: "Comparing template versions: what changed between two versions of a kind, and what moving a mapping takes"
type: concept
summary: "Compare two versions of an OSDU kind, each change weighed against what the synced mappings write, and take a report of what moving a mapping means."
keywords:
  - compare versions
  - compare templates
  - template version
  - schema version
  - breaking change
  - additive change
  - wording change
  - significant change
  - minor change
  - in use
  - deprecated
  - move a mapping
  - upgrade a mapping
  - newer version of a kind
  - migration report
  - what breaks
  - report
  - html report
  - copy as markdown
  - shared schema
  - browse osdu
related:
  - delivery-concept-templates
  - delivery-flow-mapping
  - delivery-cli-check
  - delivery-concept-preflight
  - delivery-cli-template
  - delivery-guide-writing-a-mapping
  - delivery-concept-api
  - delivery-concept-mapping-overview
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Templates/TemplateComparison.cs
  - osdu/src/SqlFlow.Delivery/Templates/TemplateChangeAdvice.cs
  - osdu/src/SqlFlow.Delivery/Templates/TemplateDeprecation.cs
  - osdu/src/SqlFlow.Delivery/Templates/TemplateMoveReport.cs
  - osdu/src/SqlFlow.Delivery/Templates/TemplateMoveReport.Html.cs
  - osdu/src/SqlFlow.Delivery/Validation/MappingMove.cs
  - osdu/src/SqlFlow.Delivery/Validation/MappingCoverage.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryTemplateEndpoints.Compare.cs
  - osdu/gui/src/features/delivery/TemplateCompareSheet.tsx
  - osdu/gui/src/features/delivery/TemplateCompareReport.tsx
---

# Comparing template versions: what changed between two versions of a kind, and what moving a mapping takes

Before moving a mapping to a newer version of its kind, compare the two. On the Templates page's **Browse OSDU** tab,
**Compare versions** picks two versions of one kind, each from any release of the OSDU data definitions
(`GET /api/v1/delivery/templates/osdu/compare?fromRelease=&fromKind=&toRelease=&toKind=`), and weighs every change
against what the synced mappings of the kind write: what a change means is found in the mapping documents, and a
change to something no mapping uses has less impact ([weighing a change](#weighing-a-change)). Its **Report** says
what moving a mapping from one version to the other takes ([the report](#the-report)).

## The verdict and the changes

The verdict comes first: the same file, files that differ only in the version identifiers each carries
(`x-osdu-schema-source` and `$id`), whether the two are the same template version, and how many variables changed of each
impact:

| Impact | Means | Changes of this impact |
| --- | --- | --- |
| Breaking | A mapping written for the older version can stop rendering, or render a record the newer version does not accept. | A variable removed; its type or shape, format, pattern, free-key type, unit context or writer changed; a list of objects that became nested inside another array; a property that became required; an entity type it no longer points to; a new required property inside an object the older version already has. |
| Additive | Something a mapping may now use. | A new optional variable (or a new required one inside a new object); a property no longer required; a new entity type it points to. |
| Wording | Only how it reads. | A title or a description, a deprecation among them ([deprecated properties](#deprecated-properties)). |

Every variable that differs is listed with each field that changed and its value on both sides. The two schema files
are shown as the release publishes them, and so is every shared schema they refer to that differs, paired by name
across versions (`AbstractFacility` 1.0.0 against 1.1.0): a kind's own file can be the same in two releases while a
schema it refers to changed, and that is where its template's changes come from. A release's statuses are shown but
judge nothing.

## Weighing a change

The impact says what a change could mean for any mapping; the synced mappings of the kind say what it means for this
estate. Every mapping a synced repository holds of the kind, at a version other than the newer one, is read from its
document, against the template it pins, which can be another version than the comparison's older side. What it writes
(every property its entries target, the properties of a list's items, and what a literal or an object written whole
holds) is what each change is weighed against:

| Weight | Means |
| --- | --- |
| Significant | It asks something of a mapping that writes what it changes: the mapping has to change for it ([synced mappings](#synced-mappings)), or the newer version deprecates what it writes. |
| Minor | It touches what a mapping writes, or an object it writes inside, and asks nothing of it: how it reads, a rule it no longer has to keep, more kinds it may point to. |
| Not in use | No synced mapping writes what it changes, so it has the least impact: it matters only to a mapping that starts to use it, or to one not synced here. |

A new optional variable is never in use, since no mapping can write it yet; a new required one inside an object a
mapping writes is in use, and significant when the mapping would leave it out. A mapping that already pins the newer
version has nothing to move and is not weighed, nor is one that could not be read. With none to weigh against, no
change is weighed and each counts in full.

The sheet says under the verdict what the changes were read against and how many are of each weight. It lists them by
weight, the significant first and each weight's changes by impact, with the changes no mapping uses folded away; a
filter narrows the list to one weight. With several mappings weighed, each change names the ones that use it. A change
whose description now deprecates its variable carries **Deprecated**, loud only where a mapping writes the variable.
`GET /templates/osdu/compare` carries each change's `weight` (`Significant`, `Minor` or `Unused`) and `usedBy`, both
null when no mapping is weighed, its `deprecates`, and the `weighedMappings` the changes were read against.

### Deprecated properties

The OSDU data definitions deprecate a property by starting its description with `DEPRECATED`, followed by what to use
instead: in WellLog 1.5.0, `LogRun` reads `DEPRECATED: Use the Well Log Acquisition schema LogRun[] array and the
LogRunID.` Only the description changes, so OSDU still accepts a record that writes the property (the data definitions
call such a change documentation-only), and the comparison counts it as wording. Read against a mapping that writes the
property it is significant: the mapping keeps working, and should move to what replaces the property before a later
version removes it.

## The report

**Report**, beside the version pickers, takes the comparison as a report on moving from the older version to the newer:

- **Copy as Markdown** puts it on the clipboard, to paste into a ticket, a pull request or a wiki;
- **Download** saves it as an HTML page to read, print or send (it needs nothing else: its styles are inline, it reads
  in a light and a dark theme, and it runs no script), as Markdown, or as JSON for a script or a tool, named after the
  entity and each side's version and release (`Wellbore-1.0.0-v0.29.0-to-1.3.0-v0.30.0-report.html`).

The control plane writes it (`GET /api/v1/delivery/templates/osdu/compare/report?fromRelease=&fromKind=&toRelease=&toKind=&format=html`,
answered as a file to save; `format=md` for Markdown, `format=json`, the default, for JSON), so it reads the same however
it is taken. In order, it holds:

1. **Which version is which**: each side's kind, release, status in the release, template version and published file.
2. **The verdict**, read against the mappings first: how many synced mappings there are and where each stands, and how
   many of the changes are significant for them, minor, or touch nothing they use; then the comparison's own counts.
3. **The synced mappings of the kind** and what the move means for each ([below](#synced-mappings)), what needs
   changes first.
4. **Every change, by weight**: the significant ones, the minor ones, then the changes no synced mapping uses, each
   with its impact in the schema, the mappings that use it, and what it asks of a mapping: remove what writes a removed
   variable, give a value of a changed type, write values in a new format or matching a new pattern, fill a variable
   that became required on every record, stop writing a variable OSDU Delivery or OSDU now writes, and so on. With no
   mapping to weigh against, the changes are listed by impact instead, each field that changed on a line of its own.
5. **The shared schemas** the kind refers to that differ, with their files on each side.
6. **How to move a mapping**: download the newer file into the template folder the mapping reads, fix what the report
   lists, plan to move off what is deprecated, check it with `sqlflow check`, and run the flow's `plan` operation.

The HTML page says the same, laid out to be read: the verdict and its figures first, each mapping as a card with its
findings in tables, and the changes no mapping uses folded away.

The JSON holds the same: `kind`, `from` and `to`, `sameFile`, `onlyIdentifiersDiffer`, `sameTemplate`, `counts` (the
impacts, `unchanged`, `weighedMappings`, and `significant`, `minor` and `unused`, null when no mapping is weighed),
`mappings` (each with `repository`, `mapping`, `path`, `pinnedKind`, `pinnedVersion`, `state`, `problem`, `writes`, the
`counts` of its findings by kind, and `findings`, each with `path`, `kind`, `line`, `reads`, `change`, `fields`,
`requirement`, `deprecation`, `alreadyDeprecated` and `note`), `changes` (each with `path`, `change`, `impact`, `role`,
`deprecates`, `weight`, `usedBy`, `summary`, `action` and its `fields`, every field with a `note`), `sharedSchemas` and
`generatedUtc`.

### Synced mappings

Each mapping is read against what its document writes, from the template it pins. What the move means for it is said
in four kinds of finding, each with the line of the document its node starts on and how the mapping fills the property
(`dataset.log_run`, `cache.UnitOfMeasure.id by Code = dataset.unit`, a literal), as the [mapping
overview](mapping-overview.md) describes the node:

- **To change**: a variable it writes, or one inside it, that the newer version removed (a removed object is one
  finding, not one per property), or whose type, format, pattern, free-key type, unit context, writer, nesting or
  relationships changed as a breaking change; and what the newer version requires that it would leave empty or may
  leave out. For the latter the mapping, pinned to the newer version, is held to the rule [the preflight
  gate](preflight.md#the-preflight-gate) holds required properties to, and what that finds and the version it pins did
  not is listed in the gate's words. So a variable that became required is to change only when the mapping may leave
  it out, and a fragment, whose record another mapping renders whole, is not held to it.
- **Deprecated**: a variable it writes that the newer version deprecates, with what its description says to use
  instead, and whether the version it pins already deprecated it.
- **Changed without breaking it**: a variable it writes, or an object it writes inside, whose title or description
  reads differently, which it no longer has to fill, or which may point to more kinds; a variable that became required
  and that it fills on every row is said so.
- **It may now fill**: a new optional variable inside an object it writes, the record's `data` included.

Each mapping is said to need changes, to move as it is (deprecations and changes that break nothing do not stop a
move), to already pin the newer version (or one holding the same variables), or not to be checked, with why: its
repository's last sync found the document invalid, or its template cannot be read. A line says how many properties it
writes and how many of them the newer version leaves the same. Nothing is rendered and no cache is read, so the checks
of the gate that read the cache are left to `sqlflow check` once the mapping pins the new version ([check](../cli/check.md)).
A report lists at most 200 mappings of a kind, by name.

## Moving a mapping

Download the newer file into the template folder its mapping reads, fix what the report lists under the mapping, plan
to move off what it writes that is deprecated, and check it ([check](../cli/check.md)). A mapping that names its version
is moved by naming the new one (give it a new mapping version when both are to run side by side, each pinning its own);
one that names the kind alone moves with the file. Run the flow's `plan` operation to see which records would be
delivered again before delivering ([running an OSDU flow](../cli/run.md)).

## Where to compare

| Task | GUI | API (`/api/v1/delivery`) |
| --- | --- | --- |
| Compare two versions of a kind, each change weighed against the synced mappings | Templates, Browse OSDU, Compare versions | `GET /templates/osdu/compare` |
| Take the report on moving from one to the other | Compare versions, Report | `GET /templates/osdu/compare/report` |

Both are reads, open to any signed-in user ([authentication and identity](authentication-and-identity.md)). Where a
template comes from and how a document settles its version is on [templates](templates.md).
