---
id: delivery-wiki-0002-client-supplied-ids
title: "Decision 0002: deterministic, client-supplied OSDU ids"
type: decision
summary: "Why OSDU ids are computed from the data by the delivery instead of assigned by OSDU, and what that guarantees."
keywords:
  - "osdu id"
  - "client-supplied id"
  - "deterministic id"
  - "duplicate records"
  - "idfrom"
  - "delivery key"
referenceRefs:
  - delivery-flow-mapping
  - delivery-concept-ledger
related:
  - delivery-wiki-0015-lost-records
updated: 2026-10-16
---
# 0002: Deterministic, client-supplied OSDU ids

Status: proposed; needs confirmation that the data partition accepts client-supplied ids for the kinds in
scope. Documented in [mapping](../../reference/flow/mapping.md) (The OSDU id).

## Context

OSDU record ids may be client-supplied in the form `{partition}:{entityType}:{unique}`. The previous design
let OSDU assign ids and then had to remember them, which is where the duplicate-record defects came from.

## Decision

The unique segment is the delivery key without hyphens. The renderer stamps `id` on every document; the
storage and wellbore DDMS writes are upserts by that id. The ledger's `TargetId` is a convenience, not a
dependency: it can be recomputed from the key at any time.

## Consequences

- Upsert is native; create-versus-update disappears as a concept. A replayed submission cannot create a
  second record.
- References to records this system also delivers can be computed rather than searched, because their ids follow
  from their keys. The template format has no computed reference: a mapping reads such an id from the metadata cache
  (`$cache: <Type>.id` with `$findBy`, [flow/mapping-lookups.md](../../reference/flow/mapping-lookups.md)).
- If a partition rejects client-supplied ids for a kind, the protocol would need a lookup-by-natural-key
  step before the write and the ledger's `TargetId` would become authoritative. That is a protocol-level
  change; the mapping, hashing and ledger are unaffected.

## Addendum: ids made from the key

A mapping may make the unique segment from the key's own values, percent-encoded
(`dataset.idFrom: key`), so reference data it delivers is named by code as OSDU's catalogs are
(`WELLDB::GAPI`). The delivery key without hyphens stays the default. The id is still computed from the
data, and the delivery key stays the ledger's identity for the record, so the consequences above hold
with two additions: ids made from values can meet where delivery keys cannot, so the ledger refuses a
second claim on an id from a record of the same flow as it does from another flow's; and the ledger's
`TargetId` is no longer recomputable from the delivery key alone, so a record keeps the id it first
claimed, and a render that gives it another is held rather than sent. The one exception is an id an
operator gives a record OSDU lost ([0015](0015-lost-records.md)): the record claims it, marked assigned, and
is written under it whatever its mapping makes from its key.
