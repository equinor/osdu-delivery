---
id: delivery-guide-writing-a-mapping
title: "Writing a mapping: from a template in a template folder to a mapping that checks clean"
type: guide
summary: "Put the template in a template folder, scaffold the mapping in the builder or the MCP server, fill it in, then check, test values and preview until clean."
keywords:
  - write a mapping
  - new mapping
  - scaffold mapping
  - mapping builder
  - delivery_scaffold_mapping
  - sqlflow check
  - sqlflow values
  - sqlflow preview
  - sqlflow validate mapping
  - template capture
  - template folder
  - preflight failed
  - held record reasons
  - mapping example
related:
  - delivery-flow-mapping
  - delivery-flow-mapping-values
  - delivery-flow-mapping-lookups
  - delivery-flow-mapping-modifiers
  - delivery-flow-mapping-assertions
  - delivery-cli-check
  - delivery-cli-values
  - delivery-cli-preview
  - delivery-concept-templates
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryVerbs.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryValueCheckVerbs.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryPreviewVerbs.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryCliModule.cs
  - osdu/src/SqlFlow.Delivery/Templates/MappingBuilder.cs
  - osdu/src/SqlFlow.Delivery/Templates/TemplateFolders.cs
  - osdu/src/SqlFlow.Delivery/Documents/MappingCatalog.cs
  - osdu/src/SqlFlow.Delivery/Documents/MappingMapper.cs
  - osdu/src/SqlFlow.Delivery/Engine/RenderResolver.cs
  - osdu/src/SqlFlow.Delivery/Engine/Planning/Planner.cs
  - osdu/src/SqlFlow.Delivery/Engine/Checks/ValueChecker.cs
  - osdu/src/SqlFlow.Delivery/Validation/Preflight.cs
  - osdu/src/SqlFlow.Delivery/Validation/MappingCoverage.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryTemplateEndpoints.cs
  - osdu/gui/src/features/delivery/MappingBuilderPage.tsx
  - osdu/gui/src/features/delivery/MappingEntryEditor.tsx
  - osdu/hosts/osdu-delivery-mcp/src/tools/documents.rs
---

# Writing a mapping: from a template in a template folder to a mapping that checks clean

A mapping says, property by property, how a row of an ingestion table becomes an OSDU record of one kind. This guide
writes one for well logs, `WellLog@1.1.0`, filling `osdu:wks:work-product-component--WellLog:1.4.0` from the well
database's silver tables, and then runs the loop that makes it right: validate the document, check it against its
template and the partition's cache, test what it makes of every row, preview one record, fix, repeat. Nothing in the
loop sends anything to OSDU.

The language itself is on [the mapping document](../flow/mapping.md), [mapping values](../flow/mapping-values.md),
[mapping lookups](../flow/mapping-lookups.md), [modifiers](../flow/mapping-modifiers.md) and
[expressions](../flow/mapping-expressions.md). This page is the path through them.

## What you need first

- **The rows.** A mapping reads keyed ingestion tables that SQLFlow's own flows load: here `OsduData.silver.WellLog`
  (key `log_id`) and its curves, `OsduData.silver.WellLogCurve` (keys `log_id`, `curve_id`), landed by a pre flow and
  loaded by an `ing` flow ([ingestion flow](../../../../sqlflow/docs/reference/flow/ing.md)). The mapping names their
  columns as they are: `log_name`, `wellbore_name`, `sampling_domain`, `curve_mnemonic`, `curve_unit`.
- **The cache the mapping reads.** Reference data and lookup tables are read from the cache of the partition the flow
  delivers to, filled by cache flows: [OSDU reference data](reference-data-cache.md) (`UnitOfMeasure`,
  `LogCurveType`...) and [your own lookup tables](lookup-table-cache.md) (`UnitAlias`, `CurveDictionary`, the
  `SamplingDomain` dictionary).
- **The command line with the module's database.** `check`, `values` and `preview` read the template from the template
  folders of your working copy, and the cache from the module's database: `--db <reference>`, else
  `SQLFLOW_CATALOG_DB`, unless the module has a connection of its own in `SQLFLOW_OSDU_DB`. `values` and `preview` also open the flow's ingestion tables on your machine through
  the flow's own connection reference, and ask the platform whatever the mapping's searches ask.

## 1. Put the template in a template folder

