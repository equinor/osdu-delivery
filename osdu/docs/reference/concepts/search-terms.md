---
id: delivery-concept-search-terms
title: "Search terms: searching OSDU by the columns of your source tables"
type: concept
summary: "How the source columns delivery flows read become search terms the explorer searches OSDU by, and how to rename, route, delete or restore one."
keywords:
  - search terms
  - source column
  - search by source column
  - search term route
  - rename a search term
  - delete a search term
  - restore a search term
  - not searchable
  - lookup route
  - key route
  - search terms page
  - osdu.SearchTerm
related:
  - delivery-concept-explorer
  - delivery-concept-gui
  - delivery-flow-mapping-values
  - delivery-flow-mapping-lookups
  - delivery-flow-mapping-modifiers
  - delivery-concept-templates
  - delivery-flow-delivery
sourceRefs:
  - osdu/src/SqlFlow.Delivery/SearchTerms/SearchTerm.cs
  - osdu/src/SqlFlow.Delivery/SearchTerms/SearchTermCompiler.cs
  - osdu/src/SqlFlow.Delivery/SearchTerms/SearchTermDirectory.cs
  - osdu/src/SqlFlow.Delivery/SearchTerms/SearchTermResolver.cs
  - osdu/src/SqlFlow.Delivery/SearchTerms/SearchTermValues.cs
  - osdu/src/SqlFlow.Delivery/Catalog/DeliverySearchTermCatalog.cs
  - osdu/src/SqlFlow.Delivery/Catalog/DeliveryCatalogSync.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Background/SearchTermCatalogRefreshService.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliverySearchTermEndpoints.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryExplorerEndpoints.Terms.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/DeliveryControlPlaneModule.cs
  - osdu/src/SqlFlow.Delivery/Engine/Search/RecordExplorer.Via.cs
  - osdu/src/SqlFlow.Delivery/Engine/Search/ExplorerVia.cs
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/gui/src/features/delivery/searchTerms/SearchTermsPage.tsx
  - osdu/gui/src/features/delivery/explorer/explorerTerms.ts
  - osdu/gui/src/features/delivery/explorer/ExplorerFilterEditor.tsx
  - osdu/gui/src/features/delivery/explorer/explorerHeld.ts
---

# Search terms

People who look for delivered data in OSDU usually know it by the columns of the system it came from: a well log by
its `wellbore_name`, its `log_run`, its curves' `curve_unit`. The OSDU record holds other names and often other values:
the log names its wellbore by a record id in `data.WellboreID`, its curves' units as references to `UnitOfMeasure`
records. The delivery flows already say exactly how one becomes the other (the tables each reads and the mapping it
renders them with), so OSDU Delivery reads them backwards. A **search term** is a column of a source table that an
active delivery flow reads, named as the source names it (`WellLog.wellbore_name`), with every route by which a value of
it reaches the records the flow delivers.

The [explorer](explorer.md) offers the terms beside a type's own properties, as **Source columns**. A value typed for a
term is the source's own; the control plane carries it to what the record holds the way a delivery would, and asks
OSDU's search service, as it asks any condition. Nothing is searched in the ledger or the cache: the mappings and the
cache flows' declarations (metadata, never a cached value) only write the query, and what it finds is what OSDU holds.

The **Search terms** page (`/delivery/search-terms`, under Setup in the OSDU menu) lists the terms of one entity type
and refines them: renamed to what the people searching call them, searched through another of their routes, given a
note, or deleted from the explorer and restored.

## Where the terms come from

The terms are extracted from the pipelines. Every interface of every active delivery flow reads a record table
(`source.record.object`) and the tables of its child datasets (`source.datasets.<name>.object`), and renders them with
the mapping its `render.mapping` pins. Every column of those tables the mapping reads into the record's `data` or `tags`
is a term:

- a `$from` column, written as it stands or through `$modifiers`;
- each column an `$expr` reads;
- each column a `$cache` node, a `$lookup` of one of the mapping's named `lookups`, or a `$search` node compares in its
  `$findBy`;
