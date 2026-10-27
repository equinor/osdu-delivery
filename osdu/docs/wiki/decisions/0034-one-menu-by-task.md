---
id: delivery-wiki-0034-one-menu-by-task
title: "Decision 0034: the module lays out the whole menu, SQLFlow's pages and its own, by what a person does there"
type: decision
summary: "Why OSDU Delivery's menu is one layout of every page grouped by task, not SQLFlow's groups plus an OSDU group, and why every entry must be placed."
keywords:
  - "menu"
  - "side bar"
  - "activity bar"
  - "navigation groups"
  - "menu layout"
  - "subscribers"
  - "osdu group"
sourceRefs:
  - osdu/gui/src/module.tsx
  - osdu/gui/e2e/02-navigation.spec.ts
  - sqlflow/gui/src/layout/menuLayout.ts
  - sqlflow/gui/src/layout/nav.ts
  - sqlflow/gui/src/modules/registry.ts
  - sqlflow/gui/unit/menuLayout.test.ts
referenceRefs:
  - delivery-concept-menu
  - delivery-concept-gui
updated: 2026-10-27
---
# 0034: The module lays out the whole menu, SQLFlow's pages and its own, by what a person does there

Status: accepted. Documented in [the menu](../../reference/concepts/menu.md) and, for the extension point, in SQLFlow's
`gui/DESIGN.md` (the side bar) and `GuiModule.menu`.

## Context

The menu was SQLFlow's six groups (Operate, Workspace, Tools, Explore, Admin and Settings) with one group of the module's
own, OSDU, placed after Workspace and split into Ledger, In OSDU, Build and Setup. The module kept everything it added in
that one group so that what was OSDU and what was the platform stayed plain. The menu was grouped by who added a page,
and a group's name did not say what its entries were for:

- Workspace held Subscribers, the reports and apps that read the warehouse, beside the repositories, pipelines and
  schedules; nothing about a subscriber is part of a workspace. The owner raised it.
- Tools held Datasources and Integrations, the connections and acquisition flows the pipelines read, beside two
  analysis tools.
- Timeline, a chart of when runs happened, sat in Workspace beside the definitions; Runs was in Operate.
- Settings held Maintenance, the whole database's storage and retention, beside a person's own tokens and
  notifications.
- Setup held Search terms, which only the Explorer uses, and Partitions.
- What a delivered record is built from (mappings, templates, cache, order) was in the OSDU group, and the pipelines and
  schedules that deliver it in Workspace, so the OSDU group had to say that no flow is configured there.

## Decision

A module may lay out the whole menu (`GuiModule.menu`): its groups in order, each with the entries listed straight under
it and its sections, every entry named by its path. SQLFlow's build lays out nothing and keeps its own groups. OSDU
Delivery's layout groups every page by the question a person brings to it, so a group's name predicts each entry in it:

- **Operate**: the day's work (Home, Assistant, Runs, Timeline), and under **Health** whether data keeps arriving, how
  the flows perform, the workers, and the database's storage.
- **Ledger**: what was delivered, flow by flow and record by record, removals, who did what, and the ledgers left behind.
- **OSDU**: what OSDU holds read live, the Explorer's search terms beside it, the checks of it, the dimensions built from
  it, and the partitions.
- **Build**: what is delivered, all of it kept in the repositories, under **Flows** (repositories, pipelines, schedules),
  **Delivery** (mappings, templates, cache, order) and **Sources** (connections, acquisition flows, and the two tools
  that look at a source before a flow is written for it).
- **Explore**: the data the flows read and write, its lineage, its subscribers, its definitions' changes, and search.
- **Admin**: who and what may act (users, external triggers, the assistant's AI context), shown to an admin only.
- **Settings**: the person's own tokens and notifications.

A group of more than one sort of page lists each sort under a section, and a group with a single sort lists its entries
flat. The layout lists every entry exactly once and its module adds no groups and names no group on its entries.
Registration refuses an entry left out, one listed twice, a path no entry has, an empty group or section, and a top
group after a bottom one. The first of these is deliberate: a page SQLFlow adds in a later `git subtree pull` fails
the GUI at startup, naming the path, until someone decides where it belongs, rather than landing in a group chosen for
it.

## Rejected

- **Keep the OSDU group and fix SQLFlow's own groups.** It would move Subscribers and the rest in `sqlflow/`, which is
  SQLFlow's menu to change, and it keeps the split that matters here: the pipelines and schedules apart from the
  mappings they deliver with, and delivery status apart from the runs. The people using this product work across both.
- **Move SQLFlow's entries one by one** (an extension that re-places an entry into another group). The menu would be
  read from three lists (SQLFlow's groups, the module's groups, the moves), and a page added later would still land
  where SQLFlow put it. One layout is the menu as it shows.
- **Gate single entries by scope**, to put External triggers beside Schedules and AI context beside the Assistant. An
  entry has no scope of its own, only its group; Admin as who and what may act holds its three pages without it.
- **More groups** (a Tools or a System group of their own). Tools held two pages, both of which look at a source before a
  flow is written; an eighth icon on the activity bar costs every visit, and the pages fit Build's Sources.

## Consequences

- A browser that kept folded groups under the old ids finds the new groups unfolded once; from then on it keeps them as
  the person leaves them.
- Every page SQLFlow adds is a decision for this product: the startup failure names it, and the layout in
  `osdu/gui/src/module.tsx` is the one place to answer it.
- SQLFlow's own menu, Subscribers under Workspace included, ships as it does upstream.
