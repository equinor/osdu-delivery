---
id: delivery-concept-template-versions
title: "Comparing template versions: what changed between two versions of a kind, and what moving a mapping takes"
type: concept
summary: "Compare two versions of an OSDU kind from any releases, and take a report of what moving a mapping from one to the other breaks, synced mappings included."
keywords:
  - compare versions
  - compare templates
  - template version
  - schema version
  - breaking change
  - additive change
  - wording change
  - move a mapping
  - upgrade a mapping
  - newer version of a kind
  - migration report
  - what breaks
  - report
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
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Templates/TemplateComparison.cs
  - osdu/src/SqlFlow.Delivery/Templates/TemplateChangeAdvice.cs
  - osdu/src/SqlFlow.Delivery/Templates/TemplateMoveReport.cs
  - osdu/src/SqlFlow.Delivery/Validation/MappingMove.cs
  - osdu/src/SqlFlow.Delivery/Validation/MappingCoverage.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryTemplateEndpoints.Compare.cs
  - osdu/gui/src/features/delivery/TemplateCompareSheet.tsx
  - osdu/gui/src/features/delivery/TemplateCompareReport.tsx
---

# Comparing template versions: what changed between two versions of a kind, and what moving a mapping takes

Before moving a mapping to a newer version of its kind, compare the two. On the Templates page's **Browse OSDU** tab,
**Compare versions** picks two versions of one kind, each from any release of the OSDU data definitions
(`GET /api/v1/delivery/templates/osdu/compare?fromRelease=&fromKind=&toRelease=&toKind=`), and its **Report** says what
moving a mapping from one to the other takes, every synced mapping of the kind included ([the report](#the-report)).

## The verdict and the changes

The verdict comes first: the same file, files that differ only in the version identifiers each carries
(`x-osdu-schema-source` and `$id`), whether the two are the same template version, and how many variables changed of each
impact:

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

## The report

**Report**, beside the version pickers, takes the comparison as a report on moving from the older version to the newer:

- **Copy as Markdown** puts it on the clipboard, to paste into a ticket, a pull request or a wiki;
- **Download** saves it as Markdown, or as JSON for a script or a tool, named after the entity and each side's version
  and release (`Wellbore-1.0.0-v0.29.0-to-1.3.0-v0.30.0-report.md`).

The control plane writes it (`GET /api/v1/delivery/templates/osdu/compare/report?fromRelease=&fromKind=&toRelease=&toKind=&format=md`,
answered as a file to save; `format=json`, the default, for JSON), so it reads the same however it is taken. In order, it
holds:

1. **Which version is which**: each side's kind, release, status in the release, template version and published file.
2. **The verdict**, as the comparison gives it.
3. **The synced mappings of the kind** and what each has to change ([below](#synced-mappings)), what needs changes first.
4. **Every breaking, additive and wording change**, each with what it asks of a mapping: remove what writes a removed
   variable, give a value of a changed type, write values in a new format or matching a new pattern, fill a variable
   that became required on every record, stop writing a variable OSDU Delivery or OSDU now writes, and so on.
5. **The shared schemas** the kind refers to that differ, with their files on each side.
6. **How to move a mapping**: download the newer file into the template folder the mapping reads, fix what the report
   lists, check it with `sqlflow check`, and run the flow's `plan` operation.

The JSON holds the same: `kind`, `from` and `to`, `sameFile`, `onlyIdentifiersDiffer`, `sameTemplate`, `counts`,
`mappings` (each with `repository`, `mapping`, `path`, `pinnedKind`, `pinnedVersion`, `state`, `problem` and `findings`),
`changes` (each with `path`, `change`, `impact`, `role`, `summary`, `action` and its `fields`, every field with a `note`),
`sharedSchemas` and `generatedUtc`.

### Synced mappings

Every mapping a synced repository holds of the kind, at any version of it, is set against the newer version from the
template it pins, which can be another version than the comparison's older side. Two things make a finding:

- **What it writes that changed in a way that breaks it**: a variable it writes, or one inside it, that the newer
  version removed (a removed object is one finding, not one per property), or whose type, format, pattern, free-key
  type, unit context, writer, nesting or relationships changed as a breaking change.
- **What the newer version requires that it would leave empty or may leave out**: the mapping, pinned to the newer
  version, is held to the rule [the preflight gate](preflight.md#the-preflight-gate) holds required properties to, and
  what that finds and the version it pins did not is listed in the gate's words. So a variable that became required is a
  finding only when the mapping may leave it out, and a fragment, whose record another mapping renders whole, is not
  held to it.

Each mapping is said to need changes, to move as it is, to already pin the newer version (or one holding the same
variables), or not to be checked, with why: its repository's last sync found the document invalid, or its template
cannot be read. Nothing is rendered and no cache is read, so the checks of the gate that read the cache are left to
`sqlflow check` once the mapping pins the new version ([check](../cli/check.md)). A report lists at most 200 mappings of
a kind, by name.

## Moving a mapping

Download the newer file into the template folder its mapping reads, fix what the report lists under the mapping and what
the comparison calls breaking, and check it ([check](../cli/check.md)). A mapping that names its version is moved by
naming the new one (give it a new mapping version when both are to run side by side, each pinning its own); one that
names the kind alone moves with the file. Run the flow's `plan` operation to see which records would be delivered again
before delivering ([running an OSDU flow](../cli/run.md)).

## Where to compare

| Task | GUI | API (`/api/v1/delivery`) |
| --- | --- | --- |
| Compare two versions of a kind | Templates, Browse OSDU, Compare versions | `GET /templates/osdu/compare` |
| Take the report on moving from one to the other | Compare versions, Report | `GET /templates/osdu/compare/report` |

Both are reads, open to any signed-in user ([authentication and identity](authentication-and-identity.md)). Where a
template comes from and how a document settles its version is on [templates](templates.md).