- each column of the dataset's `key`, from which the record's id is made.

A `$findAll` node gives no route, and neither do `acl` and `legal`: they say who may read a record, not what it holds.

The terms are written in two places, and nowhere else:

- **Every repository sync**, in the transaction that writes the repository's mappings, cache declarations and
  interfaces, from the flows it parsed, the valid mappings their interfaces pin and the cached types its cache flows
  declare.
- **Once when the control plane starts**, from the catalog's copies of the delivery flows, so a module upgraded since a
  repository's last sync describes its terms as the new version does, and a repository with no delivery flow any longer
  loses its terms. A pass that fails (the module database not migrated yet, a lost connection) is tried again every 30
  seconds until one completes.

A term is identified by the table the column is read from (its name with brackets and quotes taken off) and the column,
in lower case: `osdudata.silver.welllog/wellbore_name`. Its id is a UUIDv5 of that text, so a term is the same whichever
flow reads the table, whichever mapping (and mapping version) renders it, whichever repository holds them, and on every
host. Two flows reading one table give one term, its routes read by both mappings. Its name, until a person gives it
another, is the table's own name and the column: `WellLog.wellbore_name`, `WellLogCurve.curve_unit`.

A term is kept per entity type its routes fill (a table rendered into two types is one term with routes into each), and
lists the active delivery flows that read it. A term no pipeline gives any longer is removed at the next sync; what a
person made of it is kept apart and stays (see [Refining a term](#refining-a-term)).

The sync leaves a term out, with a warning among the sync's warnings (in the control plane's log for the pass at start),
when:

- the mapping no longer parses (`its search terms are left out, since the mapping no longer reads`);
- the mapping reads a dataset the flow does not declare (`reads the dataset curves, which the flow reading ... does not
  declare; its columns give no search term.`);
- a cached type's declared fields cannot be read (`is not read for search terms`);
- its key is longer than 600 characters, its table name longer than 400, its entity type longer than 200, or its column
  longer than 128.

### An example

A well log delivery flow reads `OsduData.silver.WellLog` as its record table and `OsduData.silver.WellLogCurve` as its
`curves` dataset (see [delivery flow](../flow/delivery.md) for the `source` keys), and renders them with this mapping,
`WellLog@1.3.0`, a version of the well log mapping written to give a route of each kind: it finds the wellbore in the
cache by name, where the well database's own `WellLog@1.0.0` refers to it with `ref`
([one source, several kinds](../guides/multi-kind-source.md#3-point-the-well-log-at-its-wellbore)):

```yaml
documentType: mapping
name: WellLog
version: 1.3.0
template:
  kind: osdu:wks:work-product-component--WellLog:1.4.0
  version: 26a3c3441882db4f
description: Well logs from the well database, with their wellbore read from the cache.

dataset:
  system: welldb
  key: [log_id]
  label: "{wellbore_name} / {log_id}"

parameters:
  dataPartition: { required: true }
  aclOwner: { required: true }
  aclViewer: { required: true }
  legalTag: { required: true }

record:
  acl:
    owners: ["{$param.aclOwner}"]
    viewers: ["{$param.aclViewer}"]
  legal:
    legaltags: ["{$param.legalTag}"]
    otherRelevantDataCountries: [US]
  data:
    LogRun: { $from: log_run }             # copy
    Name:
      $from: log_name
      $modifiers: [trim]                   # steps
    WellboreID:
      $cache: Wellbore.id                  # lookup
      $findBy: FacilityName = wellbore_name
    Description:
      $expr: log_source & " run " & log_run  # expression
    Curves:
      $forEach: curves
      $item:
        Mnemonic: { $from: curve_id }      # copy, from the curves dataset
        CurveUnit:
          $from: curve_unit
          $modifiers:
            - replace: $cache.UnitAlias    # steps through a cached table
            - ref
          $required: false
```

It gives these terms of `work-product-component--WellLog`:

| Term | Route | Searched in | Searchable |
| --- | --- | --- | --- |
| `WellLog.log_run` | copy, and expression | `data.LogRun` (the copy is preferred) | yes |
| `WellLog.log_name` | steps (`trim`) | `data.Name` | yes |
| `WellLog.wellbore_name` | lookup of `Wellbore` by `FacilityName` | `data.WellboreID` | when a cache flow declares `Wellbore` from OSDU and captures `FacilityName` |
| `WellLog.log_source` | expression | `data.Description` | no: `its value is computed by the expression ..., which a search cannot run backwards` |
| `WellLog.log_id` | key | the record's `id` | yes |
| `WellLogCurve.curve_id` | copy | `data.Curves.Mnemonic` | yes |
| `WellLogCurve.curve_unit` | steps | `data.Curves.CurveUnit` | no: `it is translated through the cached table UnitAlias, which a search does not read` |

## Routes

A route is one way a column reaches the record: the template variable it fills (`osdu.data.WellboreID`), its path in the
record (`data.WellboreID`), the mapping node that fills it and where the mapping writes it, the modifiers on the way, the
node's `$when` and `$description`, and how a value becomes the value written:

| Route | The mapping | A search by the term |
| --- | --- | --- |
| **copy** | writes the value as it stands (`LogRun: { $from: log_run }`) | compares the property with the value typed |
| **steps** | puts the value through `$modifiers` first | puts the value typed through the same modifiers, by the renderer's own code, then compares |
| **lookup** | finds a record of a cached type by the value (`$cache` with `$findBy`, or `$lookup`) and writes its id or one of its fields | finds those records in OSDU itself, by the kind the cache flow captures, then compares the property with what they hold |
| **search** | finds a record by searching the platform (`$search`) and writes its id | runs the mapping's own search in OSDU, then compares the property with the ids found |
| **key** | makes the record's id from the dataset's key, of which the column is a part | makes the id from the value typed, as a delivery makes it, and compares the record's id |
| **expression** | computes the value with `$expr` | none: an expression cannot be run backwards |

A value is carried through a route by the render's own code: the same modifiers, the same rule for an empty value and the
same conversion to the type the template gives the variable, with the partition the explorer reads as the mapping's
`dataPartition`. The node's `$when` is not asked: a search looks for the value wherever a record holds it. A value the
mapping cannot carry (a word where the template takes a number, an empty value) is refused with why before anything is
read.

**What steps keep of a value** decides what a search may ask of it. Modifiers that only `trim`, change case (`upper`,
`lower`) or `split` keep its **text** (its words and its start). Modifiers that only `trim` or read it as a `number` or a
`date` keep its **order**, so a range of values is a range of what is written. Anything else makes another value of it,
which is compared whole.

**A lookup** finds its records where the cached type comes from: the cache flow that declares the type names the kind it
captures and the fields it keeps by their paths in the record, and the lookup's `$findBy` names those fields. A type
whose rows are not OSDU records (a table or a dictionary) gives no searchable route. **A search** route runs the
mapping's own `searches:` entry, each line of its `$findBy` a clause, read by the schema the search pins. Of the records
found, the first 50 are compared (the ids in the reference form a relationship holds, `dev:master-data--Wellbore:abc:`,
or the field the route reads of them); a value that finds more says so in the answer's notes, and to narrow it.

**A key** route makes the record's id as a delivery makes it: from the delivery key derived over the mapping's source
system and the key's values, or from the values themselves for a mapping whose `dataset.idFrom` is `key`.
A key of several columns needs the others' values too: each is read from the property of the record its copy fills, by
one grouping search per column, and an id is made for every combination; the first 50 are compared. A key column whose
partner the record does not hold as it stands is not searchable.

### Why a route is not searchable

A route says why no value can be carried through it, and the reason is shown on the term. The reasons, as the code
words them:

| Reason | Cause |
| --- | --- |
| `it is translated through the cached table <Type>, which a search does not read` | a `replace` modifier reads a cached table |
| `its id is built from the cached table <Type>, which a search does not read` | an `id` modifier reads a cached table |
| `its id is built from the flow's parameter <name>, which a search is not given` | an `id` modifier reads a parameter other than the partition |
| `its id is built from <column> as well` | an `id` modifier reads another column |
| `its value is computed by the expression <text>, which a search cannot run backwards` | an `$expr` |
| `the cached type <Type> is declared by no cache flow of the repository` | a lookup of a type nothing captures |
| `the cached type <Type> holds rows of a table, not records OSDU holds, so a search of the platform cannot find them` | a lookup of a table or dictionary type |
| `the cached type <Type> does not capture <field>` | a `$findBy` field, or the field read, is not captured |
| `the mapping declares no search <Name>` | a `$search` naming no `searches:` entry |

Whether a route can be compared is then decided against the [templates](templates.md) the mapping reads, as the
template folders on its path held them at the repository's last sync, each time the terms are read: the property the
route writes is compared as the template the mapping fills says the platform indexes it (text, a keyword, a number, a
boolean or a date, inside a nested list through it), and for a lookup or a search, the properties the records are found
by as the schema of those records says (the search's pinned schema, or the newest template the mapping reads of the
cached type's kind). A template the mapping does not read makes the route not searchable:

- `the template of <Name@version> cannot be read: ...`
- `no template the mapping reads of <kind> says how its records are indexed; put one in a template folder on the mapping's path to search through <Type>`

A template's file put in a folder on the mapping's path makes the routes it describes searchable once the repository is
synced again.

### The conditions a route takes

| Route | Conditions, the likeliest first |
| --- | --- |
| copy, or steps keeping text, into text | contains, is, is not, is one of, is none of, starts with, has a value, has no value |
| copy, or steps keeping text, into a keyword | is, is not, is one of, is none of, starts with, has a value, has no value |
| copy, or steps keeping order, into a number or a date | is in a range, is, is not, is one of, is none of, has a value, has no value |
| copy, or steps, into a boolean | is, has a value, has no value |
| lookup, search | is, contains (when every property found by is text), starts with (when every one is text or a keyword outside a nested list), is not, is one of, is none of, has a value, has no value |
| key | is, is not, is one of, is none of |
| any other | is, is not, is one of, is none of, has a value, has no value |

Inside a nested list, **starts with**, **has a value** and **has no value** are not offered, as for any property in the
explorer.

### The route a term is searched by

A term reaches the record by as many routes as its mappings write it. It is searched through the route a person picked
on the Search terms page while that route can be searched, else the plainest one that can: a copy before steps, before a
lookup or a search, before a key; the record's `data` before its `tags`; a value of its own before one inside a list;
then the shorter path. A term none of whose routes can be searched is listed as **Not searchable** with why, and not
offered in the explorer.

Each route lists the kinds it fills, one per mapping version that writes it. With one kind in view in the explorer
(`osdu:wks:work-product-component--WellLog:1.4.0`), the routes that fill it are preferred, and values are carried
through the mapping version that renders that kind; so a column two versions write into different properties is
searched where the version in view writes it. A key's ids are made as every source system that reads it makes them.

## Searching by a term

In the explorer, with a type or a kind picked, the condition editor lists the type's terms first under **Source
columns**, by name, each with the property it is searched in; the record's properties follow. A place of several types
(every type, a group) offers none. A term picked takes the conditions its route allows, and says its value is typed as
the source column holds it. The values listed under it are those of the property the route writes (a copy, or steps
keeping text), or those of the property its records are found by (`Values of Wellbore FacilityName` for a lookup by
`FacilityName`), or none (a key, or steps that make another value).

A condition on a term is a chip naming the term, marked as a source column, and is part of the explorer's address
(`st` in the condition), so a link brings it back. The search field can search one source column, picked at its start
(**In** a column), and offers the source columns searched in lately for the type as text is typed. A term deleted or no
longer extracted is marked on its chip, and a search naming it is refused with why.

The control plane resolves each condition naming a term before the search is checked or run, in the partition the
search reads: the term, its route for the kind searched, the mapping that renders that kind and the template it pins,
and each value carried through the mapping. A copy or steps become a condition on the property, as any is. A lookup, a
search or a key is read through other records first: the explorer searches OSDU for the records the value finds (or
makes the ids the key gives), then compares the property with what it found, and says so in the answer's notes, for
example `WellLog.wellbore_name is Wellbore A-42: 1 Wellbore record found by ..., compared by id with data.WellboreID.` A value
that finds nothing finds no record, or, for a condition that excludes, leaves every record.

The refusals a search can meet, as the control plane words them:

- `No search term <id> is extracted from the pipelines of an active delivery flow; sync the repository, or pick the property itself.`
- `<Name> is deleted from the search terms; restore it on the Search terms page.`
- `<Name> cannot be searched: <why>.`
- `<column> reaches the records of <type> and <type>: pick one of those types to search it.`
- `<column> is a column of <type> records, not of <type>'s.`
- `<Name> is searched through <path> (<how>), which takes <conditions>, not <condition>.`
- `<Name> '<value>' cannot be searched for: <why>.`

## The Search terms page

The page lists the terms of one entity type, picked at its head (each type with how many terms it has), found by any
part of a name, a column or a path, and shown by state, each with its count:

| State | What it lists |
| --- | --- |
| **Terms** | every term not deleted and still extracted (the page opens here) |
| **Searched** | terms the explorer offers |
| **Not searchable** | terms none of whose routes can be searched (why on hover) |
| **Deleted** | terms taken out of the explorer, kept until restored |
| **No longer found** | what a person made of a term no pipeline gives any longer |

A row shows the **Name** (a note on hover), the **Source column** in full (the table's whole name and the column, cut
from the start when long so the column stays in view), **Searched as** (the property, with `steps`, `lookup`, `search` or
`expression` beside it), the **State** and when it was **Changed** (`as extracted` when nobody has). The grid fits the
page and scrolls in place; the arrow keys move through it and Enter opens a row. **Search** with the type's name opens
the explorer on that type.

A row opens the term in a panel:

- **Name**: the name the explorer offers it by, at most 100 characters on one line, unique among the terms of its entity
  type in any case. Left empty (or set to the column's own label), the term keeps `Table.column`.
- **Note**: what the term means to the people searching by it, at most 1,000 characters, shown on hover wherever the
  term is offered.
- **Offered in the explorer**: off, the term is deleted (see below).
- **Searched as**: every route, with how it reaches the record, its steps, the records a lookup or a search finds and by
  what, the versions of the type that write it so, and why one cannot be searched. Picking one makes it the term's
  route; a route that cannot be searched cannot be picked.
- **Read by**: the mappings and the flows that read the column, and who changed the term last.

**Save** keeps the changes; **Reset** removes everything people made of the term, which is then searched as its
pipelines give it. For a term **No longer found**, the button is **Remove**.

### Refining a term

What a person makes of a term is kept apart from the terms a sync writes, by the term's id, so it holds across every
sync, every pipeline and every mapping version that keeps the column, and applies again if a term that was gone comes
back. A refinement that keeps nothing (no name, no note, not deleted, no route picked) is removed rather than kept.
Every change records who made it and when.

Refusals of a refinement:

- `The name '<name>' is the search term <column>'s already; two terms of <type> cannot share one.`
- `'<route>' is not a route of the search term <column> on <type>: <routes>.`
- `The search term <column> cannot be searched through <variable>: <why>.`
- `A term's name is at most 100 characters; this one is <n>.` (and the same for a note, at most 1,000)
- `A term's name holds a character that cannot be printed.`
- `The search term <key> is no longer extracted from any pipeline, so only its refinement can be removed.`

Terms were once keyed by their mapping's source system, so one table read by two flows under two systems gave two terms.
Each sync moves what was made of such a term to the term its column is now; of two made for what is now one term, the
newer moves, and the other is listed under **No longer found** for a person to remove.

### Deleting and restoring terms

A term a pipeline reads cannot be erased: the next sync extracts it again. Deleting it takes it out of the search
instead: the explorer no longer offers it, a condition on it that a link still carries is refused, and it is listed
under **Deleted** with its name, note and route kept, through every sync, until it is restored. A term **No longer
found** is only what was made of it, and deleting it removes that for good.

Every row has a box: a click picks it, a shift-click picks every row from the one picked last, Space picks the row the
arrow keys are on, and the box in the header picks every row listed. While any are picked, the state tabs give way to
how many are picked and **Delete N** (confirmed first, saying what happens to each kind), **Restore N** for the deleted
ones, and **Clear**. Only the rows listed are acted on, never one the find hides, and picking another type or state lets
the picked rows go. One request deletes or restores every term picked, in one save, at most 5,000 at once; an id that is
no term any longer is reported, not refused. The **Offered in the explorer** switch deletes and restores one term the
same way.

## Tables

Both tables are in the module's `osdu` schema (see [the ledger](ledger.md) for the rest of the model).

| Table | What it holds |
| --- | --- |
| [`osdu.SearchTerm`](../tables/search-term.md) | One row per repository, term and entity type: the term's identity and key, the entity type its routes fill, its routes, and the active delivery flows that read its table. Written only by the sync and the control plane's start. |
| [`osdu.SearchTermRefinement`](../tables/search-term-refinement.md) | One row per term a person refined, keyed by `TermId`: its name, note, whether it is deleted, and its route. A sync changes none of it, except to move a refinement made while terms were keyed by their mapping's source system onto the term that column is now. |

The tables came with the migration `20261008170839_SearchTerms`; `20261008204608_SearchTermSources` keyed the terms by
their table, emptying the read model for the control plane to write again and keeping every refinement.

## The API

| Route | Group | What it does |
| --- | --- | --- |
| `GET /api/v1/delivery/search-terms?entityType=&kind=&orphans=` | read | The terms of an entity type, of the one a kind or kind pattern names (a pattern of many types has none), or of every type; named by a kind, each is searched through its route for that kind. `orphans=true` adds the refinements no pipeline gives any longer. |
| `GET /api/v1/delivery/search-terms/entity-types` | read | The entity types terms are extracted for, each with how many terms it has. |
| `GET /api/v1/delivery/search-terms/{termId}?entityType=` | read | One term as it reaches the entity type (the first it reaches when none is named), or its refinement while no pipeline gives it; 404 for neither. |
| `PUT /api/v1/delivery/search-terms/{termId}?entityType=` | author | Refines a term. Body: `name`, `excluded`, `route` (a route's `id`), `note`. A body that keeps nothing removes the refinement; a refusal answers 400 with the reason. |
| `DELETE /api/v1/delivery/search-terms/{termId}/refinement` | author | Removes what people made of the term: 204, or 404 when there was nothing. |
| `POST /api/v1/delivery/search-terms/delete` | author | Deletes terms in one save. Body: `terms` (ids, at most 5,000) and `entityType`. Answers `deleted`, `removed` and `missing`. |
| `POST /api/v1/delivery/search-terms/restore` | author | Offers deleted terms again in one save. Body: `terms` (ids, at most 5,000). Answers `restored` and `missing`. |

A condition of the explorer's `filters` names a term by `term` (its id) in place of a property; its `condition`, `value`,
`values` and `to` are the source's own (see [the explorer](explorer.md#the-api)). The groups are SQLFlow's
authorization policies: SQLFlow resolves `read`, `operate` and `author` to any signed-in user, so anyone signed in can
read and refine the terms ([authentication and identity](authentication-and-identity.md)). The MCP server offers none of
these routes. The [API reference](api.md) lists every `/api/v1/delivery` endpoint.
