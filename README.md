# OSDU Delivery

[![SCM Compliance](https://scm-compliance-api.radix.equinor.com/repos/equinor/c5db785e-d5ab-4ece-acfd-51394a8cd325/badge)](https://developer.equinor.com/governance/scm-policy/)
[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](LICENSE)
[![.NET 9](https://img.shields.io/badge/.NET-9.0-512BD4.svg)](https://dotnet.microsoft.com/)

**Get any data into OSDU. Configured, not coded.**

Wells, logs, seismic, production data and more: delivered, kept in sync, and traceable record by record.

[OSDU](https://osduforum.org/) is the open data platform of the energy industry: one place for wells, logs, seismic,
production data and more, each stored as typed records behind a set of services. Getting data into it is hard. Every
type of data has its own schema, its own codes and its own services, so most teams end up writing and maintaining a
custom loader for each one.

OSDU Delivery replaces those loaders with configuration. Everything a loader would otherwise read, look up or hard-code
lives in three places, each known and versioned, and a mapping connects them:

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
| **Drop-off zone** | Where your source data arrives: files (CSV, JSON, Parquet) in a folder or storage location, or the tables of a source database, loaded into a table keyed by record. | Whatever system the data came from, the mapping reads it in one shape: columns of a row. |
| **Template** | The OSDU schema of the type you deliver, kept as a file beside your mappings. | The mapping fills a known shape, and is checked against it before anything runs. |
| **Cache** | What mappings look values up in: reference data captured from OSDU, lookup tables from files or databases, and small dictionaries. One per OSDU data partition, kept as versions. | Codes and units are translated, and references checked against what OSDU holds, without a line of lookup code. |

With those three in place, a new type of data needs only a mapping: this column, translated through that lookup, fills
that property of the schema. It is built for data teams that have to load data into OSDU, keep it current, and answer
for it.

## Why it matters

| Without OSDU Delivery | With OSDU Delivery |
| --- | --- |
| A custom loader for each type of data | A mapping for each type of data |
| Source-specific reading code in every loader | One drop-off zone, one shape: rows of a table |
| Lookup code against OSDU and other systems in every loader | One versioned cache that every mapping reads |
| Code for each OSDU service: storage, files, the domain data stores | Every OSDU write path built in |
| Retry and clean-up logic written by hand | Retries, resume and undo built in |
| Full reloads, or home-made change tracking | Only what changed is sent, including after a mapping or reference data change |
| "What did we send?" answered from logs, if at all | Every record traceable in a ledger |

## How it works

1. **Drop your data.** Put source files in the drop-off zone, or point a flow at a source database.
2. **Keep the template.** Pick the OSDU schema of the type you deliver, in the GUI or from the command line, and commit
   it beside your mappings. Nothing to write.
3. **Fill the cache.** A cache flow captures what your mappings look up, and refreshes it on a schedule.
4. **Write the mapping.** A YAML file says how one row becomes one record. Write it by hand, build it in the GUI's
   mapping builder, or let an AI assistant draft it.
5. **Deliver.** A delivery flow names the table, the mapping and the OSDU target. `plan` shows what would be sent,
   `run` sends it, and a schedule keeps OSDU in sync from then on.

A mapping, simplified from the sample's well logs:

```yaml
name: WellLog
template:
  kind: osdu:wks:work-product-component--WellLog:1.4.0   # template: the OSDU schema this mapping fills
dataset:
  key: [source_project, log_id]          # same source row, same OSDU record, every time
record:
  data:
    Name: { $from: log_source }          # drop-off zone: a column of the source row
    Curves:
      $forEach: curves                   # one entry per curve row
      $item:
        Mnemonic: { $from: curve_id }
        CurveUnit:
          $from: curve_unit
          $modifiers:
            - replace: $cache.UnitAlias  # cache: the source's unit, translated through a lookup table
            - ref                        # ...into a reference to OSDU's unit of measure
```

This mapping is the only part that knows about well logs. Nothing in the engine does: given another template and
mapping, it delivers wellbores, seismic, samples, production volumes or any other OSDU type.

## What you get

- **Every OSDU write path.** The storage, file and dataset services; manifest and workflow ingestion; and the domain
  data stores (DDMSs) for wellbores, seismic, rock and fluid samples, well delivery, production and reservoir models
  ([coverage plan](docs/osdu-coverage-plan.md)).
- **Stable ids.** The same source row always becomes the same OSDU record, so reruns never create duplicates.
- **Checked before sending.** Mappings are checked against their schema, legal tags are confirmed with OSDU, and each
  record can be validated before it goes out.
- **Every record accounted for.** The ledger holds where each record came from (the source file and row), what shaped
  it (the mapping, schema and reference data versions), every attempt with what OSDU answered, the id and version it
  landed as, and every release, redelivery, removal and reversal with who did it and when. Nothing reaches OSDU without
  going through it, and any record is found by its key, OSDU id or source file.
- **After delivery.** Verify delivered records against OSDU, explore what OSDU holds live in the GUI, test it with data
  quality checks, reconcile records no one accounts for, retrieve records back into your own storage, and correct with
  removals, reversals and redeliveries, every action recorded.
- **Runs near your data.** Work spreads over as many workers as you run, and workers fetch their work instead of
  accepting connections, so they can run inside the network that holds your data.
- **One configuration, every environment.** The same flows run in dev, test and prod; a new environment is a setting.

## An AI assistant that builds it with you

Because a delivery is configuration, an AI assistant can build one. `osdu-delivery-mcp` connects Claude, Cursor,
VS Code, Codex, Slack or the GUI's own chat to OSDU Delivery. Ask for a new data type and it reads the schema, drafts the
mapping, checks it until it is valid, and opens a pull request for a person to review; ask whether a record was
delivered, why it failed, or what a reference data change will resend. It works from definitions and delivery
metadata, never from the records' content, with the signed-in person's own permissions
([MCP guide](osdu/docs/reference/guides/mcp.md)).

## Try it

Run `dev.bat` (Windows) or `./dev.sh` (macOS, Linux) from the checkout: the first start asks for a SQL Server, an OSDU
platform and a flow repository, sets the machine up, and opens the GUI
([Run it locally](osdu/docs/reference/guides/run-locally.md)).

The [welldb sample](osdu/docs/reference/guides/welldb-sample.md) fills an empty partition, from the reference data to
well logs with their curves. Once its wellbores are in OSDU, delivering the logs takes four commands:

```bash
cd osdu/samples
sqlflow check   welldb/flows/welldb-welllog-03-header-delivery.yaml --set logSource=COMPOSITE
sqlflow preview welldb/flows/welldb-welllog-03-header-delivery.yaml --set logSource=COMPOSITE
sqlflow run     welldb/flows/welldb-welllog-03-header-delivery.yaml --set logSource=COMPOSITE --operation plan
sqlflow run     welldb/flows/welldb-welllog-03-header-delivery.yaml --set logSource=COMPOSITE
```

`check` validates the flow, its mapping and its schema; `preview` builds one record without sending it; `plan` shows
what a run would send; `run` delivers. [Your first delivery](osdu/docs/reference/guides/getting-started.md) builds one
from scratch.

## Learn more

- [What OSDU Delivery is](osdu/docs/reference/concepts/overview.md): the product on one page
- [Reference documentation](osdu/docs/reference/README.md): every flow, mapping key, command and concept
- [Deployment](osdu/docs/reference/guides/deployment.md): Docker Compose, Kubernetes, Azure Bicep, Terraform and Radix
- [Architecture](osdu/docs/reference/concepts/architecture.md): the components, the databases and how they fit
- [Pattern catalog](osdu/docs/wiki/maps/pattern-catalog.md): which configuration solves which delivery problem
- [Wiki](osdu/docs/wiki/index.md): why it is this way: the decisions, patterns, narratives, incidents and maps

## Status

Delivery has been verified against a live OSDU (Azure Data Manager for Energy 0.29, September 2026) through the storage,
file, manifest ingestion and Wellbore DDMS paths. Every write path is tested against OSDU's own service contracts, pinned
in [osdu/specs](osdu/specs). Recent changes are in the [changelog](CHANGELOG.md).

## Contributing

[CONTRIBUTING.md](CONTRIBUTING.md) sets up a development machine and runs the tests, [CLAUDE.md](CLAUDE.md) holds the
project's working rules, and [osdu/README.md](osdu/README.md) is a map of the module's code. OSDU Delivery is built on
[SQLFlow](https://github.com/TahirRiaz/SQLFlow), a metadata-driven pipeline engine kept in [sqlflow/](sqlflow/), which
is why its command line is `sqlflow`.

## License and contact

Licensed under the GNU General Public License v3.0 ([LICENSE](LICENSE)).

Questions, bugs and feature requests: **[tahir@sqlflow.io](mailto:tahir@sqlflow.io)** or an issue on the repository.
Security reports go by email or a private security advisory, never a public issue ([SECURITY.md](SECURITY.md)).
