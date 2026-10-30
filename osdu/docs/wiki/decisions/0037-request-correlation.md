---
id: delivery-wiki-0037-request-correlation
title: "Decision 0037: every OSDU request carries the run's correlation id, named by the repository and the flow"
type: decision
summary: "Why every OSDU request carries the run's id in correlation-id, under the flow's operation code and a repository document's code."
keywords:
  - correlation id
  - correlation-id header
  - telemetry
  - repository document
  - osdu-delivery.yaml
  - operation code
  - run id
  - azure logs
updated: 2026-10-30
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Http/OsduCorrelation.cs
  - osdu/src/SqlFlow.Delivery/Engine/RunCorrelation.cs
  - osdu/src/SqlFlow.Delivery/Documents/RepositoryDocuments.cs
  - osdu/src/SqlFlow.Delivery/Model/FlowTelemetry.cs
  - osdu/src/SqlFlow.Delivery/Engine/Operations/DeliveryOperations.cs
referenceRefs:
  - delivery-concept-request-correlation
  - delivery-flow-repository
  - delivery-flow-overview
  - delivery-concept-ledger
---
# 0037: Every OSDU request carries the run's correlation id, named by the repository and the flow

Status: accepted. Documented in [request correlation](../../reference/concepts/request-correlation.md) and
[the repository document](../../reference/flow/repository.md).

## Context

The platform's operators trace what reaches OSDU through its telemetry in Azure, and needed every request OSDU Delivery
sends to say what sent it: which repository, which operation, which execution. Until then a request carried a
`correlation-id` with a bare GUID minted for each unit of work (a record's try, a chunk of removals, a read), so an
attempt could be matched to the services' logs, but nothing in the value said where it came from. Requests outside those
units carried none: every search of a cache refresh, a retrieval, a dimension build, an inventory build or an assertion.

The owner set the shape: `osdu-delivery-<repository>-<operation>-<guid>`, the repository's part set per repository, the
operation written in the flow's YAML in short form (`dim` for a dimension build), the GUID the system's own for the
execution, and the product's own calls flagged `osdu-delivery`.

## Decision

- **The header is `correlation-id`.** OSDU's Azure library (`os-core-lib-azure`) puts it on every log line of a request
  (`Slf4jMDCFilter`), records it in the transaction log as one of three request headers it records at all, beside
  `data-partition-id` and `content-type` (`TransactionLogFilter`), and carries it on the messages the service publishes
  (`PubSubAttributesBuilder`, `ServiceBusPublisher`). A header of another name would be sent and never logged.
- **The value is `osdu-delivery-<repository>-<operation>-<run id>`.** The product comes first, so one prefix finds
  everything it sent. Codes are a lower-case letter, then lower-case letters and digits, with no hyphen, so the value
  splits on its hyphens and the id is always its last 36 characters.
- **The id is the run's.** Every request of one run carries the same value, the id the GUI, the API and `sqlflow run`
  list the run under, so one value finds an execution and its prefix finds every run of a repository or a flow. A
  record's attempt names the value its requests carried, as it did the unit of work's.
- **The repository's code is a document at the repository's root**, `osdu-delivery.yaml` with `documentType: repository`
  and `code`, found walking up from the flow's file as its `mappings/` are, never above the folder holding `.git`. It is
  versioned and reviewed with the flows, and a folder may hold its own for the flows under it.
- **The operation code is the flow's `telemetry.operation`**, any kind, or its kind's code (`dlv`, `cache`, `ret`, `ast`,
  `dim`, `inv`, `rmv`, and `dwn` for the download flow of [0038](0038-download-flow.md)).
- **Every kind's executor opens the run's value before it does anything else** and says it in the run log; a repository
  document a run cannot read fails the run before it sends anything, and `sqlflow validate` refuses the flow for it.
- **The product's own operations carry `osdu-delivery-<operation>-<id>`**, a new id each time: the target probe
  (`probe`), a record's read-back (`read`), the explorer (`explore`), a preview, a value check, the schema index
  (`schema`) and the setup check (`signin`). Inside a run, an operation's requests are the run's. The schema index's
  reading outlives the ask that started it, so it carries a value of its own.
- **Anything else carries `osdu-delivery-<id>`**, so no request to OSDU goes without one.
- **A flow cannot set the header**: a `correlation-id` in any `headers` block is refused.

## Alternatives considered

- **A header named in the YAML.** Not taken: the services log `correlation-id` only, so any other name defeats the
  purpose, and two headers would leave two ids to reconcile.
- **A new id for each unit of work, as before.** Not taken: the operators trace executions, and a value per try cannot be
  grouped into a run from the platform's side. The cost is that requests of one run share a value; a failing request is
  told apart by its path, its record id and its time, and the record's attempt names its run.
- **The repository's code in each flow.** Not taken: it would be repeated in every flow of a repository, with nothing
  keeping the copies alike.
- **The code in the control plane's configuration of the repository**, beside its URL and branch. Not taken: SQLFlow's
  repository sources would need a property bag and runs would need to know their repository, a change to the vendored
  engine for an OSDU concern, and the code would live outside git.
- **Refusing a flow with no repository document.** Not taken: every estate would stop until it added one. A flow without
  one runs as before, its value without the repository's segment, and its run log says why.

## Consequences

- Every OSDU flow kind runs from a checkout of its repository at the run's commit: retrieval and inventory flows, and
  cache flows holding no dictionary, ran from a snapshot of their one file before.
- Attempts written before keep their bare GUID; a problem signature turns either into `<id>`, so failures still group.
- The value is longer than a GUID: the GUI shows it truncated with a copy, and nothing stores it in a column.
- The setup check keeps to SQLFlow's core and writes the value itself; a test holds it to the module's form.
