---
id: delivery-wiki-0041-local-demo-against-a-stand-in
title: "Decision 0041: the local demo delivers to a stand-in for OSDU, and its verdict counts only what was evaluated"
type: decision
summary: "Why the demo delivers to the e2e stand-in extended to take writes, not a live platform or a plan, and why a passed run is not a pass."
keywords:
  - local demo
  - bootstrap
  - stand-in
  - osdu stand-in
  - tour
  - hermetic
  - skipped assertion
  - index settle
  - clean machine
updated: 2026-11-01
sourceRefs:
  - osdu/gui/standin/osdu-standin.mjs
  - osdu/gui/standin/osdu-writes.mjs
  - osdu/src/SqlFlow.Delivery.Setup/DevSetupWizard.Demo.cs
  - osdu/src/SqlFlow.Delivery.Setup/DevTour.cs
  - bootstrap.ps1
  - bootstrap.sh
referenceRefs:
  - delivery-guide-local-demo
  - delivery-cli-dev
---
# 0041: The local demo delivers to a stand-in for OSDU, and its verdict counts only what was evaluated

Status: accepted. Documented in [the local demo](../../reference/guides/local-demo.md).

The human's words: "my ultimate goal is to have a script file that aftr download can build and boot strap a local test enviorment
where we can test both sqlflow and osdu delivery", "i want to create a getting started video using this setup logic. showcasing what
osdu delivery can do", "ensure that you handel all edge cases gracefully", and "We need to make sure that the first impresion is
magical".

## It delivers for real, to a stand-in on the machine

What the product does is deliver records to OSDU and prove they arrived, so the demo has to show that, on a machine with no OSDU to
reach. A `plan` needs no stand-in, but it shows a render and no delivery: no ledger entry with an OSDU version, no compare, no
assertion on what arrived.

The e2e suite's stand-in already held records, searched them and served them back, and the two sample estates deliver through only
two protocols, so it takes writes behind a switch rather than a second program being written. The C# `FakeOsduPlatform` takes writes
too and covers far more, but it is an in-process handler built to record what the contract suites assert: serving it meant moving
it out of the tests, writing a server around it and porting the search the explorer reads, for services the estates do not call.

## It stays a test double, in the open

The stand-in enforces nothing a platform enforces, and [the guide says so](../../reference/guides/local-demo.md#what-the-stand-in-is):
a delivery that passes against it has met contracts, not an operator.

## It refuses what it did not make

The demo stops on a `.sqlflow/env` that is not its own and on a catalog this checkout did not create, and `reset` runs only over its
own file. The rules on a live OSDU and on a flow's destination rule out a setup that writes anywhere a person might have pointed
it, so its environment names only the stand-in, on loopback.

## A passed run is not a pass

While this was built, every assertion flow reported `passed` having evaluated nothing: a test inside its index settle window is
skipped, the run succeeds, and the status reads `passed`. That is right for a schedule and wrong for a proof. So the tour reads the
Assertions board and passes only when every test was evaluated and passed, and it waits out the settle window rather than a demo
setting `indexSettleSeconds: 0`, so the flows stand as a real estate writes them.

## A proof is tried by tampering with what it proves

The same work showed the estate's per-curve rows checking each curve's metadata and no sample: a value changed in the DDMS went
unnoticed. Each log now has a test of its samples, and what shows it works is a sample changed and the test going red.

## What it costs

The stand-in carries a dependency on a Parquet reader, to answer the DDMS's reads of bulk data, loaded only when it takes writes. The
demo names `OsduDelivery`, `OsduData` and `SqlFlowDemo` by name, because the sample flows do, so it needs a server of its own: on a
machine that already runs OSDU Delivery it stops. A tour takes the settle window's five minutes more than its work does.
