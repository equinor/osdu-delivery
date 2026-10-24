# OSDU Delivery

[![SCM Compliance](https://scm-compliance-api.radix.equinor.com/repos/equinor/c5db785e-d5ab-4ece-acfd-51394a8cd325/badge)](https://developer.equinor.com/governance/scm-policy/)
[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](LICENSE)
[![.NET 9](https://img.shields.io/badge/.NET-9.0-512BD4.svg)](https://dotnet.microsoft.com/)

**Get any data into OSDU. Configured, not coded.**

Wells, logs, seismic, production data and more: delivered, kept in sync, and traceable record by record.

[OSDU](https://osduforum.org/) is the open data platform of the energy industry. Every type of data it holds has its
own schema, its own codes and its own services, so most teams write and maintain a custom loader for each one. OSDU
Delivery replaces those loaders with configuration: a mapping says how a row of your source data becomes an OSDU
record, and OSDU Delivery does the rest.

- **It delivers** the records through the right OSDU services, with their files and bulk data, such as log curves.
- **It keeps OSDU in sync** with your source, sending only what changed, including when a mapping or the reference data
  it reads changes.
- **It accounts for every record** in a ledger: the source row it came from, what shaped it, every attempt to send it,
  the OSDU id it landed as, and who has acted on it since.

## Start here

- [How it works](osdu/docs/reference/concepts/how-it-works.md): your source data, the OSDU schema and a cache, connected
  by a mapping, and what you get from it.
- [Run it locally](osdu/docs/reference/guides/run-locally.md): what to install, and the one command that sets a machine
  up and starts everything.
- [The welldb sample](osdu/docs/reference/guides/welldb-sample.md): a complete package that fills an empty partition,
  from reference data to well logs with their curves.
- [Your first delivery](osdu/docs/reference/guides/getting-started.md): one wellbore from a CSV file to OSDU, step by step
  on the command line.

## Documentation

- [What OSDU Delivery is](osdu/docs/reference/concepts/overview.md): the flows that carry data to OSDU, the ledger, and
  where to look
- [Reference documentation](osdu/docs/reference/README.md): every flow, mapping key, command and concept
- [Deployment](osdu/docs/reference/guides/deployment.md): Docker Compose, Kubernetes, Azure Bicep, Terraform and Radix
- [The AI assistant](osdu/docs/reference/guides/mcp.md): Claude, Cursor, VS Code, Codex, Slack or the GUI's own chat,
  building and operating deliveries with you
- [Architecture](osdu/docs/reference/concepts/architecture.md): the components, the databases and how they fit
- [Pattern catalog](osdu/docs/wiki/maps/pattern-catalog.md): which configuration solves which delivery problem
- [Wiki](osdu/docs/wiki/index.md): why it is this way: the decisions, patterns and narratives behind it

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
