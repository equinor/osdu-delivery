---
id: delivery-flow-staged-accounting
title: "Staged accounting (delivered, staged: true): every staged row in OSDU, or the ledger's reason it is not"
type: flow-reference
summary: "A delivered assertion that starts from the rows a delivery flow stages: each is in OSDU, held back by the ledger with its reason, or unexplained."
keywords:
  - staged accounting
  - staged
  - explained
  - delivered
  - are all records uploaded
  - every staged row delivered
  - compare the staged data with osdu
  - database versus osdu
  - held records
  - failed records
  - never planned
  - delivered and not found
  - why a record is not in osdu
  - ledger reason
  - record table
  - values
yamlPath: "(flowType: assertion) tests[].assert[].delivered with staged, explained, values, exact, interface"
related:
  - delivery-flow-assertion
  - delivery-flow-staged-proof-values
  - delivery-concept-ledger
  - delivery-flow-removal
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Documents/AssertionDocumentMapper.cs
  - osdu/src/SqlFlow.Delivery/Model/AssertionFlowDefinition.cs
  - osdu/src/SqlFlow.Delivery/Engine/Assertions/StagedAccounting.cs
  - osdu/src/SqlFlow.Delivery/Engine/Assertions/StagedDeliveries.cs
  - osdu/src/SqlFlow.Delivery/Engine/DeliveryFlowDocuments.cs
  - osdu/src/SqlFlow.Delivery/Model/MappingDefinition.cs
---

# Staged accounting (delivered, staged: true): every staged row in OSDU, or the ledger's reason it is not

A `delivered` assertion holds the records a delivery flow's ledger says it delivered to what OSDU holds. It cannot see a
row the delivery never took: one it held, one that failed, one no run planned. With `staged: true` it starts from the rows
the delivery flow stages instead, and accounts for each one: it is in OSDU, the ledger holds it back and says why, or
nothing explains it. That answers "is everything the source holds in OSDU?", and for each row that is not, why not.

```yaml
tests:
  - name: logs-staged
    kind: osdu:wks:work-product-component--WellLog:1.4.0
    query: 'data.Source:"{system}"'
    maxRecords: 100000
    assert:
      - delivered: welldb-welllog-03-delivery
        staged: true
        values: { logSource: COMPOSITE }
        explained: warning
```

The assertion names the delivery flow and nothing else of where its rows are: they are read through the delivery flow's
own `source.connection`, its record table, key, scope and filter, so the two never drift apart. The ledger is the module's
own, in the catalog's database every node reaches already, so it takes no connection either. To compare OSDU with what a
query of the staged tables answers, see [proof values from staged data](staged-proof-values.md).

## The keys

