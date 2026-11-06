---
id: delivery-concept-gui-designs
title: "The workbench's designs: SQLFlow and Equinor, light and dark, picked in the title bar or on the sign-in page"
type: concept
summary: "The GUI's two designs, Equinor and SQLFlow, each light or dark: where to pick one, what the browser remembers, and what the Equinor design loads."
keywords:
  - design
  - designs
  - theme
  - themes
  - dark mode
  - light mode
  - equinor design
  - equinor branding
  - equinor design system
  - eds
  - sqlflow design
  - change the colours
  - switch theme
  - remember the theme
  - design menu
  - sign-in page design
  - equinor font
  - equinor logo
related:
  - delivery-concept-gui
  - delivery-concept-menu
  - delivery-flow-assertion-sets
sourceRefs:
  - osdu/gui/src/module.tsx
  - osdu/gui/src/theme/equinor.css
  - osdu/gui/index.html
  - osdu/gui/src/features/delivery/assertions/dashboard/dashboardTokens.ts
  - sqlflow/gui/src/theme/designs.ts
  - sqlflow/gui/src/theme/appearance.ts
  - sqlflow/gui/src/theme/ThemeModeContext.tsx
  - sqlflow/gui/src/layout/workbench/AppearanceControl.tsx
---

# The workbench's designs

The GUI comes in two designs, each with a light and a dark mode, and each person picks the one they wear:

| Design | What it looks like |
| --- | --- |
| **Equinor** | The Equinor Design System's colours: white chrome and a grey canvas in light, EDS's near-black canvas and dark surfaces in dark, Moss Green for what a person acts on, Equinor's symbol in the title bar and Equinor's typeface for page and section titles. The sign-in page and the home page's opening are set on Slate Blue. |
| **SQLFlow** | SQLFlow's own design, as it has always been: the navy chrome, the one blue, SQLFlow's mark. |

A first visit opens in **Equinor, dark**. The pages, their layout and what they do are the same in every design; only
the colours, the corners, the title bar's mark and the titles' typeface change.

## Picking a design

- **In the workbench**: the palette button in the title bar, beside the command palette, opens a menu with the designs
  and then **Light** and **Dark**. The menu stays open while you try them, and each pick shows at once; Escape or a click
  elsewhere closes it.
- **On the sign-in page**: the same button sits in the page's top right corner. The sign-in page itself stays on its
  brand field in either mode, so its menu calls the mode **Workbench mode**: the one the workbench opens in once you are
  signed in. A design picked there shows on the sign-in page at once.

## What is remembered

The design and mode you wore last are kept in your browser, and the next visit opens in them, before the page first
paints, so no other design flashes first. A first visit's Equinor dark is kept the same way once it is shown. The choice
belongs to the browser, not to your account: another browser or another computer opens in Equinor dark until you pick
there too, and clearing the site's data starts over. Nothing about the choice reaches the control plane.

A mode picked before the designs existed is kept: such a browser opens in the Equinor design in the mode it had.

## What the Equinor design loads

Equinor's typeface and its logo are Equinor's and are never copied into this repository: the font may be used only by
Equinor and its appointed suppliers, and the logo is Equinor's trademark. The browser fetches both from the Equinor
Design System's own service, `https://cdn.eds.equinor.com`, the first time the Equinor design is worn:

| What | From |
| --- | --- |
| The typeface (Equinor, for titles) | `https://cdn.eds.equinor.com/font/eds-uprights-vf.css` and the font files it names |
| The title bar's mark | `https://cdn.eds.equinor.com/logo/equinor-logo-primary.svg`, its symbol alone, in Energy Red |

A browser that cannot reach that service (no internet, or a policy that blocks it) still shows the design's colours; the
titles fall back to Inter, the interface's typeface in every design, and the title bar shows no mark beside the product
name. The SQLFlow design loads nothing from outside the deployment.

The Equinor colours are the Equinor Design System's own (`@equinor/eds-tokens` 2.3.2), each beside the EDS token it
comes from in [the stylesheet](../../../gui/src/theme/equinor.css); every text and status colour is measured at 4.5:1
against its ground in both modes. Charts keep the workbench's validated series palette, since the design system has no
data visualisation colours of its own.

## The downloaded dashboard

A [downloaded assertion dashboard](../flow/assertion-sets.md#download-the-dashboard) opens in the design it was
downloaded in, in the mode it was downloaded in, and its own light and dark switch moves between that design's two
modes. It loads nothing, so its text is in Inter where the computer has it and its own sans-serif otherwise, whichever
design it carries.
