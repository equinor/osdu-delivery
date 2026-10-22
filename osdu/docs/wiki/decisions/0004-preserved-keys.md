---
id: delivery-wiki-0004-preserved-keys
title: "Decision 0004: Datasets, DDMSDatasets and ExtensionProperties belong to OSDU"
type: decision
summary: "Why an update keeps the properties a DDMS or External Data Services writes into a record, and leaves them out of the hash."
keywords:
  - "preserved keys"
  - "datasets"
  - "ddmsdatasets"
  - "extensionproperties"
  - "record update"
  - "eds"
referenceRefs:
  - delivery-flow-ddms
  - delivery-concept-change-detection
related:
  - delivery-wiki-0019-linked-templates
updated: 2026-10-22
---
# 0004: Datasets, DDMSDatasets and ExtensionProperties are OSDU's

Status: proposed. Documented in [routes](../../reference/flow/routes.md) and [change detection](../../reference/concepts/change-detection.md).

## Context

The wellbore DDMS populates `data.Datasets` and `data.DDMSDatasets` when bulk data is written, and
`data.ExtensionProperties` may be enriched downstream. None of them comes from the source, so none is in
the rendered document or its hash. An update that replaced the whole `data` block would erase them.

External Data Services does the same to a connected source data job: after every fetch it reads the job and writes it
back with its run state (`LastSuccessfulRunDateUTC`, `FailedRecords`, `CreateTimeMax`;
[../../specs/eds-dms/INTEGRATION.md](../../../specs/eds-dms/INTEGRATION.md) section 2.1), on its own schedule.

## Decision

They are OSDU's. The flow's `protocolOptions.preserveDataKeys` lists them; before an update the protocol
reads the current record and copies those keys into the outgoing document. They stay outside the content
hash and outside change detection.

A data job's run state is carried the same way without the flow listing it: the storage, manifest and workflow routes
add those keys to the flow's for every record of that type.

## Consequences

- An update never erases bulk-data bookkeeping, downstream enrichment or a job's run state.
- A write that carries keys records the hash of everything else it wrote (`ownedContent.hash`, with the keys it left out),
  and a verify pass that finds a newer version reads the record and compares it: a version whose only changes are in
  those keys is another system's, not drift. This keeps a data job EDS updates after every fetch from showing as drift,
  and from being sent again by a reconciling pass after each run. The routes that write records through storage or a
  manifest record the hash (storage, file, dataset, manifest, manifestAndDdms and workflow; a registration through the
  Dataset service writes the record whole and clears it); the ddms and fileAndDdms routes judge drift by version alone.
- Reconciling a record that did drift re-sends the rendered document with the keys preserved again.
- If the domain decides one of them is ours (for example a curated `ExtensionProperties`), remove it from the
  list and map it in the mapping, at which point it enters the hash like any other property.