A mapping fills a template that a template folder on its path holds: a folder named `templates` beside the mappings or
above them, committed with them ([template folders](../concepts/templates.md#template-folders)). Write the kind's schema
into one from the OSDU data definitions, or download it on the Browse OSDU tab of the GUI's Templates page
([sqlflow template](../cli/template.md)):

```bash
sqlflow template capture --kind osdu:wks:work-product-component--WellLog:1.4.0 --out welldb/templates/osdu_wks_work-product-component--WellLog_1.4.0.json
```

```text
wrote template osdu:wks:work-product-component--WellLog:1.4.0 version 26a3c3441882db4f to <repo>/welldb/templates/osdu_wks_work-product-component--WellLog_1.4.0.json
```

The 16 hexadecimal characters are the template version. The mapping may name it (`template.version`), which pins that
version exactly, or name the kind alone and fill the one version the nearest folder holding the kind holds. The file is
the template: commit it, and the repository's sync records it; nothing of it is kept in a database. `sqlflow template
folders --for welldb/mappings/WellLog@1.4.0.yaml` lists the templates the mapping will read.

To see what can be filled, open the kind on the Templates page: **View** on the Browse OSDU tab before the file is
committed, or the file's row on the Repositories tab once the repository has synced. It lists every variable
(`osdu.data.WellboreID`, `osdu.data.Curves[].CurveUnit`...) with its shape, whether the schema requires it, who writes
it (a mapping, OSDU Delivery, or OSDU), the entity types it points to and its unit context.

## 2. Start from a scaffold

Start from a scaffold rather than an empty file. There are two ways; the command line has no scaffold verb.

**The mapping builder.** In the GUI, open **Mappings** under the OSDU menu's Build section and press **New mapping**
(an existing mapping opens with **Open in builder**). Pick the partition in the title bar: the builder reads that
partition's cache to prefill and check. Then, under Setup:

1. Pick the **Repository** the mapping will live in. Its delivery flow supplies the parameter values the check renders
   with (shown under Check values, and never written into the mapping).
2. Pick the **Template**, one of the files the repository's template folders hold, and give the **Name**, **Mapping
   version** and **Source system**. The file will be `<Name>@<version>.yaml` in the `mappings` folder beside the
   template folder holding the template, which is then on its path. Press **Start mapping**. A repository whose folders
   hold no template yet says so: download the kind on Browse OSDU, commit it to a template folder, and sync the
   repository.
3. Under **Dataset**, add the key columns, choose how the OSDU id is made, and write the label.
4. Every template variable is listed. Open one to choose its input (Dataset column, Repeat child rows, Cache, Lookup,
   Platform search, Static value, Expression, First value of, List of values or List of objects), with its modifiers,
   condition and required flag. A variable pointing to a type the cache holds offers **Use cache**. Lookups have a card
   of their own.
5. The right side shows the check (`loads and passes`, or the errors and warnings, each opening the variable it is
   about) and the YAML, checked again as you edit. **Copy YAML**, or, once the check finds no error, **Propose to
   repository**: for a repository tracked from git, the control plane pushes a branch and opens a pull request with
   the file. Nothing reaches the catalog until it is merged and synced.

**The MCP server.** An assistant connected to the OSDU Delivery MCP server calls `delivery_scaffold_mapping` with the
synced repository's id and the path of its template file (as `delivery_templates` lists them), the mapping's name,
version and source system, and optionally a partition. It returns the YAML and the issues still to resolve, and writes
nothing. `delivery_check_mapping` then checks each edit against the template until `valid` is true: the one the
template folders on the mapping's path hold when it is given the synced repository's `repoId`, else the version it
names among those the synced repositories hold ([the MCP server](mcp.md)).

Either way, a fresh scaffold holds the header filled from what you gave, the `dataPartition` parameter, the four access
and legal lists to fill, and a `$cache` entry for every relationship property outside a repeated array whose entity
type the partition's cache holds, finding the record by the type's first cached field with the column left for you. It
is not valid yet: the issues name what is missing (the key columns, the list values, each column a `$findBy` compares).

## 3. Fill it in

Here is the mapping once filled, `mappings/WellLog@1.1.0.yaml` beside the flows. It is the version of the well log
mapping for wellbores another system delivered, whose ids are not made from a key the well log rows hold, so it finds
each wellbore on the platform by name, where the well database's own `WellLog@1.0.0` refers to the wellbores it
delivers with `ref` ([one source, several kinds](multi-kind-source.md#3-point-the-well-log-at-its-wellbore)).

```yaml
documentType: mapping
name: WellLog
version: 1.1.0
template:
  kind: osdu:wks:work-product-component--WellLog:1.4.0
  version: 26a3c3441882db4f
description: Well logs from the well database, one record per log, with its curves.

dataset:
  system: welldb
  key: [log_id]
  label: "{wellbore_name} / {log_name} ({log_id})"
  identity: [log_id, wellbore_name]

parameters:
  dataPartition: { required: true }
  aclOwner: { required: true }
  aclViewer: { required: true }
  legalTag: { required: true }

searches:
  Wellbore:
    kind: "osdu:wks:master-data--Wellbore:*"
    schema:
      kind: osdu:wks:master-data--Wellbore:1.3.0
      version: 58d6bdbd9d066a06

record:
  acl:
    owners: ["{$param.aclOwner}"]
    viewers: ["{$param.aclViewer}"]
  legal:
    legaltags: ["{$param.legalTag}"]
    otherRelevantDataCountries: [US]
  data:
    Name:
      $from: log_name
      $modifiers: [trim]
    WellboreID:
      $search: Wellbore
      $findBy:
        - data.FacilityName = wellbore_name
        - data.NameAliases.AliasName = wellbore_name
    SamplingDomainTypeID:
      $from: sampling_domain
      $modifiers:
        - replace: $cache.SamplingDomain
        - ref
      $required: false
    Curves:
      $forEach: curves
      $item:
        Mnemonic:
          $from: curve_mnemonic
        CurveUnit:
          $from: curve_unit
          $modifiers:
            - replace: $cache.UnitAlias
            - ref
        LogCurveTypeID:
          $from: curve_mnemonic
          $modifiers:
            - id: "{$param.dataPartition}:reference-data--LogCurveType:{$cache.CurveDictionary.log_curve_type_id}:"
          $required: false
```

- **The header** pins the template, and `dataset` says what identifies a record: `system` and `key` make the delivery
  key, and so the OSDU id; `label` and `identity` are for people and never enter the record
  ([the mapping document](../flow/mapping.md)).
- **The access and legal lists** are literal, written with the flow's parameters. A flow leaving `aclOwner`,
  `aclViewer` or `legalTag` out of `render.parameters` takes `${env:OSDU_ACL_OWNER}`, `${env:OSDU_ACL_VIEWER}` and
  `${env:OSDU_LEGAL_TAG}`; a flow naming its partitions sets `dataPartition` to the partition a run delivers to.
- **The wellbore** is searched on the platform by name, then alias ([searches](../flow/mapping-lookups.md#searches-and-search)).
- **The references** are built from the source's codes: `replace: $cache.<Type>` translates through a table the cache
  holds, and `ref` or `id` builds the reference, checked against the partition's reference data in the cache
  ([modifiers](../flow/mapping-modifiers.md)).
- **The curves** are one item per child row of the `curves` dataset, which the delivery flow joins to the record.

### State the rules the values must meet

The template decides what OSDU accepts; what the values must be for a record to be right is the mapping's to say, beside
the property, with `$assert` ([mapping assertions](../flow/mapping-assertions.md)). Here every log must carry a depth
curve, and a log with fewer than two curves is sent with the failure recorded:

```yaml
    Curves:
      $forEach: curves
      $assert:
        - name: index-and-one-curve
          length: { atLeast: 2 }
          onFail: report
      $item:
        Mnemonic:
          $from: curve_mnemonic
          $assert:
            - name: a depth curve is among the curves
              in: [MD, DEPT]
              values: any
```

A log that fails the first assertion of `Mnemonic` is held before it is sent, its document kept until a release sends
it; `onFail: report` sends it and records the failure, and `onFail: omit` leaves a failing value out. `check` holds each
assertion against the template, and `values` and `preview` (steps 7 and 8) say which rows fail which assertion before a
run does.

## 4. Validate the document

```bash
sqlflow validate mappings/WellLog@1.1.0.yaml
```

```text
OK  'WellLog@1.1.0 -> osdu:wks:work-product-component--WellLog:1.4.0' is valid (mapping).
```

`validate` reads the document alone, offline: no template, cache, flow or table. It catches what the document says
wrong in itself: an unknown key, a word of the mapping language misspelled (named with the one it most likely meant),
a malformed `$findBy` line, modifier, id template or condition, a lookup or search nothing reads, a `{$param...}` the
mapping does not declare. Each error names the file and the place (`record.data.WellboreID: ...`); the messages are
listed on the [lookups](../flow/mapping-lookups.md#errors-when-the-mapping-is-read) and
[modifiers](../flow/mapping-modifiers.md#errors-when-the-mapping-is-read) pages. `sqlflow validate .` validates every
document of the repository in one pass ([validate](../cli/validate.md)).

## 5. Pin it from the delivery flow

The checks that need the template, the cache and the rows run through the delivery flow that renders with the mapping:
the well log flow, `welldb-welllog-03-delivery.yaml`, whole on [Delivery flow](../flow/delivery.md#a-fuller-example).
Pin the new version in its `render` block:

```yaml
# flows/welldb-welllog-03-delivery.yaml: its render block, now pinning the new version
render:
  mapping: WellLog@1.1.0
```

`render.mapping` names the file `mappings/WellLog@1.1.0.yaml` (or `mappings/WellLog/1.1.0.yaml`), found in the nearest
`mappings` folder walking up from the flow. `sqlflow validate` on the flow finds and reads the mapping, and refuses the
flow when it is missing or does not load; `check` holds it against its template and the cache.

## 6. Check it: sqlflow check

```bash
sqlflow check welldb-welllog-03-delivery.yaml
```

`check` loads the flow and the mapping it pins, the template its template folders hold, the version of the partition's cache the mapping
reads, and runs the preflight: every property against the template, every cached type and field against the cache,
every search property against its schema, every parameter against what the flow supplies
([the preflight](../concepts/preflight.md), [sqlflow check](../cli/check.md)). A clean mapping answers:

```text
OK  welldb-welllog-03-delivery (<flow id>)
    mapping     WellLog@1.1.0
    template    osdu:wks:work-product-component--WellLog:1.4.0 version 26a3c3441882db4f (welldb/templates/osdu_wks_work-product-component--WellLog_1.4.0.json)
    cache       partition dev version <cache version> (5 type(s))
    searches    exact only (indexer featureFlag.keywordLower.enabled unknown)
    context     <render context hash>
    mappings    <folder>/mappings
    source      ${env:OSDU_DATA_DB} OsduData.silver.WellLog (+1 dataset(s))
    payloads    none (the flow streams no payload files)
    tables      not read (run with --connect to open the ingestion tables)
```

A mapping that fails the preflight ends with exit code 1 and every error at once:

```text
ERROR  <folder>/welldb-welllog-03-delivery.yaml: preflight failed with 1 error(s):
  - <folder>/mappings/WellLog@1.1.0.yaml: record.data.SamplingDomainTypeID: replace reads $cache.SamplingDomain, which cache version '<cache version>' does not hold. Cached: CurveDictionary, LogCurveType, UnitAlias, UnitOfMeasure.
```

`check` reads no row: it does not open the ingestion tables (`--connect` only reports what a run would read there), so
a column the tables do not have is found by `values`, `preview` and a run. Warnings (a date property filled without the
`date` modifier, values a replace gives that find no record) do not stop a check; `values` lists them as `issue` lines,
the builder shows them, and a run logs them.

## 7. Test every row: sqlflow values

```bash
sqlflow values welldb-welllog-03-delivery.yaml
```

`values` renders the rows of the flow's scope exactly as a delivery renders them (the first 10,000 by default,
`--max-rows 0` for all), holds every value written to its template's rules, and adds up, variable by variable, which
rows hold their record, write a value the template does not accept, or leave a variable out, with the reasons, the
values behind them and example records ([sqlflow values](../cli/values.md)). It writes nothing anywhere. Abridged:

```text
!!  welldb-welllog-03-delivery: WellLog@1.1.0 on osdu:wks:work-product-component--WellLog:1.4.0, cache <cache version>
    rows        10,000 checked of 10,000 read (the scope holds about 48,210)
    outcome     9,958 clean, 42 held, 0 with an invalid value, 0 leaving a variable out
    osdu.data.Curves[].CurveUnit: 42 held, 0 invalid, 0 empty, 9,958 valid, 0 not applicable
      held           42  the id {$param.dataPartition}:reference-data--UnitOfMeasure:{$value}: gives dev:reference-data--UnitOfMeasure:METRES:, and version <cache version> of the cache of partition 'dev' holds no such reference-data--UnitOfMeasure record in UnitOfMeasure, so the reference would point at nothing
               values  'METRES' x40, 'FEET.' x2
               e.g.    welldb:LOG-0123, item 4
```

Here two spellings of a unit are missing from the unit table, so their curves would reference a unit the partition
does not hold. `values` exits 1 while any row is held, writes an invalid value or fails an assertion that would hold
it; a variable an optional node leaves out, and an assertion that only reports or leaves a value out, are reported
without failing it. A row failing an assertion adds an `asserted` line, and an `asserted` finding under its property. `--target osdu.data.Curves[].CurveUnit` checks one variable, and `--rows failing.csv`
writes every failing row.

## 8. Look at one record: sqlflow preview

```bash
sqlflow preview welldb-welllog-03-delivery.yaml --key LOG-0123
```

`preview` renders one record as a delivery would and sends nothing: the scope's first record, or the one `--key` names.
It says what the next run would do with it and why, why it would be held, what the mapping's assertions found of it,
which `$coalesce` alternative gave each value, the records the document refers to and the requests the route would
make, and then the document itself
([sqlflow preview](../cli/preview.md)). Read the document against the template: the names, the units, the references.

## 9. Fix and repeat

| What you see | Where it comes from | What to change |
| --- | --- | --- |
| `Mapping 'WellLog@1.1.0' was not found under '...'` | `check` | The file name or folder: `mappings/<Name>@<version>.yaml`. |
| `... declares 'WellLog@1.1.1' but is filed as 'WellLog@1.1.0'` | `check` | Make the file name and the document's `name` and `version` agree. |
| `... cannot render: no template folder on the path of ... holds template ...` | `check` | Put the template's file in a folder on the mapping's path (step 1), or name the version a folder there holds. |
| `..., the nearest template folder on the path of ... holding <kind>, holds <n> versions of it: ...` | `check` | Keep one version of the kind in that folder, or name the version in the mapping's `template.version`. |
| `mapping parameter '<name>' is required and the flow supplies no value.` | `check` | Supply it under the flow's `render.parameters`. (`aclOwner`, `aclViewer` and `legalTag` default to `${env:OSDU_ACL_OWNER}`, `${env:OSDU_ACL_VIEWER}` and `${env:OSDU_LEGAL_TAG}`, which have to resolve.) |
| `... reads <Type> from the cache, and cache version '...' does not hold it` | `check` | Add the type to a cache flow of the partition and refresh it, or fix the name. |
| `template ... requires osdu.data.<property>, which the mapping does not fill.` | `check` | Fill the property, without `$required: false`. |
| `... reads <column>, which the record table does not hold. Columns: ...` | `values`, `preview`, a run | The column name, or the ingestion flow. |
| A held reason naming a value the cache does not know | `values`, `preview` | A `replace` for the spelling, a row in the lookup table, or the reference data in the cache. |
| A held reason for an empty column | `values`, `preview` | The source, or `$required: false` where the template allows the property to be absent. |
| A value the template does not accept | `values` | A modifier (`date`, `number`, `split`) or the source. |
| `<property>.$assert[<n>]: '<path>' is a number in ...; matches compares text.` | `check` | A condition that suits the property's type ([mapping assertions](../flow/mapping-assertions.md#the-preflight)). |
| An `asserted` finding, `fails "<assertion>" (record); the record is held before it is sent` | `values`, `preview` | The source rows, or the rule, if the rule is wrong; `onFail: report` to send such records and record the failure. |

Each change is a new round: `validate`, `check`, `values`, `preview`. A mapping is ready when `check` answers `OK`,
`values` answers `OK` over the whole scope (`--max-rows 0`), and the previewed documents read right.

## 10. Deliver with it

Commit the mapping beside the flow, or propose it from the builder, and let the repository sync. A mapping is
versioned like a flow: a changed mapping is a new version the flow pins. The flow's next ordinary run then reads its
whole scope once and renders every record again, and only a record whose rendered document changed is sent. A run with
`--operation plan` reports what a delivery would send and changes nothing, so it shows what a mapping change will do
before it does it ([running an OSDU flow](../cli/run.md), [change detection](../concepts/change-detection.md)).
