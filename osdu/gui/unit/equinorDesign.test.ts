// The Equinor design (src/theme/equinor.css): that it sets every token SQLFlow's design sets, in both modes, so nothing of
// SQLFlow's palette shows through it, and that its text and status colours read (DESIGN.md 3.2: 4.5:1 for text, and for a
// status colour on the 15% tint of itself a chip is filled with; 3:1 for icons).

import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { describe, it } from "node:test";

type Tokens = Record<string, string>;

function stylesheet(relative: string): string {
  return readFileSync(new URL(relative, import.meta.url), "utf8").replace(/\/\*[\s\S]*?\*\//g, "");
}

/** The custom properties of the rule whose selector list starts with `selector`. */
function block(css: string, selector: string): Tokens {
  const at = css.indexOf(selector);
  assert.ok(at >= 0, `no rule for ${selector}`);
  const open = css.indexOf("{", at);
  const body = css.slice(open + 1, css.indexOf("}", open));
  return Object.fromEntries([...body.matchAll(/(--[a-z0-9-]+)\s*:\s*([^;]+);/g)].map((match) => [match[1], match[2].trim()]));
}

const sqlflow = stylesheet("../../../sqlflow/gui/src/index.css");
const equinor = stylesheet("../src/theme/equinor.css");
const sqlflowLight = block(sqlflow, ":root {");
const sqlflowDark = block(sqlflow, ".dark {");
const light = block(equinor, ':root[data-sf-design="equinor"] {');
const dark = block(equinor, ':root[data-sf-design="equinor"].dark');

/** The tokens that hold in both modes, which the light block alone sets. */
const MODELESS = new Set(["--radius", "--brand-navy", "--brand-navy-deep", "--brand-cream", "--brand-blue"]);

const isChart = (name: string) => name.startsWith("--chart-");

function hex(tokens: Tokens, name: string): [number, number, number] {
  let value = tokens[name];
  for (let hops = 0; value !== undefined && value.startsWith("var("); hops += 1) {
    assert.ok(hops < 8, `${name} refers to itself`);
    value = tokens[value.slice(4, -1)];
  }

  assert.ok(value !== undefined && /^#[0-9a-f]{6}$/i.test(value), `${name} is a six-digit hex colour (${String(value)})`);
  return [1, 3, 5].map((at) => Number.parseInt(value.slice(at, at + 2), 16)) as [number, number, number];
}

function luminance([r, g, b]: [number, number, number]): number {
  const channel = (value: number) => {
    const share = value / 255;
    return share <= 0.03928 ? share / 12.92 : ((share + 0.055) / 1.055) ** 2.4;
  };
  return 0.2126 * channel(r) + 0.7152 * channel(g) + 0.0722 * channel(b);
}

function contrast(a: [number, number, number], b: [number, number, number]): number {
  const [high, low] = [luminance(a), luminance(b)].sort((x, y) => y - x);
  return (high + 0.05) / (low + 0.05);
}

function over(top: [number, number, number], share: number, ground: [number, number, number]): [number, number, number] {
  return top.map((value, index) => value * share + ground[index] * (1 - share)) as [number, number, number];
}

/** Text and its ground, at 4.5:1. */
const TEXT: readonly [string, string][] = [
  ["--foreground", "--background"],
  ["--foreground", "--muted"],
  ["--card-foreground", "--card"],
  ["--popover-foreground", "--popover"],
  ["--muted-foreground", "--background"],
  ["--muted-foreground", "--card"],
  ["--muted-foreground", "--muted"],
  ["--primary-foreground", "--primary"],
  ["--secondary-foreground", "--secondary"],
  ["--accent-foreground", "--accent"],
  ["--destructive-foreground", "--destructive"],
  ["--success-foreground", "--success"],
  ["--warning-foreground", "--warning"],
  ["--primary", "--card"],
  ["--primary", "--background"],
  ["--title-bar-foreground", "--title-bar"],
  ["--title-bar-subtle", "--title-bar"],
  ["--side-bar-foreground", "--side-bar"],
  ["--muted-foreground", "--side-bar"],
  ["--sidebar-accent-foreground", "--sidebar-accent"],
  ["--status-bar-foreground", "--status-bar"],
  ["--foreground", "--tab-active"],
  ["--muted-foreground", "--tab-bar"],
];

/** Icons and their ground, at 3:1. */
const ICONS: readonly [string, string][] = [
  ["--activity-bar-foreground", "--activity-bar"],
  ["--activity-bar-active", "--activity-bar"],
];

describe("the Equinor design's tokens", () => {
  it("sets every token SQLFlow's design sets in each mode, but the chart palette, which it keeps", () => {
    for (const [mode, ours, theirs] of [["light", light, sqlflowLight], ["dark", dark, sqlflowDark]] as const) {
      const missing = Object.keys(theirs).filter((name) => !isChart(name) && !(name in ours) && !(mode === "dark" && MODELESS.has(name)));
      assert.deepEqual(missing, [], `the ${mode} block leaves SQLFlow's ${missing.join(", ")} showing through`);
    }
  });

  it("sets nothing SQLFlow's stylesheet does not define", () => {
    const unknown = [...Object.keys(light), ...Object.keys(dark)].filter((name) => !(name in sqlflowLight));
    assert.deepEqual(unknown, []);
  });

  it("sets in the dark block every token the light block sets, but those that hold in both modes", () => {
    assert.deepEqual(Object.keys(light).filter((name) => !MODELESS.has(name) && !(name in dark)), []);
    assert.deepEqual(Object.keys(dark).filter((name) => !(name in light)), []);
  });
});

for (const [mode, tokens] of [["light", light], ["dark", dark]] as const) {
  describe(`the Equinor design's ${mode} mode`, () => {
    it("draws text at 4.5:1 on its ground", () => {
      for (const [text, ground] of TEXT) {
        const ratio = contrast(hex(tokens, text), hex(tokens, ground));
        assert.ok(ratio >= 4.5, `${text} on ${ground} is ${ratio.toFixed(2)}:1`);
      }
    });

    it("draws icons at 3:1 on the activity bar", () => {
      for (const [icon, ground] of ICONS) {
        const ratio = contrast(hex(tokens, icon), hex(tokens, ground));
        assert.ok(ratio >= 3, `${icon} on ${ground} is ${ratio.toFixed(2)}:1`);
      }
    });

    it("draws each status colour's text at 4.5:1 on the 15% tint of itself a chip is filled with", () => {
      const card = hex(tokens, "--card");
      for (const status of ["--success", "--warning", "--destructive", "--info"]) {
        const colour = hex(tokens, status);
        const ratio = contrast(colour, over(colour, 0.15, card));
        assert.ok(ratio >= 4.5, `${status} on its tint is ${ratio.toFixed(2)}:1`);
      }
    });
  });
}

describe("index.html", () => {
  it("puts on, before the first paint, the appearance kept under the keys SQLFlow keeps it under", () => {
    const page = readFileSync(new URL("../index.html", import.meta.url), "utf8");
    assert.ok(page.includes('localStorage.getItem("sqlflow.design")'), "the design's key");
    assert.ok(page.includes('localStorage.getItem("sqlflow.theme")'), "the mode's key");
    assert.ok(page.includes('root.setAttribute("data-sf-design", design)'), "the design's attribute");
    assert.ok(page.indexOf("<script>") < page.indexOf("</head>"), "the script runs in the head, before the body paints");
  });
});
