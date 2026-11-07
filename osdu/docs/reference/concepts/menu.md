---
id: delivery-concept-menu
title: "The menu: where each page of OSDU Delivery sits in the side bar, the activity bar and the command palette"
type: concept
summary: "OSDU Delivery's menu: its groups and sections, every page under them, what each group is for, the activity bar's shortcuts, and how the menu folds."
keywords:
  - menu
  - side bar
  - sidebar
  - activity bar
  - shortcuts
  - menu button
  - quick links
  - jump to a page
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
  - sqlflow/gui/src/layout/AppShell.tsx
  - sqlflow/gui/src/layout/workbench/CommandPalette.tsx
---

# The menu

OSDU Delivery lays out the workbench's whole menu: SQLFlow's pages and the ones this product adds sit side by side,
grouped by what a person does there rather than by which of the two added the page
([decision 0034](../../wiki/decisions/0034-one-menu-by-task.md)). Each group is a branch of the side bar's menu, and a
group holding more than one sort of page lists each sort under a section; the activity bar opens the pages worked in most
in one click ([below](#the-activity-bar)). The pages this product adds are described in [the GUI](gui.md); the others are
SQLFlow's.

| Group | Section | Entry | Page |
| --- | --- | --- | --- |
| Operate | | Home | `/` |
| | | Assistant | `/chat` |
| | | Runs | `/runs` |
| | | Nodes | `/nodes` |
| | | Timeline | `/schedules/timeline` |
| | Health | Data streams | `/datastreams` |
| | | Insights | `/insights` |
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
| Build | Flows | Repos | `/repos` |
| | | Pipelines | `/pipelines` |
| | | Schedules | `/schedules` |
| | Delivery | Mappings | `/delivery/documents`, a mapping's page at `/delivery/documents/{id}` (and the mapping builder, `/delivery/mappings/build`) |
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
| | | Access restrictions | `/delivery/access-restrictions` |
| | | Partitions | `/delivery/partitions` |
| Settings | | Access tokens | `/settings/tokens` |
| | | Notifications | `/settings/notifications` |

## What each group is for

- **Operate**: the day's work. Home, the assistant, every run, the workers that run them (Nodes), and when each
  schedule's flows ran. **Health** is whether data keeps arriving (Data streams), how the flows perform (Insights), and
  the room the database takes (Maintenance).
- **Ledger**: what was delivered, flow by flow (Delivery) and record by record (Records), the removals that write to it,
  who did what (Audit trail), and the ledgers whose flow is gone.
- **OSDU**: what OSDU holds, read live (Explorer, which also searches by the source columns of Search terms), the checks
  of it (Assertions, Inventories), and the tables built from it (Dimensions).
- **Build**: what is delivered, how and from where, all of it kept in the repositories. **Flows** are the repositories,
  their pipelines and their schedules. **Delivery** is what a delivered record is built from (the mapping, the template
  it pins, the cache it reads) and the order records reach OSDU in. **Sources** are the connections and acquisition flows
  the pipelines read, and the tools that look at a source before a flow is written for it.
- **Explore**: the data the flows read and write and how it connects: the catalog, its lineage, the reports and apps that
  read it (Subscribers), changes to its definitions, and the search over all of it.
- **Admin**, shown to an admin only: every page an admin runs ([decision 0048](../../wiki/decisions/0048-admin-holds-admin-pages.md)).
  Who and what may act: the users, the external systems that may fire schedules, and what the assistant may read; the
  groups a record's access can be restricted to (Access restrictions); and the partitions the flows deliver to
  (Partitions). Anyone signed in can still open the last two by their address, and Partitions from the title bar's
  partition picker; only an admin changes them.
- **Settings**: the person's own access tokens and notifications, which the account menu also opens.

No flow is configured from the menu. Flows of every kind are written in their repository's files, and synced and
scheduled under **Build**, **Flows**; a flow's own page is reached through **Pipelines**.

## The activity bar

The activity bar, the narrow bar down the workbench's left edge, is a menu button and seven shortcuts
([decision 0045](../../wiki/decisions/0045-activity-bar-shortcuts.md)). The menu button shows the side bar, with the page
shown selected in its menu and unfolded into view, or hides it. Each shortcut opens its page in one click, from the top: the
assistant, the delivery overview, the repositories and their schedules, the Explorer, the mappings and the assertions.

| Shortcut | Page | Listed under |
| --- | --- | --- |
| Assistant | `/chat` | Operate |
| Delivery | `/delivery` | Ledger |
| Repos | `/repos` | Build, Flows |
| Schedules | `/schedules` | Build, Flows |
| Explorer | `/delivery/explorer` | OSDU |
| Mappings | `/delivery/documents` | Build, Delivery |
| Assertions | `/delivery/assertions` | OSDU |

A shortcut is lit while its page is shown, or a page reached from it (a mapping's page and the mapping builder light
Mappings), and its tooltip names the page. With the side bar shown, a shortcut also selects its entry in the menu, unfolding
its group and section and scrolling it into view; with the side bar hidden, it opens the page and the side bar stays hidden.
Every other page is in the side bar and the command palette.

## How the menu behaves

The side bar's menu is a tree, like the catalog's: each group a branch, each section a branch under its group, and each
entry a leaf, every level indented under the one above with a guide line down its side. A click on a group's or a
section's name folds what is under it or brings it back, and the browser keeps each as it was left. The first time the
workbench opens, the side bar is closed and the menu folded to its groups' names; the menu button opens the side bar on
the page shown, its group unfolded, and of that group's sections only the page's own.

The menu is one stop for Tab: it lands on the page shown, the arrow keys move through it (left and right fold and unfold
a branch), and Enter opens an entry's page. The command palette (Ctrl+K) lists the same entries whatever is folded, each
part headed by its group, and a section's by its group and section (`Operate · Health`, `Build · Flows`).

The mapping builder has no entry of its own: it is opened from **Mappings**, which stays lit while it is shown.
