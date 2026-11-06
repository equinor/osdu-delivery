// The colours a downloaded assertion dashboard is drawn in (osdu/docs/reference/flow/assertion-sets.md, Download the
// dashboard): the design worn in the workbench, in both its modes, written over dashboard.css's own values.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { wornTokenStyle } from "../src/features/delivery/assertions/dashboard/dashboardTokens.ts";

describe("wornTokenStyle", () => {
  it("writes the light values on the file's root and the dark values on its dark root, in the file's token order", () => {
    const style = wornTokenStyle({
      light: { "--primary": "#206f77", "--background": " #f5f5f5 " },
      dark: { "--primary": "#8cd2da", "--radius": "0.25rem" },
    });
    assert.equal(
      style,
      "html.adb-standalone { --background: #f5f5f5; --primary: #206f77; }\n"
        + "html.adb-standalone.dark { --radius: 0.25rem; --primary: #8cd2da; }",
    );
  });

  it("takes any CSS colour notation the workbench reports", () => {
    assert.equal(
      wornTokenStyle({ light: { "--muted": "color-mix(in oklab, rgb(0 0 0 / 45%) 50%, transparent)" }, dark: {} }),
      "html.adb-standalone { --muted: color-mix(in oklab, rgb(0 0 0 / 45%) 50%, transparent); }",
    );
  });

  it("leaves out a token with no value, and a value that could close the rule or the style element", () => {
    const style = wornTokenStyle({
      light: { "--card": "", "--border": "red; } body { display: none", "--ring": "</style><script>", "--input": "/* note */" },
      dark: { "--card": "#202223" },
    });
    assert.equal(style, "html.adb-standalone.dark { --card: #202223; }");
  });

  it("writes nothing when the workbench gave no values, so the file keeps SQLFlow's", () => {
    assert.equal(wornTokenStyle({ light: {}, dark: {} }), "");
  });
});
