---
id: delivery-concept-preflight
title: "Preflight: the checks a mapping, a flow and every record pass before anything reaches OSDU"
type: concept
summary: "What is checked before a delivery sends anything: the mapping on load, the preflight gate, the source tables, the run's preflight and each record's verdict."
keywords:
  - preflight
  - preflight gate
  - mapping check
  - schema validation
  - validation verdict
  - verdict fields
  - rules version
  - "target.validation"
  - enforce
  - unverified
  - invalid record
  - verifyreferences
  - template not found
  - template folder
  - required property
  - why nothing was sent
related:
  - delivery-cli-check
  - delivery-cli-values
  - delivery-cli-preview
  - delivery-flow-mapping
  - delivery-flow-mapping-assertions
  - delivery-flow-delivery
  - delivery-concept-templates
  - delivery-concept-record-lifecycle
  - delivery-flow-mapping-parent
  - delivery-flow-mapping-child
  - cli-validate
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Documents/MappingMapper.cs
  - osdu/src/SqlFlow.Delivery/Documents/MappingMapper.Record.cs
  - osdu/src/SqlFlow.Delivery/Documents/DeliveryDocumentLoader.cs
  - osdu/src/SqlFlow.Delivery/Validation/Preflight.cs
  - osdu/src/SqlFlow.Delivery/Validation/MappingCoverage.cs
  - osdu/src/SqlFlow.Delivery/Validation/RecordValidator.cs
  - osdu/src/SqlFlow.Delivery/Validation/ValidationVerdict.cs
  - osdu/src/SqlFlow.Delivery/Validation/SchemaWalk.cs
  - osdu/src/SqlFlow.Delivery/Validation/ReferenceResolver.cs
  - osdu/src/SqlFlow.Delivery/Validation/ValidationTally.cs
  - osdu/src/SqlFlow.Delivery/Validation/AssertionFindings.cs
  - osdu/src/SqlFlow.Delivery/Validation/MappingAssertionJudge.cs
  - osdu/src/SqlFlow.Delivery/Engine/Assertions/AssertionTemplates.cs
  - osdu/src/SqlFlow.Delivery/Engine/RenderResolver.cs
  - osdu/src/SqlFlow.Delivery/Engine/RouteChecks.cs
  - osdu/src/SqlFlow.Delivery/Engine/SourceRuntime.cs
  - osdu/src/SqlFlow.Delivery/Engine/FlowRuntime.cs
  - osdu/src/SqlFlow.Delivery/Engine/Planning/Planner.cs
  - osdu/src/SqlFlow.Delivery/Engine/Worker/ValidationGate.cs
  - osdu/src/SqlFlow.Delivery/Engine/Worker/ReferenceCheck.cs
  - osdu/src/SqlFlow.Delivery/Source/SourceBindings.cs
  - osdu/src/SqlFlow.Delivery/Source/SqlServerIngestionSource.cs
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryVerbs.cs
  - osdu/gui/src/api/validation.ts
  - osdu/gui/src/features/delivery/ValidationVerdictView.tsx
---

# Preflight: the checks a mapping, a flow and every record pass before anything reaches OSDU

A delivery checks what it can as early as it can, so a mistake surfaces once, with the file and the key, rather than on
every record of a run. The checks come in layers: each layer checks what only it can see, and none of them sends
anything to OSDU except the run's own probes of its target.

