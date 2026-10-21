---
id: delivery-concept-osdu-order
title: "The OSDU order: wells before wellbores before logs, from OSDU's schemas, in waves and across schedules"
type: concept
summary: "In which order records reach OSDU, how lineage orders delivery flows by what each kind refers to, and which schedules must run before another."
keywords:
  - order
  - delivery order
  - referential order
  - wellbore before well log
  - reference data first
  - x-osdu-relationship
  - waves
  - schedule dependencies
  - run before
  - cross schedule
  - circle
  - osdu lineage
  - data definitions release
  - build lineage
related:
  - delivery-concept-lineage
  - delivery-wiki-0016-osdu-order
  - delivery-flow-interfaces
  - delivery-flow-mapping
  - delivery-concept-templates
  - delivery-concept-api
  - delivery-concept-gui
  - concept-lineage-graph-and-plan
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Order/OsduOrder.cs
  - osdu/src/SqlFlow.Delivery.Order/OsduEntity.cs
  - osdu/src/SqlFlow.Delivery.Order/OsduSchemaGraph.cs
  - osdu/src/SqlFlow.Delivery.Order/OrderCheck.cs
  - osdu/src/SqlFlow.Delivery/Ordering/OrderDeliveries.cs
  - osdu/src/SqlFlow.Delivery/Ordering/OrderCatalog.cs
  - osdu/src/SqlFlow.Delivery/Ordering/RepositoryOrder.cs
  - osdu/src/SqlFlow.Delivery/Ordering/ScheduleOrders.cs
  - osdu/src/SqlFlow.Delivery/Ordering/SchemaLineageStore.cs
  - osdu/src/SqlFlow.Delivery/Ordering/SchemaLineageBuilder.cs
  - osdu/src/SqlFlow.Delivery/Documents/OrderLineage.cs
  - osdu/src/SqlFlow.Delivery/Documents/DeliveryLineage.cs
  - osdu/src/SqlFlow.Delivery/Catalog/DeliveryCatalogSync.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryOrderEndpoints.cs
  - osdu/gui/src/features/delivery/order/OrderPage.tsx
---

# The OSDU order: wells before wellbores before logs, from OSDU's schemas, in waves and across schedules

OSDU's storage service does not check references: a well log naming a wellbore that does not exist yet is accepted, and
points at nothing. So records must reach a partition in the order they refer to each other, and a partition is filled
from nothing in that order: reference data, then wells, then wellbores, then the logs and trajectories that refer to them.
The OSDU order is that order, read from OSDU's own schemas; lineage orders each repository's delivery flows by it, and
the Order page shows where schedules fire against it.

## What the order is

Every property of an OSDU schema that holds the id of another record says which entity types it may name
(`x-osdu-relationship`). A delivery whose mapping fills such a property refers to that entity type, however the mapping
fills it: from a column, as a literal, through the cache or by a search. The order of a partition is the order of the
entity types its deliveries write, each after the types it refers to:

```text
level 1: reference-data--UnitQuantity, reference-data--LogCurveType
level 2: reference-data--UnitOfMeasure          waits for UnitQuantity
level 3: master-data--Well                      waits for UnitOfMeasure
level 4: master-data--Wellbore                  waits for Well
level 5: work-product-component--WellLog        waits for Wellbore, LogCurveType, UnitOfMeasure
```

What a delivery refers to is read against the template its mapping pins ([templates](templates.md)), and only for the
properties the mapping fills: a wellbore mapping that leaves `DefinitiveTrajectoryID` empty does not wait for
trajectories. A literal fills every property it holds, so a technical assurance written as a literal list refers to the
types inside it.

- A reference to a type nothing in the partition delivers (OSDU's own reference data, another system's records) adds
  nothing.
