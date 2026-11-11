import type { ReactNode } from "react";
import { CircleCheck, CircleDashed, CircleSlash, CircleX, OctagonAlert, TriangleAlert, type LucideIcon } from "lucide-react";
import { figure, shareLine, type CheckResult, type Measure, type ShareMeasure, type TargetMeasure, type Tile } from "./checkBrief";
import { STANDINGS, STANDING_WORDS, type Standing } from "./dashboardModel";

// The marks the assertion dashboard draws a standing and a measure with (osdu/docs/reference/flow/assertion-sets.md, The
// dashboard and An open assertion). A standing never reads by colour alone: each has a glyph of its own shape, and a
// measure is always beside the figures it draws.

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

/**
 * How some assertions stand, as a ring: an arc a standing, as long as its count, in the order the dashboard weighs
 * them, with what the ring is read by (the share that passes) in its middle.
 */
export function Ring({ counts, size = 72, children }: { counts: Record<Standing, number>; size?: number; children?: ReactNode }) {
  const stroke = Math.round(size / 8);
  const radius = (size - stroke) / 2;
  const round = 2 * Math.PI * radius;
  const parts = STANDINGS.filter((standing) => counts[standing] > 0);
  const total = parts.reduce((sum, standing) => sum + counts[standing], 0);
  // A surface gap between arcs, which a ring of one arc does not need.
  const gap = parts.length > 1 ? 2 : 0;
  // Where each arc starts along the ring: after the ones before it.
  const starts = parts.map((_, at) => parts.slice(0, at).reduce((sum, standing) => sum + (round * counts[standing]) / total, 0));
  return (
    <span className="adb-ring" style={{ width: size, height: size }}>
      <svg width={size} height={size} viewBox={`0 0 ${size} ${size}`} role="img" aria-label={parts.map((s) => `${counts[s]} ${STANDING_WORDS[s].toLowerCase()}`).join(", ") || "Nothing checked yet"}>
        <circle className="adb-ring-track" cx={size / 2} cy={size / 2} r={radius} strokeWidth={stroke} fill="none" />
        {parts.map((standing, at) => (
          <circle
            key={standing}
            className={`adb-ring-${standing}`}
            cx={size / 2}
            cy={size / 2}
            r={radius}
            strokeWidth={stroke}
            fill="none"
            strokeDasharray={`${Math.max(1, (round * counts[standing]) / total - gap)} ${round}`}
            strokeDashoffset={-starts[at]!}
            transform={`rotate(-90 ${size / 2} ${size / 2})`}
          >
            <title>{`${counts[standing]} ${STANDING_WORDS[standing].toLowerCase()}`}</title>
          </circle>
        ))}
      </svg>
      {children !== undefined && <span className="adb-ring-middle">{children}</span>}
    </span>
  );
}

/**
 * Some assertions as marks, one each in the glyph of where it stands, the ones that need a look first; past the marks
 * that fit, how many more there are.
 */
export function Marks({ standings, max = 8 }: { standings: readonly Standing[]; max?: number }) {
  const ordered = [...standings].sort((a, b) => STANDINGS.indexOf(a) - STANDINGS.indexOf(b));
  const shown = ordered.length > max ? ordered.slice(0, max - 1) : ordered;
  return (
    <span className="adb-marks" aria-hidden>
      {shown.map((standing, at) => <Glyph key={`${standing}-${at}`} standing={standing} size={13} />)}
      {ordered.length > shown.length && <span className="adb-marks-more">+{(ordered.length - shown.length).toLocaleString("en-US")}</span>}
    </span>
  );
}

/** The figures a check is read by, one beside the next, as wide as each other: the one that is the news in its tone. */
export function Tiles({ tiles }: { tiles: readonly Tile[] }) {
  return (
    <dl className="adb-tiles" data-testid="dashboard-tiles">
      {tiles.map((tile) => (
        <div key={tile.label} className={`adb-tile${tile.tone === null ? "" : ` adb-tile-${tile.tone}`}`}>
          <dt>{tile.label}</dt>
          <dd>
            {tile.lead !== null && <span className="adb-tile-lead">{tile.lead}</span>}
            <span className="adb-tile-value">{tile.value}</span>
            {tile.aside !== null && <span className="adb-tile-aside">{tile.aside}</span>}
          </dd>
        </div>
      ))}
    </dl>
  );
}

