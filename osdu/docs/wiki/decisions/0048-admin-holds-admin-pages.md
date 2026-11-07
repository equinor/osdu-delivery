---
id: delivery-wiki-0048-admin-holds-admin-pages
title: "Decision 0048: every page an admin runs is in the menu's Admin group"
type: decision
summary: "Why Access restrictions and Partitions moved from the OSDU group to Admin, and what that leaves a person who is not an admin."
keywords:
  - "menu"
  - "admin group"
  - "access restrictions"
  - "partitions"
  - "side bar"
sourceRefs:
  - osdu/gui/src/module.tsx
  - osdu/gui/e2e/02-navigation.spec.ts
  - osdu/gui/src/features/delivery/PartitionSwitcher.tsx
referenceRefs:
  - delivery-concept-menu
  - delivery-concept-gui
related:
  - delivery-wiki-0034-one-menu-by-task
updated: 2026-11-07
---
# 0048: Every page an admin runs is in the menu's Admin group

Status: accepted. Amends [0034](0034-one-menu-by-task.md), whose groups otherwise stand. Documented in
[the menu](../../reference/concepts/menu.md) and [the OSDU pages of the workbench](../../reference/concepts/gui.md#admin).

## Context

0034 put Partitions in the OSDU group beside what OSDU holds, and Access restrictions joined it there when restricting
access was built. Both pages are registries only an admin changes: an admin describes, makes default or removes a
partition the configuration does not name, and registers, describes or removes an access restriction. The Admin group
held only SQLFlow's admin pages (Users, External triggers, AI context). The owner asked that Access restrictions move to
Admin and that every admin page be under it.

## Decision

The Admin group lists every page whose work is an admin's: Users, External triggers and AI context, then Access
restrictions and Partitions. The OSDU group keeps what OSDU holds and the checks of it (Explorer, Search terms,
Assertions, Inventories, Dimensions). Maintenance stays under Operate's Health: it is how much room the database takes,
which an operator reads, and only its purges are an admin's.

The Admin group is shown to an admin only (`requiresScope: "admin"`), so a person who is not an admin no longer finds the
two pages in the side bar. Neither page is closed to them: both stay open to anyone signed in at their address, the
partition picker's **Partitions** item still opens the registry, and what a non-admin needs from a restriction (which
records carry it) is on the records themselves.

## Rejected

- **Show each admin entry to admins only, wherever its group is.** A menu entry has no scope of its own in SQLFlow's
  layout (0034 rejected per-entry scopes), and it would leave admin pages scattered over the groups, which is what the
  owner asked to end.
- **Keep the pages in OSDU and add Admin links to them.** 0034 lists every entry exactly once.

## Consequences

- An admin finds every registry and every page that grants or limits access in one group.
- A person who is not an admin reaches Partitions from the title bar and Access restrictions by its address, not from the
  side bar.
