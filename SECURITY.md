# SECURITY

If you discover a security vulnerability in this project, please follow the steps below to report it.

## For "non-critical" issues

Create a GitHub issue for the vulnerability and ask the maintainer to reach out to you. Avoid putting sensitive information in the issue.

### For "critical" and time sensitive issues

Send email to [it-security@equinor.com](mailto:it-security@equinor.com) or phone the Equinor helpdesk:

- Norway (+47) 51 999 222
- US/Canada (+1) 713 878 6970

## OSDU Delivery Security Policy

This repository is a fork of [TahirRiaz/osdu-delivery](https://github.com/TahirRiaz/osdu-delivery). Vulnerabilities in that project itself are reported as described in its policy below.

## Supported Versions

OSDU Delivery is pre-1.0 and under active development. Security fixes are applied to the `main` branch.

## Reporting a Vulnerability

**Please do not report security vulnerabilities through public GitHub issues.**

Instead, report them privately through the repository's Security Advisories page, or by email to
**<tahir@sqlflow.io>**.

Please include:

- A description of the issue and its impact
- Steps to reproduce
- Affected commit
- Whether it is in the OSDU module (`osdu/`) or the vendored SQLFlow (`sqlflow/`), as far as you can tell

We aim to acknowledge reports within 3 business days and to provide a remediation timeline after triage.

## Handling of credentials

OSDU Delivery never stores secrets in flow, mapping or cache documents. Database connection strings, OSDU client
secrets and storage credentials are supplied by reference and resolved at run time, on every platform .NET runs
on. The canonical contract is
[osdu/docs/reference/concepts/environment-variables.md](osdu/docs/reference/concepts/environment-variables.md):

- Explicit references: `${env:NAME}` and `${keyvault:vault/secret}`.
- Local development values belong in the git-ignored `.sqlflow/env` file; the process environment (CI,
  Kubernetes, schedulers) always wins over it.
- `sqlflow validate` and `sqlflow run` warn loudly when a document embeds a `Password=`-style literal, without
  echoing the value. Resolved secrets are redacted before they reach any log, trace, error message, run artifact
  or ledger row, and the catalog stores redacted errors only.
- Credentials live on the tier that uses them. A compute node holds the OSDU credentials and the ingestion
  database connection its pool's flows need; nothing data-plane passes through the control plane.

Please follow this convention in any contribution and never commit real connection strings or credentials.

## Live OSDU platforms

Anything a test or a verification run creates in a live OSDU partition is logged when it is created and removed
when the work is done, at the reversible scope. Never open a pull request that adds a destructive OSDU call
(`DELETE /records/{id}`, a purge) to a test path.
