---
id: delivery-wiki-0047-designs-picked-per-person
title: "Decision 0047: Equinor's design beside SQLFlow's, picked by each person and kept in their browser"
type: decision
summary: "Why the Equinor design is each person's choice rather than a deployment setting, why EDS values are copied, and why its font and logo stay on its service."
keywords:
  - "design"
  - "theme"
  - "equinor design"
  - "equinor design system"
  - "branding"
  - "dark mode"
  - "light mode"
  - "fork"
sourceRefs:
  - osdu/gui/src/module.tsx
  - osdu/gui/src/theme/equinor.css
  - osdu/gui/unit/equinorDesign.test.ts
  - osdu/gui/e2e/35-designs.spec.ts
  - sqlflow/gui/src/theme/designs.ts
  - sqlflow/gui/src/theme/ThemeModeContext.tsx
  - sqlflow/gui/src/layout/workbench/AppearanceControl.tsx
referenceRefs:
  - delivery-concept-gui-designs
related:
  - delivery-wiki-0045-activity-bar-shortcuts
updated: 2026-11-06
---
# 0047: Equinor's design beside SQLFlow's, picked by each person and kept in their browser

Status: accepted. Documented in [the designs](../../reference/concepts/gui-designs.md) and, for the extension point, in
SQLFlow's `gui/DESIGN.md` (3.7, Designs) and `GuiModule.designs`.

## Context

Equinor asked for its own branding on the application: the workbench's stylesheet adapted to the Equinor Design System,
in its light and its dark mode, picked from a design menu, with SQLFlow's own light and dark kept as they are. The
workbench drew every colour from one set of tokens on `<html>`, keyed on a `dark` class, so a second design is a second
set of values for the same tokens rather than a restyle of its pages. The owner then asked that the theme last used be
the one the next visit opens in, and that the choice be offered on the sign-in page too, before a person is in.

## Decision

- **A choice each person makes.** The design and the mode are picked in the title bar's menu or on the sign-in page, and
  the browser keeps both. The first proposal made the offered designs and the default a deployment's setting in
  `config.json`. The owner turned it down: other companies run the product from forks, and a deployment's setting would
  be one more place where a fork's configuration and the main repository's conflict. A choice in the person's browser
  needs nothing from any deployment.
- **A first visit opens in Equinor dark**, the owner's choice of default. It is the module's (`firstMode`), so SQLFlow's
  own build still opens in its design, in the operating system's mode. The appearance a person wore is kept from the first
  visit on, which is what lets `index.html` put it on before the page first paints, so no other design flashes first.
- **The Equinor Design System's values, copied into one stylesheet** (`@equinor/eds-tokens` 2.3.2, each beside its EDS
  token), rather than EDS's own stylesheet imported and referenced. That stylesheet keys its dark values on its own
  `data-color-scheme` attribute and on the operating system's preference, not on the workbench's `dark` class, so the
  sign-in page and the home page's opening, dark panels in a light page, would have taken its light values, and an OS in
  dark mode would have turned a light page's tokens dark. Copied values also let the unit suite measure every text and
  status pair, and EDS's next major renames its variables, which this one file then follows.
- **Equinor's typeface and logo stay on Equinor's service** (`cdn.eds.equinor.com`), fetched by the browser only while
  the Equinor design is worn. The repository is public; the Equinor font may be used only by Equinor and its appointed
  suppliers, and the logo is Equinor's trademark. The title bar shows the logo's symbol alone, cut from the stacked logo
  the service serves, since its wordmark is unreadable at the bar's height.
- **The look, where the owner left it to judgement** ("what looks more professional", no full compliance needed): the
  chrome is EDS's surface colour in both modes, white in light with a hairline where it meets the canvas, as EDS's own
  top bar is, rather than a dark bar on a light page; Moss Green carries action and the status bar; titles take
  Equinor's typeface while the interface stays in Inter, as EDS 2.x sets them; the sign-in field is Slate Blue with
  Mist Blue rings, Energy Red being the logo's. The chart palette stays SQLFlow's validated one.

## Rejected

- **The designs and default in a deployment's `config.json`.** The owner's objection above: forks.
- **EDS's components (`@equinor/eds-core-react`).** A second component system beside the workbench's Radix and Tailwind
  primitives, which would mean rewriting every page and would break SQLFlow's design, which is kept.
- **EDS's experimental Tailwind theme.** Unpublished, colours only, and named for EDS's tokens rather than the
  workbench's.
- **The choice kept for the person's account.** It would follow them from computer to computer, but it needs a table and a
  route in the control plane, and every other choice the workbench remembers (the partition, the side bar, the tabs) is
  the browser's.
- **The sign-in page following the mode.** SQLFlow sets it on its brand field in both modes as the product's first
  impression; its menu names the mode the workbench's instead.

## Consequences

- A module may add designs to any SQLFlow build (`GuiModule.designs`); SQLFlow's own, with none, keeps its light and dark
  toggle and an unchanged sign-in page.
- Whatever draws on the title bar uses its tokens, never white, since a design may make the bar light; the partition
  chip did.
- A person who cannot reach Equinor's service sees the Equinor colours with titles in Inter and no mark.
- A downloaded assertion dashboard carries the design it was downloaded in, read off the page in both its modes.
- A token added to SQLFlow's design is set by the Equinor stylesheet too, or SQLFlow's value shows through; the unit
  suite fails until it is.