| Key | Default | Meaning |
| --- | --- | --- |
| `delivered` | required | The delivery flow, by name. It is found beside the assertion flow, as a [removal flow finds its delivery flow](removal.md#the-delivery-flow): where the catalog's read model says, or searched for from the assertion flow's folder up. It is bound to the partition the run tests. |
| `interface` | none | The interface of that flow, for a source with interfaces. |
| `staged` | `false` | `true` accounts for every row the flow stages; `false` holds the ledger's delivered records to OSDU, as ever. |
| `values` | none | The delivery flow's parameter values its rows are read under, written as a removal writes them: `{ logSource: COMPOSITE }`, a `{name}` token of this flow's parameters, or a list for a parameter its record scope reads several values of. Every parameter its `source.record.scope` binds needs one; the rest take the flow's defaults. Never `partition`, which is the run's. |
| `explained` | the assertion's | The severity of a row the ledger holds back and says why. `warning` lets the test warn on rows the ledger explains while it fails on rows nothing explains. |
| `exact` | `false` | `true` also fails a record the test's query finds that no staged row accounts for. |

`explained` and `values` apply only with `staged: true`, and are refused without it.

## How a row is accounted for

The rows are the delivery flow's record table in the scope `values` binds, as its delivery reads them: its `record.filter`
applied, read a page at a time. Each row's delivery key is derived from its key as the delivery derives it, and the ledger's
record of it is read by that key. The row is then:

| The row | Comes to | Says |
| --- | --- | --- |
| Its record is among those the test's query finds: the OSDU id the ledger holds it under, or for a row the ledger holds no record of, the id its mapping makes of its key | in OSDU | |
| The ledger holds it `held` | held back | `is held by <flow>: <the reason the ledger recorded>` |
| `failed` | held back | `failed in <flow>: <the error the ledger recorded>` |
| `waiting` | held back | `waits in <flow> for <id>, which another record of the ledger holds and has not delivered` |
| `pending` or `delivering` | held back | `is queued in <flow> and not sent yet` |
| `deleted` | held back | `was removed from OSDU, and <flow> sends it again when a run reads its row` |
| `reverted` | held back | `was reverted, and <flow> does not send it again while its row is unchanged, unless it is released` |
| `delivered`, and the query does not find it | unexplained | `is delivered by <flow> as <id> (version <v>) on <time>Z, and the test's query does not find it` |
| The ledger holds no record of it | unexplained | `is staged, and the ledger of <flow> holds no record of it: no run has planned it` |
| A key column is blank | unexplained | `is staged with a blank key column, so <flow> makes no record of it` |
| The table marks it deleted | not expected | (counted, never failed) |

Where the ledger knows which file and row a record came from, the reason ends with it: `(row 12 of welllogs.csv)`. A row in
OSDU counts whatever its ledger state, so a record held after an earlier version was delivered is in OSDU.

A row held back weighs `explained`; an unexplained row, and with `exact` a record no staged row accounts for, weighs the
assertion's own severity. The heavier decides the assertion.

## What is read, and its bounds

- **The records the test's query matches**, every one of them, by their ids: a test reading a sample is refused
  (`whether every staged row is among them needs all of them`). Narrow the test's `query` to the records the delivery flow
  writes, as `data.Source` does above, or `exact` counts every other record of the kind as unaccounted for.
- **The staged rows**, up to the test's `maxRecords`: more is refused, naming the flow
  (`... stages more than <n> rows in the scope read, more than this test reads; raise maxRecords ..., or read a narrower scope with values.`).
- **The ledger**, 1,000 keys at a time, by its primary key.

The assertion only reads: nothing is written to the record table, the ledger or OSDU.

## The report

```text
12345 row(s) staged: 12300 in OSDU, 40 held back by the ledger, 5 not in OSDU with nothing to explain it
(3 never planned, 2 delivered and not found, 0 with a blank key); 12 marked deleted, and not expected in OSDU;
held back most for is held by welldb-welllog-03-delivery: data.WellboreID: the wellbore W-17 the log names is not in the partition (28), ...
```

The checked count is the rows expected (staged, not marked deleted); the value is the share of them in OSDU. Examples name
the unexplained rows first, then the records no staged row accounts for, then the rows held back, each by its OSDU id (or
its key) and its label, up to the flow's `examples`. The reasons the ledger gave are grouped by state and problem, the five
most common named with how many rows share each.

## Refused

| Message | Cause |
| --- | --- |
| `... takes explained and values only with staged: true, ...` | `explained` or `values` on a delivered assertion reading the ledger. |
| `....values.partition is set; the partition is the run's, ...` | `values` naming `partition`. |
| `....values.<name> uses '{<token>}', which is neither {partition} nor declared under parameters.` | A token the assertion flow does not declare. |
| `<test>: assert[<n>].values gives delivery flow '<flow>' no value for '<parameter>', which its record scope binds column '<column>' to; ...` | A scope parameter left unnamed (when the run reads the rows). |
| `<test>: assert[<n>].values.<name> names no parameter of delivery flow '<flow>'; it declares ...` | A value for a parameter the delivery flow does not declare. |
| `<test>: assert[<n>].delivered names delivery flow '<flow>', which neither the catalog nor the folders around <file> declare. ...` | No delivery flow of that name beside the assertion flow. |

## See also

- [Assertion flow](assertion.md): the tests, the assertions and the report.
- [The delivery ledger](../concepts/ledger.md): what the ledger keeps of every record, and its states.
- [Proof values from staged data](staged-proof-values.md): OSDU compared with what a query of the staged tables answers.
- [Removal flow](removal.md): how a flow that names a delivery flow finds it.
