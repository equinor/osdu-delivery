---
id: delivery-wiki-0045-activity-bar-shortcuts
title: "Decision 0045: the activity bar opens the pages worked in most, in one click"
type: decision
summary: "Why the activity bar is a menu button and seven page shortcuts rather than an icon per menu group, and why a shortcut leaves the side bar as it was."
keywords:
  - "activity bar"
  - "shortcuts"
  - "menu button"
  - "side bar"
  - "quick links"
  - "navigation"
sourceRefs:
  - osdu/gui/src/module.tsx
  - osdu/gui/e2e/02-navigation.spec.ts
  - sqlflow/gui/src/layout/menuLayout.ts
  - sqlflow/gui/src/layout/nav.ts
  - sqlflow/gui/src/layout/AppShell.tsx
  - sqlflow/gui/src/layout/workbench/ActivityBar.tsx
  - sqlflow/gui/src/layout/workbench/SideBar.tsx
  - sqlflow/gui/unit/menuLayout.test.ts
referenceRefs:
  - delivery-concept-menu
related:
  - delivery-wiki-0034-one-menu-by-task
updated: 2026-11-04
---
# 0045: The activity bar opens the pages worked in most, in one click

Status: accepted. Amends [0034](0034-one-menu-by-task.md), whose groups stay the side bar's. Documented in
[the menu](../../reference/concepts/menu.md#the-activity-bar) and, for the extension point, in SQLFlow's `gui/DESIGN.md`
(the activity bar) and `GuiModule.menu.shortcuts`.

## Context

The activity bar was an icon per menu group (Operate, Ledger, OSDU, Build, Explore, Admin and Settings), as SQLFlow's
is. An icon only revealed its group in the side bar, which then had to be read and clicked. With the side bar closed on a
first visit and the menu folded to its headers, reaching a page took two or three clicks: the group's icon, often a
section, then the entry. The owner said the bar pointed at sections rather than at places, and that the same few pages
are reached many times a day: the assistant, the delivery overview, the repositories, the schedules, the mappings, the
Explorer and the assertions. They asked for a button straight to each, in a logical order, and for the page a button
opens to be selected in the menu's tree.

## Decision

A menu layout may name shortcuts (`MenuLayout.shortcuts`, the paths of entries it lists). When it does, the activity bar
is a menu button, which shows or hides the side bar, and then an icon per shortcut in the layout's order, each with its
entry's own icon and its label as the tooltip. OSDU Delivery names seven, in the order the owner asked for: **Assistant**,
**Delivery**, **Repos**, **Schedules**, **Explorer**, **Mappings** and **Assertions**. The bar first followed the data's
path instead (the assistant; then Repos, Mappings and Schedules, what is delivered and when; then Delivery, Explorer and
Assertions, what was delivered, what OSDU holds and whether it is right). The owner then asked for the order above, with
the delivery overview second and the mappings after the Explorer.

A shortcut opens its page in one click and is lit while that page, or one reached from it (a mapping's page, the
builder), is shown. With the side bar shown, the shortcut's entry is selected in the tree, its group and section unfold
and the row scrolls into view. With the side bar hidden, it stays hidden, and the menu button later opens it with the
page shown unfolded into view. The groups, their sections and every entry stay the side bar's and the command palette's,
as 0034 laid them out.

## Rejected

- **Keep the group icons and add the shortcuts beside them.** Fourteen icons in a 48px bar read as one column of
  look-alikes, and the group icons were the part the owner asked to go.
- **Open the side bar on every shortcut**, so the selection is always seen. A shortcut is one click to the page; opening
  the side bar takes width from the page every time, and every jump would overwrite the person's choice of showing it or
  not. The selection is there whenever they open it.
- **Keep the data's path as the order.** It was the first order shipped, because a person can predict it without
  learning it. The bar holds the pages the owner reaches most, and they named the order to reach them in.
- **Drop Assertions from the bar.** The owner considered it and kept it, last.
- **Shortcuts each person picks.** Nothing asked for it, it needs a store for each person's list, and the seven pages
  cover the work the owner described.
- **The Settings gear at the bottom.** Access tokens and notifications are in the account menu, and in the side bar's
  Settings group.

## Consequences

- The activity bar no longer reveals a group; the side bar's groups are reached through the menu button. How the groups
  and sections fold, and the browser's memory of it, are unchanged.
- A page added to the product is placed in the side bar's layout as before; it becomes a shortcut only by being named in
  `shortcuts`.
- SQLFlow's own build names no shortcuts, and its activity bar shows its groups as before.
