# OSDU Delivery

[![SCM Compliance](https://scm-compliance-api.radix.equinor.com/repos/equinor/c5db785e-d5ab-4ece-acfd-51394a8cd325/badge)](https://developer.equinor.com/governance/scm-policy/)
[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](LICENSE)
[![.NET 9](https://img.shields.io/badge/.NET-9.0-512BD4.svg)](https://dotnet.microsoft.com/)

**Get any data into OSDU. Configured, not coded.**

Wells, logs, seismic, production data and more: delivered, kept in sync, and traceable record by record.

[OSDU](https://osduforum.org/) is the open data platform of the energy industry. Every type of data it holds has its own
schema, its own codes and its own services, so most teams end up writing and maintaining a custom loader for each one.
OSDU Delivery replaces those loaders with configuration, for data teams that have to load data into OSDU, keep it
current, and answer for it.

## Try it

One command takes a laptop from nothing to a running OSDU Delivery that has delivered a sample estate and proved it. No OSDU
to connect to, no configuration: a stand-in for OSDU runs on the machine, and everything else is the real product.

```powershell
# Windows
irm https://raw.githubusercontent.com/TahirRiaz/osdu-delivery/main/bootstrap.ps1 | iex
```

```bash
# macOS and Linux
curl -fsSL https://raw.githubusercontent.com/TahirRiaz/osdu-delivery/main/bootstrap.sh | bash
```

You need a SQL Server, or Docker Desktop running to start one ([the local demo](osdu/docs/reference/guides/local-demo.md)).

## Introduction and architecture

<table>
<tr>
<td width="50%" valign="top">
<h3>Why it exists</h3>
<a href="https://www.youtube.com/watch?v=yffQyPOpJcA"><img src="https://img.youtube.com/vi/yffQyPOpJcA/maxresdefault.jpg" width="100%" alt="Stop building OSDU ingestion pipelines"></a>
<br><strong><a href="https://www.youtube.com/watch?v=yffQyPOpJcA">Stop building OSDU ingestion pipelines</a></strong> (3:42). Start here.
</td>
<td width="50%" valign="top">
<h3>How it works</h3>
<a href="https://www.youtube.com/watch?v=vxYiGkvaSLs"><img src="https://img.youtube.com/vi/vxYiGkvaSLs/maxresdefault.jpg" width="100%" alt="Architecture and data flow"></a>
<br><strong><a href="https://www.youtube.com/watch?v=vxYiGkvaSLs">Architecture and data flow</a></strong> (6:18). No coding or OSDU knowledge needed.
</td>
</tr>
</table>

## Technical deep dive

<table>
<tr>
<td width="50%" valign="top">
<h3>How to write a mapping</h3>
<a href="https://www.youtube.com/watch?v=k8XU0Boqh4w"><img src="https://img.youtube.com/vi/k8XU0Boqh4w/maxresdefault.jpg" width="100%" alt="Map curated data to OSDU with YAML"></a>
<br><strong><a href="https://www.youtube.com/watch?v=k8XU0Boqh4w">Map curated data to OSDU with YAML</a></strong> (17:30). A hands-on tutorial.
</td>
<td width="50%" valign="top">
<h3>How the code is organized</h3>
<a href="https://www.youtube.com/watch?v=QnOzD6ycfV8"><img src="https://img.youtube.com/vi/QnOzD6ycfV8/maxresdefault.jpg" width="100%" alt="Codebase walkthrough"></a>
<br><strong><a href="https://www.youtube.com/watch?v=QnOzD6ycfV8">Codebase walkthrough</a></strong> (12:58). For developers.
</td>
</tr>
</table>

### What a mapping looks like

One YAML file connects three places: the drop-off zone that holds your source data, the template that fixes the OSDU
schema, and the cache that holds the lookups. This one is simplified from the sample's well logs:

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
mapping, it delivers wellbores, seismic, samples, production volumes or any other OSDU type. A mapping can also clean,
translate, convert and compute: [mapping transformations](osdu/docs/reference/flow/mapping-transformations.md) lists
every function and modifier with its result.

## What you get

- **Every OSDU write path.** The storage, file and dataset services; manifest and workflow ingestion; and the domain
  data stores (DDMSs) for wellbores, seismic, rock and fluid samples, well delivery, production and reservoir models
  ([coverage plan](docs/osdu-coverage-plan.md)).
- **Safe to rerun.** The same row always becomes the same record, and only what changed is sent. Retries, resume and
  undo are built in.
- **Checked before sending.** Mappings are checked against their schema, legal tags are confirmed with OSDU, and each
  record can be validated before it goes out.
- **Every record accounted for.** Nothing reaches OSDU without going through the
  [ledger](osdu/docs/reference/concepts/ledger.md), and any record is found by its key, OSDU id or source file.
- **After delivery.** Verify delivered records against OSDU, explore what OSDU holds live in the GUI, test it with data
  quality checks, reconcile records no one accounts for, retrieve records back into your own storage, and correct with
  removals, reversals and redeliveries, every action recorded.
- **Runs near your data.** Workers fetch their work, so they can run inside the network that holds your data.
- **One configuration, every environment.** The same flows run in dev, test and prod; a new environment is a setting.

## An AI assistant that builds it with you

Because a delivery is configuration, an AI assistant can build one. `osdu-delivery-mcp` connects Claude, Cursor,
VS Code, Codex, Slack or the GUI's own chat to OSDU Delivery. Ask for a new data type and it reads the schema, drafts the
mapping, checks it until it is valid, and opens a pull request for a person to review; ask whether a record was
delivered, why it failed, or what a reference data change will resend. It works from definitions and delivery
metadata, never from the records' content, with the signed-in person's own permissions; only data mode, which a
message starting with `!` turns on, reads data, with a read-only query over what an approved AI context opens
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
- [Mapping transformations](osdu/docs/reference/flow/mapping-transformations.md): every function, modifier and lookup
  a mapping supports, with results
- [Deployment](osdu/docs/reference/guides/deployment.md): Docker Compose, Kubernetes, Azure Bicep, Terraform and Radix
- [Architecture](osdu/docs/reference/concepts/architecture.md): the components, the databases and how they fit
- [Pattern catalog](osdu/docs/wiki/maps/pattern-catalog.md): which configuration solves which delivery problem
- [Wiki](osdu/docs/wiki/index.md): why it is this way: the decisions, patterns, narratives, incidents and maps

## Status

Delivery has been verified against a live OSDU (Azure Data Manager for Energy 0.29, September 2026) through the storage,
file, manifest ingestion and Wellbore DDMS paths. Every write path is tested against OSDU's own service contracts, pinned
in [osdu/specs](osdu/specs). Recent changes are in the [changelog](CHANGELOG.md).

## Contributing

[CONTRIBUTING.md](CONTRIBUTING.md) sets up a development machine and runs the tests, the
[codebase walkthrough](https://www.youtube.com/watch?v=QnOzD6ycfV8) shows how the code is organized,
[CLAUDE.md](CLAUDE.md) holds the project's working rules, and [osdu/README.md](osdu/README.md) is a map of the module's
code. OSDU Delivery is built on
[SQLFlow](https://github.com/TahirRiaz/SQLFlow), a metadata-driven pipeline engine kept in [sqlflow/](sqlflow/), which
is why its command line is `sqlflow`.

## License and contact

Licensed under the GNU General Public License v3.0 ([LICENSE](LICENSE)).

Questions, bugs and feature requests: **[tahir@sqlflow.io](mailto:tahir@sqlflow.io)** or an issue on the repository.
Security reports go by email or a private security advisory, never a public issue ([SECURITY.md](SECURITY.md)).
