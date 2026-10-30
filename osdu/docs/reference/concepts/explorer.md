---
id: delivery-concept-explorer
title: "The explorer: browsing and searching what an OSDU partition holds"
type: concept
summary: "How the explorer reads an OSDU partition live: browse types, search by id, text, property or source column, open records and versions, validate, references."
keywords:
  - explorer
  - explorer connection
  - search without a flow
  - browse osdu
  - search osdu records
  - what is in osdu
  - lucene query
  - record inspector
  - compare versions
  - validate records
  - schema check
  - validation guidance
  - how to fix a record
  - osdu example record
  - referenced by
  - mentioned by
  - group by
  - filter by property
  - build a dimension
related:
  - delivery-concept-search-terms
  - delivery-concept-gui
  - delivery-concept-templates
  - delivery-concept-preflight
  - delivery-flow-assertion
  - delivery-flow-mapping-assertions
  - delivery-flow-dimension
  - delivery-flow-inventory
  - delivery-flow-inventory-selections
  - delivery-concept-partitions
sourceRefs:
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryExplorerEndpoints.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryExplorerEndpoints.Validate.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryExplorerEndpoints.References.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryExplorerEndpoints.Element.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryExplorerEndpoints.Dimension.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryExplorerEndpoints.Terms.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DirectOperationRunner.cs
  - osdu/src/SqlFlow.Delivery/Engine/Operations/ExploreOperation.cs
  - osdu/src/SqlFlow.Delivery/Engine/Operations/TargetClients.cs
  - osdu/src/SqlFlow.Delivery/Engine/Operations/PartitionConnection.cs
  - osdu/src/SqlFlow.Delivery/Engine/Search/RecordExplorer.cs
  - osdu/src/SqlFlow.Delivery/Engine/Search/RecordExplorer.Via.cs
  - osdu/src/SqlFlow.Delivery/Engine/Search/OsduSearch.cs
  - osdu/src/SqlFlow.Delivery/Engine/Search/ExplorerChecks.cs
  - osdu/src/SqlFlow.Delivery/Engine/SyncedMappings.cs
  - osdu/src/SqlFlow.Delivery/Validation/MappingAssertionJudge.cs
  - osdu/src/SqlFlow.Delivery/Validation/ValidationGuide.cs
  - osdu/src/SqlFlow.Delivery/Validation/OfficialExamples.cs
  - osdu/src/SqlFlow.Delivery/Templates/OsduDataDefinitions.cs
  - osdu/src/SqlFlow.Delivery/Engine/Search/ExplorerReferences.cs
  - osdu/src/SqlFlow.Delivery/Engine/Search/PartitionSchemaIndex.cs
  - osdu/src/SqlFlow.Delivery/Engine/Search/SchemaServiceReader.cs
  - osdu/src/SqlFlow.Delivery/Engine/Search/ExplorerElementQueries.cs
  - osdu/src/SqlFlow.Delivery/Engine/Search/ExplorerFieldCatalog.cs
  - osdu/src/SqlFlow.Delivery.Search/OsduQuery.cs
  - osdu/src/SqlFlow.Delivery/Engine/Protocols/OsduRecordProtocol.cs
  - osdu/src/SqlFlow.Delivery/Http/OsduCorrelation.cs
  - osdu/hosts/osdu-delivery-mcp/src/links.rs
  - osdu/gui/src/module.tsx
  - osdu/gui/src/features/delivery/explorer/ExplorerPage.tsx
  - osdu/gui/src/features/delivery/useLinkedPartition.ts
  - osdu/gui/src/features/delivery/explorer/ExplorerWelcome.tsx
  - osdu/gui/src/features/delivery/explorer/ExplorerResults.tsx
  - osdu/gui/src/features/delivery/explorer/ExplorerTypeRail.tsx
  - osdu/gui/src/features/delivery/explorer/ExplorerSearchBar.tsx
  - osdu/gui/src/features/delivery/explorer/ExplorerFilterEditor.tsx
  - osdu/gui/src/features/delivery/explorer/ExplorerRecord.tsx
  - osdu/gui/src/features/delivery/explorer/ExplorerValidation.tsx
  - osdu/gui/src/features/delivery/ValidationGuideView.tsx
  - osdu/gui/src/features/delivery/ValidationVerdictView.tsx
  - osdu/gui/src/api/validation.ts
  - osdu/gui/src/features/delivery/explorer/ExplorerReferences.tsx
  - osdu/gui/src/features/delivery/explorer/ExplorerElementQuery.tsx
  - osdu/gui/src/features/delivery/explorer/explorerModel.ts
  - osdu/gui/src/features/delivery/explorer/dimension/DimensionBuildPanel.tsx
  - osdu/gui/src/features/delivery/OsduRecordInspector.tsx
  - osdu/specs/core/search/openapi.yaml
  - osdu/specs/core/storage/openapi.yaml
  - osdu/specs/core/schema_service/openapi.yaml
