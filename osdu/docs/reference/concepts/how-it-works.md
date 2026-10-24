---
id: delivery-concept-how-it-works
title: "How OSDU Delivery works: a mapping connects your source data, the OSDU schema and a cache"
type: concept
summary: "Why configuration replaces a custom loader per OSDU type: the drop-off zone, the template, the cache, the mapping that connects them, and what you get."
keywords:
  - how it works
  - configured not coded
  - configuration instead of code
  - custom loader
  - drop-off zone
  - template cache mapping
  - why osdu delivery
  - what you get
  - features
  - load data into osdu
related:
  - delivery-concept-overview
  - delivery-flow-mapping
  - delivery-concept-templates
  - delivery-concept-partition-cache
  - delivery-concept-change-detection
  - delivery-concept-ledger
  - delivery-flow-routes
  - delivery-guide-writing-a-mapping
  - delivery-guide-welldb-sample
  - delivery-guide-mcp
sourceRefs:
  - osdu/samples/welldb/mappings/WellLog@1.4.0.yaml
  - osdu/samples/welldb/flows/welldb-welllog-03-header-delivery.yaml
  - osdu/src/SqlFlow.Delivery/Templates/TemplateFolders.cs
  - osdu/src/SqlFlow.Delivery/Snapshots/CacheDeclaration.cs
  - osdu/src/SqlFlow.Delivery/Documents/MappingMapper.cs
  - osdu/src/SqlFlow.Delivery/Validation/Preflight.cs
  - osdu/src/SqlFlow.Delivery/Planning/ChangeDetector.cs
  - osdu/src/SqlFlow.Delivery/Identity/DeliveryKey.cs
  - osdu/src/SqlFlow.Delivery/Protocols/DeliveryProtocol.cs
  - osdu/src/SqlFlow.Delivery/Engine/DeliveryExecutor.cs
  - osdu/src/SqlFlow.Delivery/Ledger/ILedger.cs
---

# How OSDU Delivery works: a mapping connects your source data, the OSDU schema and a cache