| Layer | When | What it needs | Where you see it |
| --- | --- | --- | --- |
| [Reading the documents](#when-a-document-is-read) | every load: `sqlflow validate`, a repository sync, every run | the files | `validate`, the sync's warnings |
| [The preflight gate](#the-preflight-gate) | before any row renders | the template the template folders on the mapping's path hold, and the partition cache (the module's database) | `sqlflow check`, `preview`, `values`, every run |
| [The source tables](#the-source-tables) | when the tables are opened | the ingestion database | `sqlflow check --connect`, every run that reads the source |
| [The run's preflight](#the-runs-preflight) | when a run starts | the target, the ledger | the run's log and result |
| [Each record's render](#each-records-render) | as each record renders | the row | the record's hold reason |
| [The gate before sending](#validation-before-a-record-is-sent) | just before a record is written | the template and the records it refers to | the attempt's verdict |

If the preflight gate finds an error, nothing renders. A run's failed preflight plans and sends nothing, so fixing the
cause and running again is all it takes.

## When a document is read

Loading a flow refuses what is wrong in the document itself: unknown and duplicate keys, a floating mapping reference, a
literal password or other literal secret, a `${...}` reference in a location, malformed names and bounds, cross-key rules such as a header beside `partitions`, and, in the
interface form, a misplaced key, an `after:` cycle or two interfaces keeping one ledger. The messages are on the
[delivery flow](../flow/delivery.md#validation-errors) and [interfaces](../flow/interfaces.md#load-errors) pages.

Loading a mapping parses its header, every node of the record tree, the `$findBy` lines, modifiers and expressions, and
refuses:

- a word the mapping language does not have, naming the one it most likely meant:
  `record.data.Name: '$form' is not a word of the mapping language. Did you mean '$from'? A property whose name starts with '$' is written $$form.`
- a map mixing the language's words with the record's properties, and a key written twice in one map;
- a condition that does not give true or false, or reads no column;
- a `{$param.<name>}` the mapping does not declare, and a mapping that does not declare `dataPartition`;
- `acl.owners`, `acl.viewers`, `legal.legaltags` and `legal.otherRelevantDataCountries` that are not literal lists of at
  least one text value without repeats. The access lists may add value nodes beside their literals; the legal lists may
  not, since the legal service checks them before a run, for every record alike:
  `record.legal.legaltags reads values with record.legal.legaltags[0], and the legal tags are a literal list: the legal service checks them before a run, for every record alike.`
- a modifier out of place, such as `id` or `ref` anywhere but last;
- an assertion (`$assert`) where a node takes none, or one that cannot mean what it says, such as `omit` on a required
  node or the incoming stage on a value the cache gives
  ([mapping assertions](../flow/mapping-assertions.md#errors-when-the-mapping-is-read));
- a search or a lookup that is read and not declared, or declared and not read:
  `search 'Wellbore' is declared and nothing reads it; a search costs a call to the platform for every value it is asked about, so one nothing reads is a mistake rather than spare capacity.`

These need no catalog, so `sqlflow validate` on a folder checks every flow and mapping in it offline. `validate` also
finds and reads the mapping a delivery flow pins and the dictionaries a cache or dimension flow names, as a run finds
them, and refuses a flow whose file is missing; a repository sync and a run keep such a flow and report the missing file
in their own terms. What a mapping writes is described on [mapping](../flow/mapping.md) and its pages.

## The preflight gate

Before any row is rendered, and with no OSDU call, the mapping is judged against its template, the partition's cache and
the flow's parameters. Resolving the render inputs first refuses a missing input:

| Input | Message |
| --- | --- |
| The template the mapping fills | `flows/welldb-welllog-03-header-delivery.yaml: mapping WellLog@1.4.0 cannot render: no template folder on the path of mappings/WellLog@1.4.0.yaml holds template osdu:wks:work-product-component--WellLog:1.4.0 version 26a3c3441882db4f. ...`, the rest saying which folders it read and how to put the file in one ([template folders](templates.md#template-folders)) |
| A cache for a mapping that reads one | `mapping WellLog@1.0.0 reads the cache of partition 'dev', which holds no version yet. Refresh a cache flow that builds it (...) to capture one.` |
| A pinned cache version | `render.cacheVersion pins version <v> of the cache of partition 'dev', which the catalog does not hold.` |
| The module's database, for a mapping that reads the cache | `The osdu schema is read through the catalog's connection, which did not resolve: Environment variable 'SQLFLOW_CATALOG_DB' is not set. Give the command the catalog's connection with --db <conn-ref>, or set SQLFLOW_CATALOG_DB.`, from a command given no `--db` and no `SQLFLOW_CATALOG_DB` |

Then the gate checks, collecting every finding:

1. **The template version.** The template given is the one the mapping pins, at the content version it names.
2. **Searches.** Every search resolves against the schema it pins, and every property its `$findBy` lines compare can be
   matched exactly on the platform, as that schema says the property is indexed.
3. **The parent.** A mapping that reads `$parent` fills a template linked to one parent of another entity type, the
   folders on its path hold the parent's template, and every path it reads is a property of that template, of a shape the
   property it fills can take ([mapping $parent](../flow/mapping-parent.md#what-the-preflight-checks)). A mapping that
   links its children with `$child` links kinds of another entity type whose templates the folders on its path hold and
   name its own entity type as their parent, on properties that take a reference of the child's entity type
   ([mapping $child](../flow/mapping-child.md#what-the-preflight-checks)).
4. **Every property is a variable of the template.** It exists at that version, it is not one OSDU Delivery writes
   (`id`, `kind`) or OSDU sets (`version`, `createTime`, `createUser`, `modifyTime`, `modifyUser`), and it is not a list
   of objects inside another list's items.
5. **Shapes agree.** A `$forEach` fills a list of objects; a single value fills a scalar or a list of values; a literal
   object or list fills an object or a list; a `$findAll` fills a list. Inside an open object the mapping's own shape
   stands.
6. **What the last modifier gives is what the variable takes.** `equals` fills a boolean, `number` a number, an integer
   or unformatted text, `date` text of a date format, and `id` or `ref` text. A text value written to a date or date-time
   without the `date` modifier is a warning.
7. **Every cached type exists** in the cache version the render reads, holds the field the node reads, and holds at
   least one of the fields `$findBy` compares (a field it does not hold is a warning; a type holding none of them is an
   error). A replace reading the cache reads a table the version holds, on fields it holds; values a replace can give that
   then find nothing in the cache are listed as a warning. An `id` modifier's cache token reads a lookup table the version
   holds, at a field its rows hold.
8. **References resolve to the right entity type.** A `$cache: <Type>.id` node, a search, a cached field written to a
   relationship, an `id` or `ref` modifier and a literal on a relationship each give ids of an entity type the
   relationship allows (`data.WellboreID` takes only `master-data--Wellbore`), matching the variable's pattern. A literal
   id, and the ids a cached field holds, must exist in the cache version when it holds that entity type. A `ref` settles
   one entity type.
9. **Every property the template requires in `data` has a node**, and none of those nodes may leave it out
   (`$required: false`); an object the template requires may instead be filled through its properties, at least one of
   which renders on every row:
   `template osdu:wks:master-data--Wellbore:1.3.0 requires osdu.data.<Property>, which the mapping does not fill.`
10. **Parameters.** Every parameter the mapping requires has a value (`mapping parameter '<name>' is required and the
   flow supplies no value.`), the flow supplies none the mapping does not declare, and every `{$param.<name>}` token and
   `$param.<name>` an expression reads has a value.
11. **Columns.** When the flow's tables are known (a run that reads them), every column and child dataset the mapping
    reads exists in them, the key's, the label's, the expressions' and the columns its assertions' conditions read
    included.
12. **Assertions.** Each record-stage condition of a node's `$assert` suits the type of the value its property carries
    (a list judged value by value, a list of objects only by `exists`, `empty` and `length`, an object only by `exists`
    and `empty`), compares with values the property can hold, and every `where` field is a property of the template
    whose condition suits it:
    `<file>: record.data.SamplingStart.$assert[0]: 'data.SamplingStart' is a number in osdu:wks:work-product-component--WellLog:1.4.0 (template 26a3c3441882db4f); matches compares text.`
    See [mapping assertions](../flow/mapping-assertions.md#the-preflight).
13. **Enrichments.** For a mapping with `enrich:`, each enrichment mapping is judged against the template it fills as the
    mapping is, and the chain as a whole: what each enrichment writes and reads, the datasets, files and workflows the
    flow binds, and that `render.parameters` supplies no value no mapping of the chain declares
    ([enrichments](../flow/enrich.md#what-the-preflight-checks)).

When the tables are open a run also checks the flow against them: `source.record.key` names the mapping's `dataset.key`
columns in the same order, the scope, `lastModified` and payload columns exist, and the mapping writes nothing under a
data key `target.protocolOptions.preserveDataKeys` carries over from OSDU.

A failure lists every error at once:

```text
flows/welldb-welllog-03-delivery.yaml: preflight failed with 1 error(s):
  - mappings/WellLog@1.0.0.yaml: record.data.LogName fills a variable that template osdu:wks:work-product-component--WellLog:1.4.0 version 26a3c3441882db4f does not have.
```

Warnings do not stop a run; a run writes them to its log once. `sqlflow check` runs the gate without the source columns
and prints what the flow renders with: the mapping, the template, the cache version, the render context hash, the
mappings folder and, for a source with interfaces, each interface's route, ledger and order
([sqlflow check](../cli/check.md)). Templates and their versions are on [templates](templates.md).

## The source tables

Opening the source (a run that reads it, `sqlflow check --connect`, `sqlflow preview`, `sqlflow values`) reads the
tables' columns and refuses a table the node cannot see, a column the flow names that a table does not hold, a key column
that cannot be compared, a non-date `updated` column, a declared primary key that is not the table's integer identity
primary key, a record key without an unfiltered unique index when a primary key is declared, a row in the run's scope
with a null key column, and a database that refuses snapshot isolation under `isolation: snapshot`. Each message names
the key and the table, and, where a statement fixes it, gives it ([delivery flow](../flow/delivery.md#the-identity-primary-key)).
[The record table a delivery reads](../flow/delivery-record-table.md) lists what the table must hold, the ing flow keys
that make it, and each message with its fix.

## The run's preflight

A run checks, before it plans anything:

- that its route can deliver the kind its mapping renders: a dataset kind on the `dataset` route, never on the `file`
  route; a DSPDM row only on the `dspdm` route; an Energistics object only on the `etp` route; a kind some DDMS the flow
  reaches serves, on a route through a DDMS ([routes](../flow/routes.md));
- for a source with interfaces, every selected interface at once: the ledger can be read, each mapping, template and
  cache resolves, each route suits its kind, the interfaces have an order ([order](../flow/interfaces.md#order)), each
  record table has the shape it needs, and, for an operation that reaches the target, each route's service answers and
  accepts the credentials (a service that cannot be reached, answers 5xx or refuses with 401 or 403 is a finding; another
  client error on its probe path is noted, not refused);
- for a run that plans to deliver (deliver, replan, intake), the mapping's legal tags with the legal service, unless
  `protocolOptions.validateLegalTags` is `false` ([protocols](protocols.md#before-a-run-the-legal-tag-check)):
  `The legal service refuses 1 of the legal tag(s) mapping WellLog@1.0.0 puts on every record, so nothing was planned or sent: ...`

A source's findings come back together:
`The preflight of 'welldb-03-delivery' found 2 problem(s), so nothing was planned or sent: interface 'wellbores': ... | interface 'welllogs': ...`

## Each record's render

The render holds a record, with every reason, when it cannot be delivered as it stands: an empty key column, a value that
cannot take its variable's type, a property the template requires rendered empty, a reference it cannot resolve, an id
whose shape, pattern or entity type is wrong, a child dataset over its row ceiling, a deleted row, or a route's own rules
(the Wellbore DDMS's, External Data Services'), or a parent its mapping reads with `$parent` that OSDU does not hold
([mapping $parent](../flow/mapping-parent.md)). Held records keep their reasons in the ledger; records held the same way
share one issue ([record lifecycle](record-lifecycle.md)). `sqlflow values` renders every row of a scope and reports
these, variable by variable, before a run does ([sqlflow values](../cli/values.md)).

The render also judges the mapping's assertions ([mapping assertions](../flow/mapping-assertions.md)): an incoming one
as its node reads the row, a record-stage one on the assembled record. A failure whose action is `omit` leaves its value
out of the document there; what the others found travels with the document to the check before sending, which holds the
record for a failure whose action is `hold`. A render never holds a record for an assertion.

## Validation before a record is sent

Immediately before a try writes a record's document, the gate checks the document against the template of its kind at
the version it was rendered for (whatever the flow pins now), as the route will send it. It applies every rule of JSON
Schema that OSDU's schemas use: types, formats, patterns, enumerations, constants, lengths, bounds, item counts and
uniqueness, required properties at every depth, properties an object does not allow, the forms a `oneOf` or `anyOf`
allows, and the entity types a relationship allows. The records the document refers to are looked up once for each group
of records sent together: in the ledger first, then in OSDU's storage service under `target.verifyReferences: storage`,
else in the cache version the flow renders with, for the entity types it captures.

| Verdict | When |
| --- | --- |
| `valid` | every rule that applies was checked and met |
| `invalid` | a rule is broken, or the document refers to a record that the cache (or storage, when asked) does not hold |
| `unverified` | nothing is broken and some part could not be checked: a pattern no regular expression dialect reads, a check that ran out of time or walked past its bounds, a reference nothing could answer for, or no template of the kind at the version the document was rendered for in a template folder on the mapping's path |
| `notValidated` | the try sends the payload alone, so nothing of the document is sent or checked |

`target.validation` decides which verdicts hold the record; every verdict is recorded on the try's attempt, and the record
keeps the last outcome:

| Setting | What the gate does |
| --- | --- |
| `mode: report` (default) | sends every record and records its verdict |
| `mode: enforce` | holds an `invalid` record with its document kept |
| `unverified: send` (default) | sends an `unverified` record and records what was not checked |
| `unverified: hold` | holds an `unverified` record as `enforce` holds an invalid one |

A held record's reason names the rules it breaks by the property path every record shares, so records broken the same way
share one issue:

```text
validation: the record breaks the schema of osdu:wks:work-product-component--WellLog:1.4.0: <path> <rule>: <message>; and 2 more. validation.mode is enforce, so it is held; release it to send this document as it is, or correct the source or the mapping.
```

Releasing a record the gate held accepts its document as it is: it is sent whatever its verdict says, and the verdict
records that it was accepted. A document rendered differently later is judged again. What the route itself fills when it
sends a record (the dataset list of a route that registers files or datasets, the dataset properties the Dataset service
fills, the data keys an update carries over from OSDU, and the bulk link a DDMS manages) is not judged as it was rendered.
A run's trace counts the verdicts by template and names the rules broken most often, rather than writing a line per
record. In the interface form, an interface's `validation` block lays its keys over `target.validation`.

### What a verdict holds

A verdict is one JSON object, the same wherever it is read: under `validation` in an attempt's result (a record's
history in the GUI and the CLI), and in the explorer's answers ([explorer](explorer.md#validate)). Its fields, in the
order they are written:

| Field | What it holds |
| --- | --- |
| `outcome` | `valid`, `invalid`, `unverified` or `notValidated`, as the table above says. |
| `schema` | What the record was checked against: `kind`, `version` (the schema's content version, as [templates](templates.md) name it) and `source`, which is `template` for a template OSDU Delivery holds and `schema-service` for the partition's Schema service. The gate before sending always checks against the template version the document was rendered for, read from the template folders on the mapping's path; once no folder there holds that version any longer, the record is `unverified`. The explorer checks against a template a synced repository holds or the Schema service's. Absent when no schema could be had. |
| `rules` | How many rules were applied. |
| `problemCount`, `problems` | Every problem found is counted; the first 50 the check finds are listed, and each reference nothing holds is added as a problem of the rule `reference`, at most 20 of those listed. |
| `unverifiedCount`, `unverified` | Every part not checked is counted; the first 20 are listed. |
| `references` | The records the document refers to, counted by where they were found: `total` (the distinct ids it names at its relationships), `ledger`, `cache`, `osdu`, `missing` and `notChecked`; `cut` when the document names more ids than a check collects (10,000), so the counts cover the first ones; and `missingIds`, at most 20 ids not found, each with `id`, `at`, `path` and `detail` (what said it is missing). |
| `notes` | What the schema states that no check asserts, what the check did that a reader should know (a block of a stored record read as absent), and for `notValidated`, why nothing was checked. |
| `assertions` | What the mapping's assertions found ([below](#a-mappings-assertions)); absent when the mapping states none. |
| `accepted` | `true` when a release accepted the document as it is, so it was sent whatever the verdict says; absent otherwise. |
| `rulesVersion` | The version of the checks the verdict was reached by, now `1`. It moves when what a check finds changes meaning, so verdicts reached by different releases can be told apart. |
| `checkedUtc` | When the check ran. |
| `shortened` | `true` when the listings were cut to fit an attempt's result; absent otherwise. |

Each problem and each part not checked is a finding of five fields: `at`, the property path every record shares
(`data.VerticalMeasurements[].VerticalCRSID`, empty for the record itself); `path`, exactly where
(`data.VerticalMeasurements[2].VerticalCRSID`); `rule`, the JSON Schema keyword broken (or `relationship`, or
`reference`), and for a part not checked why it was not (`pattern`, `schema`, `budget`, `depth`, `items`, `error`, or the
schema keyword no check covers, such as `not` or `if`); `message`, what is wrong, naming the value; and `value`, the value
as text, clipped. Messages and values are redacted as a stored error is.

A check of one record keeps to bounds, so a record of any size is checked in bounded time: at most 1,000,000 values
walked, 64 levels deep, 100,000 items of one list and 5 seconds. What lies past them is counted as not checked, never as
met. A verdict takes at most 16,000 characters in an attempt's result. Past that its listings are cut step by step until
it fits, and `shortened` is set:

1. the parts not checked to 3;
2. then also the problems to 10, the missing ids to 5 and the assertions' failures to 10;
3. then the problems to 3, the parts not checked to 1, the missing ids to 1 and the assertions' failures to 3, each
   finding's message clipped to 300 characters and its value to 60.

Every count stays exact.

### A mapping's assertions

What the mapping's assertions found of the document when it was rendered joins the verdict as `assertions`: how many
judgements were made, how many failed, held, were reported and left out, and the failures (at most 50 listed, every one
counted). They are judged beside the schema, not by it, so the verdict's outcome still says what the schema check came
to. A failure whose action is `hold` holds the record with its document kept whatever `target.validation` says, under an
error that names the property, the assertion and the value; when the policy holds the record for its schema too, the
error says both:

```text
assertion: the record fails what its mapping WellLog@1.4.0 asserts: data.VerticalMeasurement.VerticalMeasurement fails "reference-elevation" with '312'. Its mapping holds a record that fails them (onFail: hold), so it is held; release it to send this document as it is, or correct the source or the mapping.
```

A release accepts the document as it is, whatever its assertions found. A try that sends the payload alone judges no
assertion, and a document queued before mappings could assert carries no findings. The record keeps how many judgements
failed on the last document checked (`AssertionFailures`, [ledger](ledger.md)), and the run's trace adds per template how
many documents failed an assertion and the assertions failed most. See
[mapping assertions](../flow/mapping-assertions.md#the-check-before-sending).

### References checked in storage

`target.verifyReferences: storage` is for a source that must not write a reference to nothing. The ids a document refers
to that no record of the ledger holds are asked of OSDU's storage service before the record is sent, and a record naming
one storage does not hold is held whatever `target.validation` says:

```text
refers to dev:master-data--Wellbore:WB-0099 (data.WellboreID), which neither the ledger nor OSDU's storage service holds; target.verifyReferences is storage, so the record is not sent with a reference to nothing. Release it once the records it names exist.
```

An id a record of the ledger holds and has not delivered yet is not held but waited for
([records that wait for records](../flow/interfaces.md#records-that-wait-for-records)). The setting is refused on the
`dspdm` route, whose rows refer to no storage record.

## See also

- [sqlflow check](../cli/check.md), [sqlflow preview](../cli/preview.md), [sqlflow values](../cli/values.md): the
  checks run by hand.
- [Templates](templates.md): what the gate checks a mapping against.
- [Record lifecycle](record-lifecycle.md): held records, issues and releases.
- [Delivery flow](../flow/delivery.md#target): `target.validation` and `target.verifyReferences`.
- [Validate](../../../../sqlflow/docs/reference/cli/validate.md): SQLFlow's offline document check.