function share(of: number, whole: number): string {
  return `${Math.max(0, Math.min(100, (100 * of) / whole))}%`;
}

/** What a number measured against its range says in a line, for a hover and for a reader of the page aloud. */
function targetLine(measure: TargetMeasure): string {
  const expected = `${measure.lead === null ? "" : `${measure.lead} `}${measure.expected}${measure.trail === null ? "" : `, ${measure.trail}`}`;
  return `${figure(measure.found)} found, ${expected} expected${measure.gap === null ? "" : `: ${measure.gap}`}`;
}

/**
 * A number against what it had to be, as one bar read left to right: what was found, then what it is short by, hatched
 * as what is not there, up to what was expected; or, past what was expected, what it is over by. A tick stands where an
 * end of the range is inside the bar. Drawn from zero, so it is left out for a measure that goes below it.
 */
function TargetGauge({ measure, result, compact }: { measure: TargetMeasure; result: CheckResult; compact: boolean }) {
  const { found, low, high } = measure;
  if (found < 0 || (low ?? 0) < 0) {
    return null;
  }

  const top = Math.max(found, low ?? 0, high ?? 0);
  const scale = top === 0 ? 1 : top;
  const short = low !== null && found < low ? low - found : 0;
  const over = high !== null && found > high ? found - high : 0;
  const within = found - over;
  // A check only noted is held against nothing, so what it missed by is drawn plain.
  const tone = result === "warned" ? "warned" : result === "noted" ? "quiet" : "failed";
  // What was counted and is there holds; a measure that is not a count is neither good nor bad by itself.
  const held = result === "passed" || (measure.counts && result !== "noted") ? "passed" : "quiet";
  const ticks = [...new Set([low, high])].filter((end): end is number => end !== null && end > 0 && end < scale);
  const line = targetLine(measure);
  return (
    <span className={`adb-gauge adb-gauge-target${compact ? " adb-gauge-compact" : ""}`} role="img" aria-label={line} title={line} data-testid="dashboard-gauge">
      {within > 0 && <span className={`adb-part-${held}`} style={{ flexGrow: within }} />}
      {short > 0 && <span className={`adb-part-missing${tone === "failed" ? "" : ` adb-part-missing-${tone}`}`} style={{ flexGrow: short }} />}
      {over > 0 && <span className={`adb-part-${tone}`} style={{ flexGrow: over }} />}
      {scale - within - short - over > 0 && <span className="adb-part-none" style={{ flexGrow: scale - within - short - over }} />}
      {ticks.map((end) => <span key={end} className="adb-gauge-tick" style={{ left: share(end, scale) }} />)}
    </span>
  );
}

/** A share as one bar: a segment a part, as wide as its count, with what is not there at all hatched. */
function ShareGauge({ measure, result, compact }: { measure: ShareMeasure; result: CheckResult; compact: boolean }) {
  const line = shareLine(measure);
  const parts = measure.parts.filter((part) => part.count > 0);
  // A check only noted is held against nothing, so what does not hold of it is drawn plain.
  const plainly = result === "noted";
  return (
    <span className={`adb-gauge adb-gauge-share${compact ? " adb-gauge-compact" : ""}`} role="img" aria-label={line} title={line} data-testid="dashboard-gauge">
      {parts.map((part) => (
        <span
          key={`${part.tone}-${part.label}`}
          className={plainly && part.tone !== "passed" ? (part.tone === "missing" ? "adb-part-missing adb-part-missing-quiet" : "adb-part-quiet") : `adb-part-${part.tone}`}
          style={{ flexGrow: part.count }}
          title={`${part.label}: ${figure(part.count)}`}
        />
      ))}
    </span>
  );
}

/** The measure of a check, drawn: a number against what it had to be, or a share that holds. */
export function Gauge({ measure, result, compact = false }: { measure: Measure; result: CheckResult; compact?: boolean }) {
  return measure.form === "target"
    ? <TargetGauge measure={measure} result={result} compact={compact} />
    : <ShareGauge measure={measure} result={result} compact={compact} />;
}
