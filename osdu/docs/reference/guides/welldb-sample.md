---
id: delivery-guide-welldb-sample
title: "The welldb sample: a complete package that fills an empty partition, from reference data to well logs"
type: guide
summary: "What the welldb sample package delivers, its drop-off zone, templates and cache flows, its schedules, what it reads back from OSDU, and how to try it."
keywords:
  - welldb sample
  - sample package
  - example estate
  - try it
  - demo
  - sample flows
  - sample schedules
  - fill an empty partition
related:
  - delivery-concept-overview
  - delivery-guide-run-locally
  - delivery-guide-getting-started
  - delivery-concept-lineage
  - delivery-guide-reference-data-cache
  - delivery-guide-lookup-table-cache
  - delivery-guide-bulk-data
  - delivery-cli-check
  - delivery-cli-preview
  - cli-lineage
sourceRefs:
  - osdu/samples/welldb/schedules.yaml
  - osdu/samples/welldb/reference/welldb-ref-03-delivery.yaml
  - osdu/samples/welldb/flows/welldb-well-03-delivery.yaml
  - osdu/samples/welldb/flows/welldb-wellbore-03-delivery.yaml
  - osdu/samples/welldb/flows/welldb-welllog-03-header-delivery.yaml
  - osdu/samples/welldb/flows/welldb-survey-03-delivery.yaml
  - osdu/samples/welldb/flows/welldb-well-04-assertion.yaml
  - osdu/samples/welldb/flows/welldb-welllog-04-header-assertion.yaml
  - osdu/samples/welldb/flows/welldb-welllog-05-dimensions.yaml
  - osdu/samples/welldb/flows/welldb-wellbore-04-retrieval.yaml
  - osdu/samples/welldb/flows/welldb-wellbore-05-removal.yaml
  - osdu/samples/welldb/flows/welldb-osdu-06-inventory.yaml
  - osdu/samples/welldb/cache/osdu-reference-00-cache.yaml
  - osdu/samples/welldb/cache/welldb-lookups-00-cache.yaml
  - osdu/samples/welldb/dictionaries/curve-aliases.yaml
  - osdu/tests/SqlFlow.Delivery.Tests/SampleDataGeneratorTests.cs
---

# The welldb sample: a complete package that fills an empty partition, from reference data to well logs

The sample in [osdu/samples/welldb](../../../samples/welldb) delivers a small well database (`welldb`) into an empty
OSDU partition. It holds everything the delivery needs: the source files and the flows that load them, the OSDU schemas
its mappings fill, the lookup tables and reference data its mappings read, and the schedules that run it all. Every
reference a record makes names a record the package delivers, and the suites check that it does. It shows each of
[the three places and the mapping](../../../../README.md) at work, and every flow kind OSDU Delivery has.

## What it delivers

| What | OSDU kind | Delivery flow | Where it goes |
| --- | --- | --- | --- |
| The reference values the records refer to: units and unit quantities, log curve types, families and business values, log types, sampling domain types, technical assurance types, vertical measurement types and trajectory station property types | `reference-data--*`, one mapping each | `welldb-ref-03-delivery` | The storage service |
| Three wells | `master-data--Well` | `welldb-well-03-delivery` | The storage service |
| Five wellbores | `master-data--Wellbore` | `welldb-wellbore-03-delivery` | The storage service |
| For each wellbore, a synthetic well log with its curve data | `work-product-component--WellLog` | `welldb-welllog-03-header-delivery` | The Wellbore DDMS, the curves as bulk data ([bulk data](bulk-data.md)) |
| For each wellbore, a directional survey with its stations, and a set of formation tops | `work-product-component--WellboreTrajectory`, `work-product-component--WellboreMarkerSet` | `welldb-survey-03-delivery` | The survey to the Wellbore DDMS, its stations as bulk data; the tops through the storage service |

Lineage runs them in the order the records refer to each other. The reference data goes first, and puts what it
delivers into the partition's cache as it runs. The wells follow, then the wellbores that name them, then the logs and
the surveys with the tops. The wells, the logs and the surveys bring the cache types they read up to date before they
render. `sqlflow lineage welldb`, from `osdu/samples`, prints the order ([lineage](../concepts/lineage.md)).

## The three parts

