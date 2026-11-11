import { CircleCheck, CircleDashed, CircleSlash, CircleX, OctagonAlert, TriangleAlert, type LucideIcon } from "lucide-react";
import { figure, heldOf, holdsAs, shareLine, type CheckResult, type Measure, type PartTone, type ShareMeasure, type TargetMeasure } from "./checkBrief";
import type { Standing } from "./dashboardModel";

// The marks the assertion dashboard draws a standing and a measure with (osdu/docs/reference/flow/assertion-sets.md, An
// open assertion). A standing never reads by colour alone: each has a glyph of its own shape, and a measure is always
// beside the figures it draws.

/** Each standing's glyph: a shape of its own, so a standing reads without its colour. */
const GLYPHS: Record<Standing, LucideIcon> = {
  failed: CircleX,
  errored: OctagonAlert,
  warned: TriangleAlert,
  notRun: CircleDashed,
  passed: CircleCheck,
  disabled: CircleSlash,
};

export function Glyph({ standing, size = 15 }: { standing: Standing; size?: number }) {
  const Icon = GLYPHS[standing];
  return <Icon className={`adb-glyph adb-tone-${standing}`} size={size} strokeWidth={2.25} aria-hidden />;
}

/** The tone a check's own number is drawn in: what it found is good, close, or off. */
const FOUND_TONE: Record<CheckResult, PartTone | "quiet"> = {
  passed: "passed",
  warned: "warned",
  failed: "failed",
  errored: "failed",
  noted: "quiet",
  disabled: "quiet",
};

function share(of: number, whole: number): string {
  return `${Math.max(0, Math.min(100, (100 * of) / whole))}%`;
}

/** What a number measured against its range says in a line, for a hover and for a reader of the page aloud. */
function targetLine(measure: TargetMeasure): string {
  const expected = `${measure.lead === null ? "" : `${measure.lead} `}${measure.expected}${measure.trail === null ? "" : `, ${measure.trail}`}`;
  return `${figure(measure.found)} found, ${expected} expected${measure.gap === null ? "" : `: ${measure.gap}`}`;
}

/**
 * A number against the range that holds, as a bullet: the bar is what was found, the shaded stretch the range that holds
 * (a tick where it is one number), and the lighter stretch beside it what only warns. Drawn from zero, so it is left out
 * for a measure that goes below it.
 */
function TargetGauge({ measure, result, compact }: { measure: TargetMeasure; result: CheckResult; compact: boolean }) {
  const { found, low, high, warnLow, warnHigh } = measure;
  if (found < 0 || (low ?? 0) < 0 || (warnLow ?? 0) < 0) {
    return null;
  }

  const top = Math.max(found, high ?? 0, low ?? 0, warnHigh ?? 0);
  const scale = top === 0 ? 1 : top * (high === null ? 1.25 : 1.08);
  const from = low ?? 0;
  const to = high ?? scale;
  const one = low !== null && high !== null && (high - low) / scale < 0.012;
  const line = targetLine(measure);
  return (
    <span className={`adb-gauge adb-gauge-target${compact ? " adb-gauge-compact" : ""}`} role="img" aria-label={line} title={line} data-testid="dashboard-gauge">
      {warnLow !== null && low !== null && warnLow < low && <span className="adb-gauge-warn" style={{ left: share(warnLow, scale), width: share(low - warnLow, scale) }} />}
      {warnHigh !== null && high !== null && warnHigh > high && <span className="adb-gauge-warn" style={{ left: share(high, scale), width: share(warnHigh - high, scale) }} />}
      {!one && <span className="adb-gauge-zone" style={{ left: share(from, scale), width: share(to - from, scale) }} />}
      <span className={`adb-gauge-found adb-part-${FOUND_TONE[result]}`} style={{ width: share(found, scale) }} />
      {one && <span className="adb-gauge-tick" style={{ left: share((low + high) / 2, scale) }} />}
    </span>
  );
}

/** A share as one bar: a segment a part, as wide as its count, with what is not there at all hatched. */
function ShareGauge({ measure, compact }: { measure: ShareMeasure; compact: boolean }) {
  const line = shareLine(measure);
  const parts = measure.parts.filter((part) => part.count > 0);
  return (
    <span className={`adb-gauge adb-gauge-share${compact ? " adb-gauge-compact" : ""}`} role="img" aria-label={line} title={line} data-testid="dashboard-gauge">
      {parts.map((part) => (
        <span key={`${part.tone}-${part.words[1]}`} className={`adb-part-${part.tone}`} style={{ flexGrow: part.count }} />
      ))}
    </span>
  );
}

/** The measure of a check, drawn: a number against its range, or a share that holds. */
export function Gauge({ measure, result, compact = false }: { measure: Measure; result: CheckResult; compact?: boolean }) {
  return measure.form === "target"
    ? <TargetGauge measure={measure} result={result} compact={compact} />
    : <ShareGauge measure={measure} compact={compact} />;
}

/** The parts of a share in words, each beside the mark it is drawn in: `3 hold`, `1 differs`, `1 is not found`. */
export function ShareLegend({ measure }: { measure: ShareMeasure }) {
  const held = heldOf(measure);
  const rest = measure.parts.filter((part) => part.tone !== "passed" && part.count > 0);
  if (rest.length <= 1 && rest.every((part) => part.words[1] === "do not")) {
    return null;
  }

  return (
    <span className="adb-legend">
      <span><i className="adb-part-passed" aria-hidden />{figure(held)} {holdsAs(measure)[held === 1 ? 0 : 1]}</span>
      {rest.map((part) => (
        <span key={`${part.tone}-${part.words[1]}`}><i className={`adb-part-${part.tone}`} aria-hidden />{figure(part.count)} {part.words[part.count === 1 ? 0 : 1]}</span>
      ))}
    </span>
  );
}