---

# The explorer

The explorer is a browser of what an OSDU partition holds, read live from OSDU's own search, storage and schema
services. It answers "what is in OSDU?", where the Records page answers "what did the delivery system do?". Nothing it
shows comes from the ledger, a mapping, the cache or the catalog: the types, the records, their versions, their links
and the records that mention them are what OSDU answers at that moment. Delivery metadata only ever helps write a query:
a search by a column of a source system ([search terms](search-terms.md)) is carried to what the records hold the way a
delivery carries it, then asked of OSDU like any other. The explorer never reads a DDMS.

It is the **Explorer** entry of the menu's **OSDU** group (`/delivery/explorer`, [the menu](menu.md)), and it reads the
partition picked in the title bar.

## How it reaches OSDU

The explorer needs no flow. Every read runs in the control plane as the request's answer (the operation
`delivery-explore`), nothing queued and nothing polled, through the partition's own connection: the platform and the
client the deployment names for the partition, by five of the values its runs resolve `${env:NAME}` references with
([configured partitions](partitions.md#configured-partitions)):

| Value | What it is |
| --- | --- |
| `OSDU_URL` | The platform's base URL; search, storage and schema are read at their own paths under it. |
| `OSDU_TOKEN_URL` | The token endpoint the client signs in at, with the OAuth 2.0 client credentials grant. |
| `OSDU_SCOPE` | The scope the token is asked for. |
| `OSDU_CLIENT_ID` | The client (application) id. |
| `OSDU_CLIENT_SECRET` | The client secret, a `${keyvault:<vault>/<secret>}` or `${env:NAME}` reference. |

Each is read as a run reads it: the control plane's value for the partition (`Osdu:Partitions:<id>:Properties:<NAME>`,
or [sqlflow config](../cli/config.md) `set --partition <id>` with no repository), then its value for no partition
(`Osdu:Properties:<NAME>`), then the control plane's own environment, which is where a local setup's `.sqlflow/env` puts
them. A repository's values are not read, since the explorer belongs to no repository. Every request carries
`data-partition-id` set to the partition, and a read's requests a `correlation-id` of `osdu-delivery-explore-<id>`, a
new id for each read, which its answer gives back as `correlationId`; the reading of the partition's schemas behind
Referenced by carries `osdu-delivery-schema-<id>` of its own ([request correlation](request-correlation.md)). A deployment can
therefore be searched as soon as its configuration names its partitions, before a repository is synced or a flow
delivers anywhere.

The partition is the one picked in the title bar (the request's `partition`, else the workbench's `X-Osdu-Partition`
header, else the registry's default), and it has to be registered. When its connection cannot be made, every read
answers 409 **No connection to the partition** and the page says why: a partition the registry does not hold is refused
as a run in it is (`Partition 'prod' is not registered with the catalog, so nothing runs in it. ...`), and a registered
one names the values its configuration and the control plane's environment leave unset, for example
`The explorer reads partition 'test' through the platform and client its configuration names, and OSDU_TOKEN_URL, OSDU_CLIENT_SECRET are not set. Set them under Osdu:Partitions:test:Properties in the control plane's configuration, or in its environment.`

When the connection can be made, a green badge with the partition's name stands beside the page's title; its tooltip,
**Read live from OSDU**, names the platform it reaches: the base URL as scheme, host, port and path, without the
credentials, query or fragment a URL can carry, or a configured reference (such as `${keyvault:...}`) as written, since
that is resolved only when a read needs it.

The explorer reads:

| Service | Calls | For |
| --- | --- | --- |
| Search | `POST /api/search/v2/query` | types and their counts, records, groups of values, mentions |
| Storage | `GET /api/storage/v2/records/{id}`, `GET /api/storage/v2/records/versions/{id}`, `GET /api/storage/v2/records/{id}/{version}` | a record, its version list, an older version |
| Storage | `POST /api/storage/v2/query/records` | the records a check of a search reads, and those a record refers to, during a validation |
| Schema | `GET /api/schema-service/v1/schema`, `GET /api/schema-service/v1/schema/{id}` | a kind's properties, validation, Referenced by |

A record is read the way a record page's OSDU tab reads it back, so both pages show a record the same way. A record
page's check of the record it shows (`POST /api/v1/delivery/flows/{pipelineId}/osdu/validate`) is the one read made
through a flow: the record's own, with its route and credentials, as the page reads the record; there a `storage` flow's
`protocolOptions.verifyPath`, and any flow's `protocolOptions.verifyBatchPath`, replace the default storage paths.

The control plane keeps the connection between reads (at most 64 targets, each retired after 10 minutes unused or 30
minutes in all, and at once on the 401 a rotated secret draws), so a token is fetched once rather than for every read.
A person is waiting on these reads, so they retry as a person would rather than as a run: at most two attempts, a
backoff of at most two seconds, no wait on a `Retry-After`, and at most 60 seconds (or, for a record page's check, its
flow's own shorter timeout) for an answer.

**Who may use it.** The page's route asks for the `operate` scope, and its reads sit in the control plane's operate
group, because they use the partition's credentials. SQLFlow resolves `operate` to any signed-in user
([authentication and identity](authentication-and-identity.md)), so in practice anyone signed in can use the explorer.
The MCP server offers none of its routes: it answers from metadata alone, and the explorer reads OSDU's data. Its
answers link here instead: an OSDU id they name opens that record in its partition (`?id=...&partition=...`), a kind its
records, and a partition the explorer on it.

## The welcome

The page opens on a welcome that reads nothing from OSDU, so it shows at once however large the partition:

- **Recently opened**: the records opened lately in this browser (the last 20), as a grid; **Clear** forgets them.
- **Types**: **Browse every type**, and the types browsed lately (the last 12).
- **Search syntax**: what the search field takes, with an example of each.

Both lists are kept in the browser only, and a record opened in another partition opens in that partition. Past the
welcome, **Recent** beside the search field lists the same records (**Forget** clears them). While no record is open,
`/` anywhere on the page outside a field puts the cursor in the search field.
OSDU is read only once the reader asks: **Browse every type** reads the types and waits for one to be picked, a type
picked reads its records, a search reads what it finds, and a record opened reads that record and nothing behind it.

## Types and records

Once asked, the page shows the partition's kinds beside the records of the place picked.

- **The type list.** One search aggregation over `kind` across every kind (`*:*:*:*`) counts the records of each, and the
  list groups them as OSDU names them: the group (`master-data`, `reference-data`, `work-product-component`, `dataset`),
  the type, and under a type kept in several kinds (versions, authorities) each kind. Groups start folded, but for the
  one holding the place picked, and while a search is in view the groups it finds something in open (unless one holds
  more than 24 types). **Filter types** finds a type among hundreds; **All types** lists every record of the partition.
  Picking a group, a type or a kind narrows the records to it (`*:*:master-data--*:*`, `*:*:master-data--Wellbore:*`, or
  the kind). The counts follow the search and the conditions, so the list also says where else a search finds something.
- **The records grid.** One row per record: its name (the first of `data.FacilityName`, `data.Name`,
  `data.ProjectName`, `data.Code` or the file name in `data.DatasetProperties.FileSourceInfo.Name`, else the unique part
  of its id), its type unless one type is in view, its id (cut from the start, so the end that tells ids apart stays
  visible, with a copy on hover), when it last changed, and a column for each property a condition asks, so the grid
  shows why each record is there. Rows load a hundred at a time as the grid scrolls; only the rows in view are drawn.
  The arrow keys, Page Up and Down, Home and End move through them, and Enter opens one.
- **The place line** over the grid says where the records are (each step a way back), how many there are (the index's
  exact count), and what the search service made the explorer do. At its end: the order, **Filter**, **Group by**, then
  as icons the query sent, **Referenced by**, **Validate** and **Read again from OSDU**.
- **Order**: Index order (Best match once text is typed), Last modified or Last created, newest first. An order the
  service refuses is dropped with a note.
- **Group by**: any property of the records in view, grouped by its distinct values with their counts; a value picked
  narrows the records to it, as the condition **is**. A property inside a nested list is grouped through it.

The search service pages through the first 10,000 records a query matches; past them the grid's foot says to narrow the
search. Every page is one query of the index, so a partition of millions reads as fast as one of hundreds. What was read
is kept for a minute and reused (the types of a whole partition and a kind's properties for ten minutes); **Read again
from OSDU** reads at once.

## Searching

One search field, in the page's header, searches the place in view and keeps it (`Search Wellbore by id, name or any
text`, or `Search every type by id, name or any text`). Clearing it (its cross, Escape, or Enter on the empty field)
lists every record of the place again. A hint at the field's end says what Enter will do (`open`, `search`, `query`,
`clear`). What is typed is read once into a query:

| Typed | Read as | Query sent |
| --- | --- | --- |
| nothing | every record of the place | none |
| a whole id, `dev:master-data--Wellbore:abc123` (a trailing colon is dropped; with a version, it opens at that version) | opens the record | |
| the start of an id, `dev:master-data--Wellbore:` or `master-data--Wellbore:WB-` (completed with the partition) | the ids starting with it | `id:dev\:master\-data\-\-Wellbore\:WB\-*` |
| words, `Wellbore A 1` | a phrase anywhere in a record | `"Wellbore A 1"` |
| one word of three or more characters with one type in view | the phrase, or the record of that type whose id is or starts with it | `"WB-0042" OR id:"dev:master-data--Wellbore:WB-0042" OR id:...*` |
| a machine-minted unique part (a GUID, a hash) in every type | the phrase, or any id of the partition ending with it | `"..." OR id:dev\:*\:...*` |
| a Lucene query, with the braces button on | as written | the text |

A value is always sent as a quoted phrase, since the service ORs bare words together. A query the service refuses (400)
is not a failure: the explorer drops the order, then the clause the reading added beyond its core (the id clauses), each
with a note, and a query still refused is the page's answer, with the service's words (**The search service refused this
search**). Anything else that keeps an answer from coming fails the read, since an empty page would say OSDU holds
nothing. A search is at most 2,000 characters, and a character that cannot be printed is refused (a Lucene query keeps
its line breaks and tabs).

**The query** icon in the place line shows what the list is read by, exactly as sent: the `kind` and the Lucene `query`
(none where the list is every record of the kind). It is copied as written or as a request, or taken into the search
field with **Edit**, to be changed there.

## Conditions and properties

What is typed in the field is searched in every property. It can be searched in one property or source column instead,
picked at the field's start (**In** a property; **Every property** goes back), and any property can be asked a condition
of its own with **Filter**. Every condition holds together with the text, and each is a chip over the grid that reads as
a sentence (`FacilityName starts with Wellbore A`); a click opens it again, its cross drops it, **Clear all** drops every one. A
page takes at most 12 conditions. The editor lists the values the records in view hold at the property, the commonest
first with their counts; picked under **contains** or **starts with**, a listed value becomes **is**.

Every condition is written the way the platform indexes the property:

| Condition | For | Query |
| --- | --- | --- |
| **contains** | text | the words as a phrase anywhere, any case: `data.FacilityName:"Wellbore A"` |
| **is**, **is not** | any | the whole value: `data.Source.keyword:"welldb"`, `NOT (...)` |
| **is one of**, **is none of** | any but a boolean | up to 50 whole values: `(data.Source.keyword:("welldb" OR "WELLDB"))` |
| **starts with** | text, a keyword | the start of the whole value, case included: `data.FacilityName.keyword:Wellbore\ A\-*` |
| **is in a range** | a number, a date | from a value (included) up to another (not included), either end open: `data.TopMeasuredDepth:["1000" TO "2000"}` |
| **has a value**, **has no value** | any | `_exists_:data.FacilityName`, `NOT (...)` |

A property inside a nested list (`data.GeoContexts.FieldID`) is asked in the service's nested form,
`nested(data.GeoContexts, (FieldID.keyword:"..."))`; **starts with** and the two value tests are not offered there. The
search service refuses a query whose every clause excludes, so conditions that only exclude, with no text beside them,
start from every record: `_exists_:id AND NOT (...)`. A value the index cannot compare (longer than the 256 characters a
keyword keeps whole, a word where a number is indexed) is refused before anything is read, with why.

For a type or a kind, the editor lists that type's [search terms](search-terms.md) first, as **Source columns**: a
value typed for one is the source's own, carried through the mapping before OSDU is asked.

**The properties of a place**, offered by Filter, Group by and the field, are read in one ask and grouped:

- **Content**: every value the kind's schema declares that a query can reach, read from the partition's Schema service,
  each asked as the schema has the platform index it, with the schema's title and description on hover. For a type, the
  schema read is that of its kind holding the most records.
- **Not in the schema**: values the first 20 records of the place hold that the schema does not declare, such as
  properties an index augmentation copies onto a record, or properties of another version of the kind.
- **Record**: the properties every record holds: `kind`, `id`, `createUser`, `createTime`, `modifyUser`, `modifyTime`,
  `acl.viewers`, `acl.owners`, `legal.legaltags`, `legal.otherRelevantDataCountries`.

Across every type, only the record's own properties and the name properties are offered. A schema that cannot be read is
a note at the foot of the list.

An [inventory](../flow/inventory-selections.md) selects its ids by the same conditions, made in the
same editor on the inventory's kind and asked of OSDU through the inventory flow's own source.

## A record

A record opens in the record inspector (the one the record page's OSDU tab uses), filling the page, with **Back to the
records** returning to the list as it was left. Its outline:

- **Record**: **Full document** (the record as OSDU returned it), **System fields** (Id, Kind, Version, Created, Last
  modified, when it was read and through what (here `partition <id>`, the partition's connection; on a record page, its
  flow), the correlation id) and **Access & legal** (Viewers, Owners, Legal tags, Countries, Legal status).
- **Content**: the record's own blocks (`data`, `meta`, `tags`, `ancestry`), shown as **Fields** (rows of field and
  value, a list of objects as a table) or **JSON**. A value that names another OSDU record is a link that opens it in the
  inspector. **Find in record** counts and steps through matches.
- **References**: **Linked records**, every record the document names, by type, each with the paths that name it.
- **Checks**: **Validation** (see [Validate](#validate)) and **Mentioned by**, the records whose values name this one
  (`"<id>" AND NOT id:"<id>"` over every property), counted by type with the first hundred listed (**Browse all** opens
  them all as a search when there are more).

**Versions.** The version picker reads the record at any version OSDU keeps. **Compare** puts any two side by side: what
was added, changed and removed, the two documents with their unchanged stretches folded, and every moved value with its
path. A version is read once and kept, since a version never changes. The record can also be downloaded as JSON.

**An id OSDU holds nothing under** answers `OSDU holds no record under this id`, and offers **Records with a near id**:
those whose ids go on from it (a paste cut short) and the same unique part under another type. An id of another
partition than the one in view says so, with **Read it in** that partition.

**The query of an element.** Beside every value and section of a record, a search button shows the Lucene query that
finds the records holding exactly that element, with **Copy** and **Search**: a value by its exact form (a text by its
`keyword` sub-field), its words, and whether a record holds one; an item of a nested list as one `nested(...)` clause
with all its values; a list or an object as every value it holds (up to 48). How each value is indexed is read from the
template of the record's kind a synced repository holds (of a version no repository holds, the newest of its type they
hold); where no template says, the query is marked **guessed**. Nothing is read from OSDU to write it.

Every record opened is remembered in this browser (the last 20). An [inventory](../flow/inventory.md) report opens one of
its ids in this same record view, in the workbench's bottom panel, with **Open in the explorer** to come here; a record
page's OSDU tab opens its record here the same way.

## Validate

The explorer checks what OSDU holds against what OSDU expects of it, with the same check the delivery system makes
before it sends a document ([preflight](preflight.md)), so a record the explorer calls invalid breaks the rule a
document would be held for.

**Validation**, under a record's Checks, checks the version in view against:

- **OSDU schema** (the default): what the partition's Schema service holds for the kind, with every schema it refers to
  bundled in (at most 200 for a kind). A reference that cannot be followed is named, and the values it describes are
  counted as not checked rather than passed.
- **Repository template**: a [template](templates.md) a synced repository holds in a template folder, read from its
  file: the version the repository synced most recently holds, or a version picked among those the repositories hold.
  A kind no repository holds says so, and how to put its file in a template folder.

The answer is the verdict a delivered record carries in its history: **valid**, **invalid** with the number of
problems, or **unverified** when a part could not be checked and nothing else is wrong. Under it: **Problems** (where,
the rule, what is wrong, and [guidance](#guidance-on-each-problem): the value found, what the schema takes there, how
to fix it, and OSDU's own example value for the kind where the data definitions publish one), **Not checked** (and why),
and **References not found** (ids the record names that storage holds nothing under, looked up in storage alone). A
record storage does not hold, a kind with no schema, or a Schema service that refuses to answer is reported as not
checked, with why. A record read back with `ancestry`, `meta` or `tags` null or empty is read as if the block were
absent, and the answer says so.

**Validate these records** (the shield in the place line) checks the records the search finds, as it stands, against the
schemas of their kinds (the Schema service's, or each kind's repository template, as the most recently synced repository
holding it holds it): the first 1,000 the search returns, read from storage in batches. It answers how many records
the search matches and how many were read, how many came to each outcome, **Rules broken most often** (each rule at each
place with the records that break it and one example, at most 50 rules), and **Records** with each record's outcome and
first problem. When the search matches more than a check reads, it says so; a whole kind is checked by an
[assertion flow](../flow/assertion.md)'s `conforms` test.

A record page's OSDU tab offers the same Validation for the record it shows, read through that record's own flow and
partition.

**A mapping's assertions.** A check can also hold the records to the business rules a mapping states
([mapping assertions](../flow/mapping-assertions.md)): the request names a synced mapping by its id (`mapping`, as
`GET /api/v1/delivery/mappings` lists it), and the mapping's record-stage assertions are judged on each record, as a
delivery judges the documents it renders. In the explorer, both checks offer the mapping beside the schema (**No
mapping's assertions** until one is picked). A record page's check judges the assertions of the record's own flow's
mapping unless the request names another.

- **Validate** adds `assertions` to the verdict: how many judgements were made and failed, and each failure with the
  property, where it was found, the assertion, its stage and `onFail`, why it fails and the value. A record no schema
  can be had for is still judged: the answer says why it was not checked (`problem`), and its verdict is `notValidated`
  with the assertions' findings.
- **Validate these records** counts the records of the entity type the mapping renders (`asserted`), those failing one
  of its assertions (`failingAssertions`) and, most records first, each assertion they fail with one example (at most
  50); each record's line carries how many judgements failed on it.
- **What is not judged is said** among the notes: the incoming assertions, which judge the rows records were rendered
  from and so not what OSDU holds; a record of another entity type than the mapping renders (one of another version of
  the same type is judged on what it holds at the same properties, and the note says so); and a mapping that cannot be
  read (`No assertions were judged: no mapping is synced under <id>; its repository may no longer declare it.`).

Nothing is held or left out of a record OSDU already holds: every failure is reported, whatever its `onFail`. A whole
kind is held to a mapping's assertions by an [assertion flow](../flow/assertion.md#holding-records-to-a-mappings-assertions)'s
`mapping` test.

### Guidance on each problem

The answer of one record's check (`POST /api/v1/delivery/explorer/validate`, and a flow's own
`POST /api/v1/delivery/flows/{pipelineId}/osdu/validate`) carries `guidance` beside the verdict
([what a verdict holds](preflight.md#what-a-verdict-holds)): for each finding, the value found, what the schema expects
there and how to meet it. Guidance is worked out when the check is asked, from the schema the check read and the record
it checked. No verdict stores it: an attempt's verdict in a record's history is shown without guidance, and asking the
explorer again works it out again. A template version never changes, so it gives the same guidance whenever it is asked.

| Field | What it holds |
| --- | --- |
| `problems` | One guide per problem the verdict lists, in the verdict's order. |
| `unverified` | One guide per part not checked the verdict lists, in its order. |
| `expectations` | What the schema expects, by place: the key a guide's `expected` names. |
| `example` | The OSDU data definitions' example record the expectations quote: its `release`, its `path` in the repository and the `webUrl` a person reads it at. Absent when none was read. |
| `exampleNote` | Why no example is quoted, when one was looked for: `The OSDU data definitions publish no example record of <kind>.`, or `OSDU's example record of <kind> could not be read: <reason>`. |

Each guide has five fields:

| Field | What it holds |
| --- | --- |
| `path`, `rule` | The finding's path and rule, as the verdict lists them. |
| `found` | The value found there, in words: `absent` for a required property, `null`, text in single quotes (`'Wellbore A-1'`, clipped at 160 characters), `a list of 3 items`, `an object of 2 properties`, or a number or boolean as JSON. Where the record is not at hand, the finding's own value. |
| `expected` | The place in `expectations` that describes this value, or null when the schema describes nothing there. A finding at an item of a list (`data.Curves[2]`) points to what each item of the list is (`data.Curves[]`). |
| `advice` | How to make the value meet the schema, or null when there is nothing to say beyond the finding's message. |

An expectation says what the schema says of the value at its place (`at`, with `[]` at the end for each item of the list
there): its `title` and `description`; `summary`, one line saying what a value there is (`text matching ^NO , of at most
64 characters`, `the id of a master-data--Wellbore record`, `one of 12 values`); the JSON `types` it may be (`null`
among them when the schema allows it); whether the object holding it makes it `required`; its `patterns` and `formats`;
the values it allows (`allowed`, at most 25 listed, `allowedCount` counting them all); its length, item and number
bounds, `multipleOf` and `uniqueItems`; the `entityTypes` an id written there may be of; for an object, its `properties`
(at most 40 listed, `propertyCount` counting them all), `requiredProperties` and whether it allows only those
(`onlyNamedProperties`); for a list, what each item is (`items`); how many forms a `oneOf` or `anyOf` allows (`forms`);
the schema's own `examples`; and `osduExample`, the value the data definitions' example record holds at that place, the
first item of each list taken, clipped at 300 characters.

Advice is written for the rule broken, naming the property and quoting one example where there is one (the schema's
first, else the data definitions' value):

| Rule | Advice |
| --- | --- |
| `required` | `Add <name>: the schema requires it, and it takes <summary>.` |
| `type` | Write the value as the schema's type, with a hint for the common slip: a number in quotes (`Write the number itself, without quotes: 1520.5.`), `true` or `false` in quotes, a list where one value is taken, one value where a list is. A null where null is not taken says to leave the property out when it is not required. |
| `pattern` | Change the value so it matches the pattern; at a relationship, write the id of a record of the entity types allowed as OSDU writes ids, `<partition>:<entity type>:<code>:` with the closing colon or a version. |
| `enum`, `const` | The values the schema allows, ten listed and the rest counted. |
| `format` | The format with a sample: `Write SpudDate as an RFC 3339 date, such as 2026-09-01.` |
| `minLength`, `maxLength`, the number bounds, `multipleOf`, `minItems`, `maxItems`, `uniqueItems` | The length, range, step or item count the schema takes. |
| `additionalProperties` | Remove the property or name it as one the object describes (listed); where the object has `ExtensionProperties`, a property of your own belongs under it. |
| `relationship` | For a name or code where an id belongs: write the id of a record of the entity types allowed, not its name or code; the cache of the reference data, or a lookup, gives a code its id. For an id of another entity type: point the property to a record of a type allowed. |
| `reference` | Deliver the record named to OSDU before this one, or correct the id. |
| `anyOf` (a `oneOf` or `anyOf` no form of which the value matches) | Give the value the shape of one of the forms the schema allows; the message says where the first form differs. |

A part not checked is told there is nothing to change in the record (the pattern cannot be run, the schema could not be
read there, or the part lies past the check's bounds), or, for `error`, to check again and report the message if it
repeats.

**OSDU's example record.** For a kind of the `osdu` authority, the guidance quotes the example record the newest release
of the OSDU data definitions publishes for that kind version, `Examples/<group>/<entity>.<version>.json`
(`Examples/work-product-component/WellLog.1.4.0.json`). It is looked up only when the verdict lists a problem or a part
not checked, once per check; each example is read from the repository once and kept in the control plane's local copy
beside its release, and a release that publishes none for a kind is remembered there too. A kind of another authority
has none to look for, and its guidance says nothing about one.

**In the GUI.** Under each problem and part not checked, the verdict shows **Found** (the value), **Takes** (the
expectation's summary, with `(required)`), **Fix:** (the advice), and **What the schema says here**, which folds out the
expectation: title, description, what it takes, whether it is required, patterns, allowed values, the records it points
to, each item, the properties, the schema's examples and **OSDU's example**. Under the verdict, a line says which
release and file OSDU's examples are from, with a link, or why none is quoted. A field of the record with problems
carries a mark whose tooltip lists them with their advice. **Validate these records** gives each rule broken most often
the same expectation summary (`expected`) and advice (`advice`), worked out on its example record.

## Referenced by

**Referenced by**, in the place line when one type or one of its kinds is picked, lists the types whose schemas name
records of that type: the users of a code list before it changes, or the kinds a master record is named from.

- **What counts**: a property marked `x-osdu-relationship` naming the type, wherever it sits (in `data`, in `meta`,
  inside a list, inside an abstract schema the kind is made of, in one form of a choice). Where a schema marks no
  relationship, an id pattern naming the type counts too, marked **by pattern**.
- **The answer**: the types by group, each version the partition holds as a chip (solid where its schema names the type,
  dashed where it does not), and under it each property path. A type opens its records. **Only types with records**
  keeps the versions the partition holds records of. Properties that name any record of the type's group are listed
  apart.

The Schema service cannot be asked which schemas refer to a type, so the control plane reads the partition's schemas once
and keeps what each names: one reading per Schema service and partition (16 partitions at most), shared by every reader.
A pass lists every schema a hundred at a time, then reads each kind's schema eight at a time; an ask waits a few seconds
and then answers with where the pass stands, and the dialog asks again until it is done. A later pass reads only the
kinds added since and those in development, since a published or obsolete schema never changes. A pass runs when nothing
has been read, when the reader asks (**Read the partition's schemas again**), or when the reading is older than twelve
hours; a pass that fails keeps the last reading and runs again when asked, or after two minutes. A pass that has failed
to read 50 kinds, more than it read, stops, since the service is failing rather than one schema.

## Building a dimension

**Build a dimension**, in the header, docks the dimension builder beside the records (the workbench's side bar folds
meanwhile). The key, the value and the attributes of a [dimension](../flow/dimension.md) are picked on the values
themselves as records are browsed and their links followed: beside each value a menu offers **Make it the key**,
**Collect it**, **Read it as the value** or **Read it as an attribute**, depending on where the record sits on the trail.
The builder suggests keys from the kind's template a synced repository holds (the values that name other records), fills an example row with
what one key reads, and writes the `dimensions:` item a dimension flow lists, checked by the loader a flow is read by.
**Copy the YAML** takes it. The builder saves nothing: no flow, no dimension, nothing in OSDU or the database. The draft
lives in the page's address (`dim`), so a link or a refresh brings it back; closing the builder drops it. See
[building and using a dimension](../guides/dimensions.md).

## Kept in the address

The text (`q`) and whether it is Lucene (`lq`), the place (`kind`), the conditions (`f`), the order (`sort`), the type
list alone (`view=types`), the record open (`id`, and `v` for a version) and a dimension being built (`dim`) are the
page's address, so a link, Back and a refresh land on the same view. A link naming a partition (`partition`) makes it the
title bar's.

## The API

The bodies and answers are listed on the [API page](api.md). The routes that read OSDU take `?partition=`; without it
the workbench's partition, else the registry's default, is read.

| Route | Group | What it does |
| --- | --- | --- |
| `GET /api/v1/delivery/explorer/connection` | read | How the partition is reached: its `partition` as the registry spells it, `available`, the `endpoint` it reaches, or the `reason` it cannot be. |
| `POST /api/v1/delivery/explorer/types` | operate | The records a search finds, counted kind by kind. |
| `POST /api/v1/delivery/explorer/search` | operate | One page of records: `text`, `lucene` or `mentions`, `kind`, `filters` (at most 12; a filter may name a search `term`), `sort`, `offset` and `limit` (1 to 200, inside the first 10,000), `facet`, `columns` (at most 8). |
| `POST /api/v1/delivery/explorer/fields` | operate | The properties the records of a `kind` hold. |
| `POST /api/v1/delivery/explorer/read` | operate | One record from storage by `targetId`, at its latest or at `version`, with its version list. |
| `POST /api/v1/delivery/explorer/validate` | operate | One record checked against its kind's schema (`schema`: `osdu` or `template`, with `templateVersion` among those the repositories hold), and against the assertions of the synced mapping `mapping` names. |
| `POST /api/v1/delivery/explorer/validate-list` | operate | The records a `search` finds checked (`max` 1 to 1,000), with `schema` and `mapping` as above. |
| `POST /api/v1/delivery/flows/{pipelineId}/osdu/validate` | operate | The same check of one record through a flow's own route, judging the flow's own mapping's assertions unless `mapping` names another; 409 for a `dspdm` or `etp` flow. |
| `POST /api/v1/delivery/explorer/referenced-by` | operate | The types whose schemas name records of a `type`; `refresh` reads the schemas again. |
| `POST /api/v1/delivery/explorer/element-queries` | operate | The query that finds an element of a record, from the templates the synced repositories hold alone. |
| `GET /api/v1/delivery/explorer/dimension/candidates?kind=` | operate | The keys a kind's template suggests for a dimension. |
| `POST /api/v1/delivery/explorer/dimension/keys` | operate | The commonest keys of a drafted dimension's path. |
| `POST /api/v1/delivery/explorer/dimension/compose` | operate | A draft written as a dimension flow's item, and checked. |

A request the read would refuse (a kind that is not one, more than 12 conditions, a page past the window, an id that is
not one) answers 400 before anything is read, with the reason, for example `The search service pages through the first
10,000 records a query matches; narrow the search to reach the others.` A partition the explorer has no connection to
(not registered, or a value of its connection unset) answers 409, **No connection to the partition**. A read that
cannot be completed as asked answers 422 with why. When OSDU refuses or cannot be reached the answer is 502, and with no
answer within the timeout 504, each with the reason redacted.