| Part | Where | What it holds |
| --- | --- | --- |
| Drop-off zone | `data/` and `reference/data/`, with the `-01-pre` and `-02-ing` flows beside each delivery | The sample files, and the flows that land them and load them into tables keyed by record |
| Templates | `templates/` | The OSDU schema each mapping fills, one file per kind and version |
| Cache | `cache/` and `dictionaries/` | `osdu-reference-00-cache` captures the partition's reference data from OSDU ([reference data cache](reference-data-cache.md)); `welldb-lookups-00-cache` captures the lookup tables its own flows load: unit spellings, depth unit spellings and the curve dictionary ([lookup table cache](lookup-table-cache.md)); `curve-aliases.yaml` groups curve mnemonics for the dimensions |

The mappings are in `mappings/`, one file per kind, named `<Name>@<version>.yaml`.

## What it reads back from OSDU

Once the records land, the package reads OSDU back:

- **Tests.** `welldb-well-04-assertion` checks the wells against their mapping's rules and the values the well database
  gives, and `welldb-welllog-04-header-assertion` checks the logs as delivered
  ([data quality tests](data-quality-tests.md)).
- **Dimensions.** `welldb-welllog-05-dimensions` keeps the distinct logs, wellbores and curve mnemonics delivered, the
  mnemonics grouped through `curve-aliases.yaml`, so a search panel can offer them as choices
  ([dimensions](dimensions.md)).
- **Inventory.** `welldb-osdu-06-inventory` sets what OSDU holds of the kinds the package delivers against every ledger
  of the partition. It only reads; an operator removes what it finds ([finding orphans](finding-orphans.md)).
- **Removal.** `welldb-wellbore-05-removal` takes out of OSDU the wellbores the well database no longer holds, and waits
  for an operator's approval when a run finds too many gone ([the removal flow](../flow/removal.md)).
- **Retrieval.** `welldb-wellbore-04-retrieval` reads the wellbores back from OSDU into a lake as JSON Lines files. Its
  target is the well database's own lake, so it joins no schedule and runs on demand until its lake is yours
  ([retrieving records](retrieving-records.md)).

## Its schedules

Every flow but the retrieval joins one of the schedules in `schedules.yaml`, and a schedule fires its flows as one group
in lineage order:

| Schedule | When | What it runs |
| --- | --- | --- |
| `welldb-cache` | Nightly | The lookup tables, and a full refresh of the partition's reference data |
| `welldb-reference` | Nightly | The reference values |
| `welldb-master` | After `welldb-reference` | The wells, the wellbores, the wells' tests, then the surveys and tops |
| `welldb-removal` | After `welldb-master` | The wellbores the well database no longer holds |
| `welldb-welllog` | Hourly | The well logs, with the log source `COMPOSITE` |
| `welldb-dimensions` | After `welldb-welllog` | The dimensions over the logs |
| `welldb-inventory` | Weekly | The inventory of what OSDU holds |

## Try it

You need a SQL Server and an OSDU partition you can write to. [Run it locally](run-locally.md) sets a machine up with
both, and [your first delivery](getting-started.md) does the same by hand. Register `osdu/samples` as the flow
repository (a repository holds one top-level folder per source, here `welldb`), and the sample's schedules then deliver
the reference data nightly, with the wells and wellbores chained after it, and refresh the caches on a schedule of
their own; "Run now" on the Schedules page runs a chain at once.

Once the wellbores are in OSDU, delivering the logs from the command line takes four commands:

```bash
cd osdu/samples
sqlflow check   welldb/flows/welldb-welllog-03-header-delivery.yaml --set logSource=COMPOSITE
sqlflow preview welldb/flows/welldb-welllog-03-header-delivery.yaml --set logSource=COMPOSITE
sqlflow run     welldb/flows/welldb-welllog-03-header-delivery.yaml --set logSource=COMPOSITE --operation plan
sqlflow run     welldb/flows/welldb-welllog-03-header-delivery.yaml --set logSource=COMPOSITE
```

[`check`](../cli/check.md) validates the flow, its mapping and its schema; [`preview`](../cli/preview.md) builds one
record without sending it; `plan` shows what a run would send; [`run`](../cli/run.md) delivers.

## See also

- [How OSDU Delivery works](../../../../README.md#how-it-works)
- [Your first delivery, end to end](getting-started.md)
- [Which delivery problem maps to which shape](../../wiki/maps/pattern-catalog.md)
