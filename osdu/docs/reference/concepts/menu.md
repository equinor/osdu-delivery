---
id: delivery-concept-menu
title: "The menu: where each page of OSDU Delivery sits in the side bar, the activity bar and the command palette"
type: concept
summary: "The workbench's menu as OSDU Delivery lays it out: its groups and sections, every page under them, what each group is for, and how the menu folds."
keywords:
  - menu
  - side bar
  - sidebar
  - activity bar
  - navigation
  - command palette
  - where is a page
  - find a page
  - menu groups
  - operate
  - health
  - ledger
  - osdu group
  - build
  - flows
  - sources
  - explore
  - admin
  - settings
related:
  - delivery-concept-gui
  - delivery-wiki-0034-one-menu-by-task
sourceRefs:
  - osdu/gui/src/module.tsx
  - sqlflow/gui/src/layout/menuLayout.ts
  - sqlflow/gui/src/layout/nav.ts
  - sqlflow/gui/src/layout/workbench/SideBar.tsx
  - sqlflow/gui/src/layout/workbench/ActivityBar.tsx
  - sqlflow/gui/src/layout/workbench/CommandPalette.tsx
---

# The menu

OSDU Delivery lays out the workbench's whole menu: SQLFlow's pages and the ones this product adds sit side by side,
grouped by what a person does there rather than by which of the two added the page
([decision 0034](../../wiki/decisions/0034-one-menu-by-task.md)). Each group is an icon of the activity bar and a branch
of the side bar's menu, and a group holding more than one sort of page lists each sort under a section. The pages this
product adds are described in [the GUI](gui.md); the others are SQLFlow's.

| Group | Section | Entry | Page |
| --- | --- | --- | --- |
| Operate | | Home | `/` |
| | | Assistant | `/chat` |
| | | Runs | `/runs` |
| | | Timeline | `/schedules/timeline` |
| | Health | Data streams | `/datastreams` |
| | | Insights | `/insights` |
| | | Nodes | `/nodes` |
| | | Maintenance | `/settings/maintenance` |
| Ledger | | Delivery | `/delivery` |
| | | Records | `/delivery/records` |
| | | Removals | `/delivery/removals` |
| | | Audit trail | `/delivery/activity` |
| | | Orphaned ledgers | `/delivery/ledgers/orphaned` |
| OSDU | | Explorer | `/delivery/explorer` |
| | | Search terms | `/delivery/search-terms` |
| | | Assertions | `/delivery/assertions` |
| | | Inventories | `/delivery/inventories` |
| | | Dimensions | `/delivery/dimensions` |
| | | Partitions | `/delivery/partitions` |
| Build | Flows | Repos | `/repos` |
| | | Pipelines | `/pipelines` |
| | | Schedules | `/schedules` |
| | Delivery | Mappings | `/delivery/documents` (and the mapping builder, `/delivery/mappings/build`) |
| | | Templates | `/delivery/templates` |
| | | Cache | `/delivery/cache` |
| | | Order | `/delivery/order` |
| | Sources | Datasources | `/datasources` |
| | | Integrations | `/integrations` |
| | | Discover | `/discover` |
| | | Key detection | `/key-detection` |
| Explore | | Catalog | `/catalog` |
| | | Lineage | `/lineage` |
| | | Subscribers | `/subscribers` |
| | | Schema changes | `/schema-changes` |
| | | Search | `/search` |
| Admin | | Users | `/users` |
| | | External triggers | `/external-triggers` |
| | | AI context | `/semantic-layer` |
| Settings | | Access tokens | `/settings/tokens` |
| | | Notifications | `/settings/notifications` |

## What each group is for

- **Operate**: the day's work. Home, the assistant, every run, and when each schedule's flows ran. **Health** is whether
  data keeps arriving (Data streams), how the flows perform (Insights), the workers that run them (Nodes), and the room
  the database takes (Maintenance).
- **Ledger**: what was delivered, flow by flow (Delivery) and record by record (Records), the removals that write to it,
  who did what (Audit trail), and the ledgers whose flow is gone.
- **OSDU**: what OSDU holds, read live (Explorer, which also searches by the source columns of Search terms), the checks
  of it (Assertions, Inventories), the tables built from it (Dimensions), and the partitions the flows deliver to.
- **Build**: what is delivered, how and from where, all of it kept in the repositories. **Flows** are the repositories,
  their pipelines and their schedules. **Delivery** is what a delivered record is built from (the mapping, the template
  it pins, the cache it reads) and the order records reach OSDU in. **Sources** are the connections and acquisition flows
  the pipelines read, and the tools that look at a source before a flow is written for it.
- **Explore**: the data the flows read and write and how it connects: the catalog, its lineage, the reports and apps that
  read it (Subscribers), changes to its definitions, and the search over all of it.
- **Admin**, shown to an admin only: who and what may act: the users, the external systems that may fire schedules, and
  what the assistant may read.
- **Settings**, at the bottom of the activity bar: the person's own access tokens and notifications.

No flow is configured from the menu. Flows of every kind are written in their repository's files, and synced and
scheduled under **Build**, **Flows**; a flow's own page is reached through **Pipelines**.

## How the menu behaves

The side bar's menu is a tree, like the catalog's: each group a branch, each section a branch under its group, and each
entry a leaf, every level indented under the one above with a guide line down its side. A click on a group's or a
section's name folds what is under it or brings it back, and the browser keeps each as it was left. The first time the
workbench opens, the side bar is closed and the menu folded to its groups' names; an activity bar icon opens the side
bar on its group, unfolded, with that group's sections folded.

The menu is one stop for Tab: it lands on the page shown, the arrow keys move through it (left and right fold and unfold
a branch), and Enter opens an entry's page. The command palette (Ctrl+K) lists the same entries whatever is folded, each
part headed by its group, and a section's by its group and section (`Operate · Health`, `Build · Flows`).

The mapping builder has no entry of its own: it is opened from **Mappings**, which stays lit while it is shown.
