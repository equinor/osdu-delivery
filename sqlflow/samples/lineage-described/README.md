# Described lineage: what made the files a flow lands

One landing flow, and two lineage descriptions (`flowType: lin`) that say where its files come from:

```text
erp sales.orders ─┐
                  ├─▶ orders-u1-curate-lin ─▶ curated.orders ─▶ orders-u2-publish-lin ─▶ data/orders ─▶ described-land-orders
erp exchange_rates┘                                                                      (the estate's own flow, wave 1)
```

| Document | Kind | What it is |
| --- | --- | --- |
| `10-land-orders.flow.yaml` | file | The estate's own flow: lands the files under `data/orders`. |
| `lineage/orders-u1-curate.yaml` | lin | A step another system runs: builds `curated.orders` from the order system. Marks its two reads as the origin, and maps four columns. |
| `lineage/orders-u2-publish.yaml` | lin | The step that writes the files. It names the landing flow with `feeds`, so the location is written once, in that flow. |

A lineage description is metadata. It is never run, it is in no wave, and the lineage SQLFlow computes is the same with
or without it.

```text
$ sqlflow validate lineage/orders-u2-publish.yaml
OK  'orders-u2-publish-lin' is valid (lineage description of a lakehouse step: 1 read(s), 1 write(s), 5 mapped column(s); metadata only, never run).

$ sqlflow lineage . --no-observed
Lineage over 1 flow(s), 2 object(s), 2 edge(s) (declared)
  wave 1: described-land-orders
  described lineage: 2 step(s) other systems run, 3 object(s) only they name, 9 mapped column(s); no part of the plan above
    orders-u1-curate-lin (lakehouse): sales.orders [origin], sales.exchange_rates [origin] -> curated.orders
    orders-u2-publish-lin (lakehouse): curated.orders -> data/orders (of flow described-land-orders)

$ sqlflow run lineage/orders-u2-publish.yaml
ERROR  a lineage description (flowType: lin) describes a step another system runs and is never executed; it is read for lineage only, so there is nothing to run, schedule or trigger.
```

After a catalog sync, the lineage graph draws the two steps when **Described steps** is switched on, and the assistant
follows them with `object_lineage(described: true)` and answers source-to-target questions with `column_mapping`.

See `docs/reference/flow/lin.md` and `docs/reference/concepts/described-lineage.md`.
