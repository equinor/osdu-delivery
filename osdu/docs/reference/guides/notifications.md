---
id: delivery-guide-notifications
title: "Failure notifications for OSDU flows: which delivery, cache and assertion runs alert, and why held records do not"
type: guide
summary: "Which OSDU flow runs alert subscribers, why held or failed records do not, and keeping a delivery flow quiet while it is built."
keywords:
  - notifications
  - alerts
  - failure alert
  - email
  - slack
  - lifecycle
  - development
  - held records
  - failed run
  - failrunon
  - subscription
  - digest
related:
  - guide-notifications
  - delivery-flow-delivery
  - delivery-flow-assertion
  - delivery-concept-record-lifecycle
  - delivery-concept-run-trace-and-metrics
  - delivery-guide-operations-runbook
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Engine/DeliveryExecutor.cs
  - osdu/src/SqlFlow.Delivery/Engine/SourceRuntime.cs
  - osdu/src/SqlFlow.Delivery/Engine/CacheExecutor.cs
  - osdu/src/SqlFlow.Delivery/Engine/AssertionExecutor.cs
  - osdu/src/SqlFlow.Delivery/Engine/Assertions/AssertionRunner.cs
  - osdu/src/SqlFlow.Delivery/Engine/RunFailure.cs
  - osdu/src/SqlFlow.Delivery/Model/AssertionFlowDefinition.cs
  - osdu/src/SqlFlow.Delivery/Documents/YamlModels.cs
  - osdu/src/SqlFlow.Delivery/Hosting/OsduDeliveryBranding.cs
  - sqlflow/src/SqlFlow.ControlPlane/Background/NotificationService.cs
  - sqlflow/src/SqlFlow.ControlPlane/Notifications/NotificationComposer.cs
  - sqlflow/src/SqlFlow.Catalog/NotificationStore.cs
  - sqlflow/src/SqlFlow.Catalog/CatalogSync.cs
---

# Failure notifications for OSDU flows

The control plane tells the people who subscribed when a run goes wrong: it failed, was cancelled, or was skipped
because an upstream member of its run group did not succeed. Subscriptions are opt-in and per user, they coalesce into
one message per window, and they go out by email or Slack. All of that is SQLFlow's, unchanged, and documented in
[SQLFlow's notifications guide](../../../../sqlflow/docs/reference/guides/notifications.md): the pipeline, immediate and
digest pacing, what a subscription selects, the channels and their configuration. This guide covers what it means for
OSDU flows.

## A notification is about a run, not a record

Every OSDU flow kind is an ordinary SQLFlow flow, so its runs alert like any other. What fails a run differs by kind:

| Kind | The run fails when |
| --- | --- |
| `delivery` | Something stops the run: the target, the ledger, the source or the flow document refuses or fails as a whole; or, for a source with several interfaces, an interface does not complete (the run ends failed and still reports what each interface did). |
| `cache` | The refresh cannot capture what it declares, or, for a flow over several partitions, one partition does not complete. |
| `assertion` | Its assertions (the `tests:` entries) come out as `failRunOn` says: a failed or errored one (`error`, the default), a warned one as well (`warning`), or never (`never`). The error names them. |
| `removal` | A removal fails, or holds what it found for an operator's approval (its guard); the run ends failed with every other removal done, and still reports how each came out. |
| `download` | A download fails; the run ends failed with every other download done. |
| `retrieval`, `dimension`, `inventory` | Something stops the run. |

**A delivery run that held or failed records still succeeds.** A held record (a value the mapping could not resolve, a
payload that is not where the record says) and a failed one are outcomes the ledger keeps with their reason, and the run
goes on with the rest. They are seen and acted on in the flow's issues, the Records page and
[sqlflow records](../cli/records.md) (`records issues`), and counted in the run's result. Alerting on them through notifications would page
on healthy runs, so the surfaces for record problems are:

- the flow's issues, which group blocked records by the error they share ([record lifecycle](../concepts/record-lifecycle.md));
- the `osdu_delivery.records` metric, whose `held` and `failed` share per flow is what to alert on
  ([run trace and metrics](../concepts/run-trace-and-metrics.md#what-to-alert-on));
- the scheduled target probe, which records an `unreachable` target in the audit trail before a run is due.

A subscription's flow-name filter is the useful narrowing: give each delivery chain its own flow names
(`welldb-wellbore-01-pre`, `welldb-wellbore-02-ing`, `welldb-wellbore-03-delivery`), and a subscription follows the chain
someone owns, pre and ingestion flows included.

## Keep a flow quiet while it is built

Every flow document, an OSDU flow included, can declare `lifecycle:` in its envelope beside `flowType` and `name`:
`production` (the default) alerts on failure, and `development` runs, schedules and records history identically but
raises no notification at all. Bringing up a delivery chain means iterating on a mapping against a real OSDU, and a
half-built mapping fails often, so a new flow starts as `development`:

```yaml
# flows/welldb-wellbore-03-delivery.yaml while it is built: the lifecycle line added to its top
flowType: delivery
name: welldb-wellbore-03-delivery
batch: welldb
lifecycle: development   # remove, or set to production, when the flow goes live

partitions: [dev]

# ... source, render and target as getting started writes them
```

The gate is applied when failures are detected, against the synced pipeline, so it takes effect on the repository's next
sync. Another value is refused by `sqlflow validate`: `'lifecycle' has unknown value 'staging'. Allowed: production, development.`

## What a message carries

Messages are SQLFlow's, under the product's name: a subject reads `OSDU Delivery: ...`, or `OSDU Delivery digest: ...` for
a digest. Set `ControlPlane:Notifications:GuiBaseUrl` to the GUI's address and each failure links to its run, whose trace
says what stopped it and which records it touched ([run trace and metrics](../concepts/run-trace-and-metrics.md)).

## See also

- [SQLFlow: failure notifications](../../../../sqlflow/docs/reference/guides/notifications.md)
- [Delivery flow](../flow/delivery.md)
- [Assertion flow](../flow/assertion.md)
- [Operations runbook](operations-runbook.md)