- A reference to the record's own type (a parent wellbore) adds nothing: records of one flow are ordered against each
  other by [record waits](../flow/interfaces.md#records-that-wait-for-records).
- A kind whose template no [template folder](templates.md#template-folders) on its mapping's path holds is read from a
  [built release of OSDU's lineage](#osdus-lineage-by-release); so is one whose template's copy the sync kept cannot be read,
  where the catalog orders a repository. When neither knows it, the delivery orders nothing and waits for nothing, and
  lineage warns on its flow.

## Cycles, and how they are cut

Two types can refer to each other: a wellbore names its definitive trajectory, and the trajectory names its wellbore.
The order cuts every cycle by fixed rules, so it is always acyclic and the same whoever computes it, and reports each cut
with its rule:

1. **A reference to a later group gives way.** Groups go reference data, master data, datasets, work product components,
   work products. In the cycle above the wellbores go first, and a wellbore's trajectory id is written when the
   trajectory exists.
2. **Within one group, a reference no search backs gives way to one a search backs.** A search is already an order
   lineage keeps ([searches](../flow/mapping-lookups.md#searches-and-search)).
3. **What is still circular is left unordered**, with a warning naming the types. Their flows are not ordered against
   each other, and a record waits for one it refers to only once its flow has put that record in the ledger.

## Lineage takes the order

A delivery flow's mapping node reads each entity type its records wait for, as every kind of it on the flow's platform
and partition (`*:*:master-data--Well:*`). Lineage binds that read to the kinds the repository's other flows write, so the
flow delivering wells runs in an earlier wave than the one delivering wellbores, even when the wellbore mapping writes
the well's id from a column ([lineage](lineage.md#how-the-nodes-order-flows)). Only references the order keeps are read,
so the reads add no cycle.

The order lineage takes is that of the repository's own deliveries. A schedule runs flows of one repository, in that
repository's waves, so inside a schedule every flow runs after the flows it refers to. Another repository's deliveries
play no part in this repository's waves; they run in that repository's schedules ([schedules](#which-schedules-run-before-which)).

A delivery that checks its records against a cache type, where every cache flow capturing that type also captures what
the delivery writes, no longer reads it: the cache flow runs after the delivery instead of the two being ordered in
neither direction. Reference data that refers to other reference data (a unit to its unit quantity) is then delivered,
in the order of its references, before the cache flow captures it; a source whose `cache.after` puts what each interface
delivers into the cache delivers both in one run, the unit quantities first ([cache](../flow/delivery.md#cache)).

A flow that leaves its partitions to the registry is part of the order of every partition the repository names, so a
flow of a named partition that refers to what it delivers runs after it, and the other way round.

### Checking a repository's waves

Each repository sync checks the waves it stamped against the order of the repository's own deliveries: every flow must
run in a later wave than the flows delivering what its records refer to. Lineage takes the order, so a breach means
lineage could not keep it: a dependency cycle its fallback wave holds, or a flow it gave no wave. A breach is a warning on
the sync; the sync still lands:

```text
The OSDU order of partition 'dev' is not kept: welldb-wellbore-03-delivery delivers master-data--Wellbore, which refers
to master-data--Well (osdu.data.WellID) that welldb-well-03-delivery delivers, but lineage runs welldb-well-03-delivery
in wave 4 and welldb-wellbore-03-delivery in wave 4.
```

## Which schedules run before which

Nothing waits across schedules: each fires on its own clock, or behind the schedules of its repository it names in
`after:` ([schedules](../../../../sqlflow/docs/reference/flow/schedule.md)). The order of each partition with every
repository's deliveries together says which schedules run best before which: schedule B runs best after schedule A when
a flow B runs delivers records referring to records a flow A runs delivers. Two flows one schedule runs are ordered by
its waves and name no other schedule.

For each such pair, the overview says how their fires fall:

| Timing | Means |
| --- | --- |
| Chained | B names A in `after:`, directly or through the schedules it chains behind. |
| Fires first | A's next fire comes before B's. |
| Fires after | B fires next, before A: its flows run before the records they refer to are delivered. |
| Not firing | A is disabled or paused. |
| No next fire | One of them has no next fire to compare: it fires behind other schedules, or has not been planned. |

Schedules that each run best before another of them are named as a circle: whichever fires first runs some flows before
what they refer to is delivered, and moving a flow to another schedule is what breaks it. A delivery a schedule refers to
that no schedule runs is listed beside it. Fixing a timing is the operator's: chain two schedules of one repository with
`after:`, or move a clock, since `after:` cannot name another repository's schedule.

## OSDU's lineage by release

A release of the OSDU data definitions can be built into OSDU's lineage: every record kind the release publishes, with
the properties of its schema that refer to other records. A release is built once and kept in the module database
(`[osdu].[SchemaLineage]`, `[osdu].[SchemaLineageKind]`), and building it again replaces it. A kind a release lists that
cannot be read is kept as a failure with why, and the rest are kept.

The order reads a kind from the newest built release holding it when no template folder on the mapping's path holds the
kind's template. A release built after a repository's mappings were synced, holding a kind one of them fills that no
folder on its path holds, computes that repository's lineage again at its next sync. Such a mapping does not load, and
the sync reports it, so the order the catalog keeps for the repository leaves its deliveries out until the template's
file is in a folder on its path.

## In the GUI and the API

The Order page (OSDU, Build) has three tabs ([the GUI](gui.md)):

- **Schedules** draws every schedule running a delivery, with an arrow from a schedule to the ones that run best after it,
  dashed where the timing does not keep the order, and a warning mark on schedules out of order or in a circle. A click
  slides the schedule's reasons in from the left, over part of the graph: when it fires, each schedule it runs best
  after, the timing and what to do about it, the flows, types and partitions behind it, and the schedules that run best
  after it.
- **Partition** lists a partition's entity types level by level, the deliveries writing each and what it waits for, with
  the cut references and the warnings behind marks.
- **Releases** builds a release of OSDU's lineage and draws it around an entity type, as far as four steps, what it refers
  to, what refers to it, or both. A click slides a type's kinds and references in from the left the same way.

| Route | Policy | Answer |
| --- | --- | --- |
| `GET /api/v1/delivery/order/releases` | read | The built releases, newest built first. |
| `POST /api/v1/delivery/order/releases` | operate | Builds `{ "release": "v0.29.0" }`, or the newest release when it is null. |
| `GET /api/v1/delivery/order/releases/{release}/entities` | read | Every entity type of the release, with its kinds and how many types it refers to and is referred to by. |
| `GET /api/v1/delivery/order/releases/{release}/graph` | read | The part of the release around `entity`, `depth` 1 to 4 steps, `direction` `both`, `out` or `in`, cut at 150 types. |
| `GET /api/v1/delivery/order/partitions` | read | Each partition's order with every repository's deliveries: levels, deliveries, references kept and cut, warnings. |
| `GET /api/v1/delivery/order/schedules` | read | Every schedule running a delivery, the schedules it runs best after with the timing and the reasons, and the circles. |

## Known limits

- **A record can still go out before what it refers to across schedules.** Only the timing of the schedules keeps that
  order; a record naming an id no record of the ledger holds goes out unless `target.verifyReferences: storage` holds it
  ([preflight](preflight.md#references-checked-in-storage)).
- **Partitions are compared as written.** A flow naming `${env:PARTITION}` and one naming `dev` are in different orders.
- **Waves order flows, not OSDU's search index.** A flow can run in the wave after the one delivering what it searches
  for and still not find it, because the index holds a record a moment after it is written ([lineage](lineage.md#known-limits)).

## See also

- [Lineage](lineage.md): the nodes, reads and writes behind the waves.
- [Decision 0016](../../wiki/decisions/0016-osdu-order.md): why the order is read from OSDU's schemas, and why schedules are
  shown rather than chained.
- [SQLFlow: the lineage graph and the execution plan](../../../../sqlflow/docs/reference/concepts/lineage-graph-and-plan.md).