[OSDU](https://osduforum.org/) is the open data platform of the energy industry: one place for wells, logs, seismic,
production data and more, each stored as typed records behind a set of services. Getting data into it is hard. Every
type of data has its own schema, its own codes and its own services, so most teams end up writing and maintaining a
custom loader for each one.

OSDU Delivery replaces those loaders with configuration. It can, because everything a loader would otherwise read, look
up or hard-code lives in three places, each known and versioned, and a mapping connects them. This page explains that
idea and what follows from it; [What OSDU Delivery is](overview.md) places the product among the flows that carry the
data and the ledger that records it.

## Three places and a mapping

```text
     drop-off zone              template                 cache
    your source data         the OSDU schema       lookups from OSDU,
    files and tables         you deliver to       files and databases
            |                       |                       |
            +-----------------------+-----------------------+
                                    |
                                 mapping
                          (connects the three)
                                    |
                     OSDU records, files, bulk data
                                    |
                                    +--> ledger: every record, every attempt, every change
```

| | What it is | Why it makes configuration enough |
| --- | --- | --- |
| **Drop-off zone** | Where your source data arrives: files (CSV, JSON, Parquet) dropped in a folder or storage location, or the tables of a source database. Built-in flows load them into a table keyed by record, noting what changed and which file and row each row came from. | Whatever system the data came from, the mapping reads it in one shape: columns of a row. |
| **Template** | The OSDU schema of the type you deliver, picked from the published OSDU schemas and kept as a file in a template folder beside your mappings, its version the hash of its content ([templates](templates.md)). | The mapping fills a known shape, and is checked against it before anything runs. |
| **Cache** | What mappings look values up in: reference data captured from OSDU, lookup tables loaded from files or databases, and small dictionaries kept beside the mappings. One cache per OSDU data partition, refreshed by its own flows and kept as versions ([the partition cache](partition-cache.md)). | Codes and units are translated, and references checked against what OSDU holds, without a line of lookup code. |

With those three in place, a new type of data needs only a mapping: this column, translated through that lookup, fills
that property of the schema. OSDU Delivery does the rest:

- it **delivers** the records through the right OSDU services, with their files and bulk data (such as log curves);
- it **keeps OSDU in sync** with your source, sending only what changed, including when a mapping, template or cache
  changes;
- it **records** every record it sends, with the source row and the mapping, template and cache versions that produced
  it, so you can always say where a record came from and what happened to it.

## From source data to OSDU in five steps

1. **Drop your data.** Put source files in the drop-off zone, or point a flow at a source database. Built-in flows load
   them into a table keyed by record.
2. **Keep the template.** Pick the OSDU schema of the type you deliver, in the GUI or with
   [`sqlflow template`](../cli/template.md), and commit it into a `templates` folder beside your mappings. Nothing to
   write.
3. **Fill the cache.** A [cache flow](../flow/cache.md) captures what your mappings look up, from OSDU, files or
   databases, and refreshes it on a schedule.
4. **Write the mapping.** A YAML file says how one row becomes one record ([the mapping](../flow/mapping.md)). Write it
   by hand ([writing a mapping](../guides/writing-a-mapping.md)), build it in the GUI's mapping builder, or let an AI
   assistant draft it.
5. **Deliver.** A [delivery flow](../flow/delivery.md) names the table, the mapping and the OSDU target. `plan` shows
   what would be sent, `run` sends it ([run](../cli/run.md)), and a schedule keeps OSDU in sync from then on.

Here is the mapping from the [welldb sample](../guides/welldb-sample.md) that delivers well logs with their curve data,
shortened. The comments show where each part comes from:

```yaml
documentType: mapping
name: WellLog
version: 1.4.0
template:                                  # template: the OSDU schema this mapping fills
  kind: osdu:wks:work-product-component--WellLog:1.4.0
  version: 26a3c3441882db4f
dataset:
  system: welldb
  key: [source_project, log_id]            # same source row, same OSDU record, every time
record:
  # ... ACLs and legal tags
  data:
    Name:
      $from: log_source                    # drop-off zone: a column of the source row
      $modifiers: [trim]
    Curves:
      $forEach: curves                     # drop-off zone: one entry per curve row
      $item:
        Mnemonic: { $from: curve_id }
        CurveUnit:
          $coalesce:                       # the first alternative that gives a value
            - $from: curve_unit
              $modifiers:
                - replace: $cache.UnitAlias  # cache: the source's unit, translated through a lookup table
                - ref                        # ...into a reference to OSDU's unit of measure
            # ... else the partition's own units, found by ID, Code or Name
    # ... the rest of the log
```

This mapping is the only part that knows about well logs. Nothing in the engine does: given another template and
mapping, it delivers wellbores, seismic, samples, production volumes or any other OSDU type.

## What you get

- **Every OSDU write path.** The storage, file and dataset services; manifest and workflow ingestion; and the domain
  data stores (DDMSs) for wellbores, seismic, rock and fluid samples, well delivery, production and reservoir models
  ([routes](../flow/routes.md)). The [coverage plan](../../../../docs/osdu-coverage-plan.md) lists what each one covers.
- **Stable ids.** The same source row always becomes the same OSDU record, so reruns never create duplicates.
- **Only what changed.** Each run reads the rows that changed and sends only the records that come out different.
  Change a mapping or the reference data, and exactly the affected records go again
  ([change detection](change-detection.md)).
- **Checked before sending.** Mappings are checked against their schema, legal tags are confirmed with OSDU, and each
  record can be validated before it goes out ([preflight](preflight.md)).
- **Safe when things fail.** Every step is recorded as it completes, so a retry picks up where it stopped. Problems
  that need a person are held for review, and a delivery that fails halfway is undone
  ([submissions](submissions.md), [record lifecycle](record-lifecycle.md)).
- **Scales out, runs near your data.** Work spreads over as many workers as you run. Workers fetch their work instead of
  accepting connections, so they can run inside the network that holds your data ([architecture](architecture.md)).
- **One configuration, every environment.** The same flows run in dev, test and prod. A new environment is a setting,
  not an edit to every flow ([partitions](partitions.md)).

## Every record accounted for

Traceability is the heart of the product. For every record OSDU Delivery sends, its ledger answers:

- **Where did it come from?** The source file and row.
- **What shaped it?** The versions of the mapping, schema and reference data it was built with.
- **What happened when it was sent?** Every attempt, with what OSDU answered.
- **What is it in OSDU?** The id and version it landed as.
- **Who touched it?** Every release, redelivery, removal and reversal, with who did it and when.

The dashboards count straight from the ledger, any record can be found by its key, OSDU id or source file, and nothing
is ever sent to OSDU without going through it. What answers each question is on [the ledger](ledger.md).

## After delivery

- **Verify** reads delivered records back from OSDU and flags any that changed there ([run](../cli/run.md)).
- **Explore** what OSDU holds, live in the GUI, searching by property or by your own source column names
  ([the explorer](explorer.md)).
- **Test** what landed with data quality checks, and publish the reports from CI
  ([data quality tests](../guides/data-quality-tests.md)).
- **Reconcile** OSDU against the ledger: find records no one accounts for and records OSDU lost, and fix them
  ([finding orphans](../guides/finding-orphans.md)).
- **Retrieve** OSDU records back into your own storage as files ([retrieving records](../guides/retrieving-records.md)).
- **Correct** with confidence: remove records, reverse a whole run, or send records again, with every action recorded
  ([removal and reversal](removal-and-reversal.md)).

## An AI assistant that builds it with you

Because a delivery is configuration, an AI assistant can build one. `osdu-delivery-mcp` connects Claude, Cursor,
VS Code, Codex, Slack or the GUI's own chat to OSDU Delivery:

- **Build.** Ask for a new data type and the assistant reads the schema, drafts the mapping, checks it until it is
  valid, and opens a pull request for a person to review.
- **Operate.** Ask whether a record was delivered, why it failed, which file it came from, or what a reference data
  change will resend.

The assistant works from definitions and delivery metadata, never from the records' content, and acts with the
signed-in person's own permissions ([the MCP server](../guides/mcp.md)).

## Compared with a custom loader per type

| Without OSDU Delivery | With OSDU Delivery |
| --- | --- |
| A custom loader for each type of data | A mapping for each type of data |
| Source-specific reading code in every loader | One drop-off zone, one shape: rows of a table |
| Lookup code against OSDU and other systems in every loader | One versioned cache that every mapping reads |
| Code for each OSDU service: storage, files, the domain data stores | Every OSDU write path built in |
| Retry and clean-up logic written by hand | Retries, resume and undo built in |
| Full reloads, or home-made change tracking | Only what changed is sent |
| "What did we send?" answered from logs, if at all | Every record traceable in a ledger |

## See also

- [What OSDU Delivery is](overview.md)
- [The welldb sample](../guides/welldb-sample.md)
- [Your first delivery, end to end](../guides/getting-started.md)
- [The mapping](../flow/mapping.md)
- [Which delivery problem maps to which shape](../../wiki/maps/pattern-catalog.md)
