import { useState, type CSSProperties, type ReactNode } from "react";
import { ChevronDown, ChevronLeft, ChevronRight, Info } from "lucide-react";
import { criterionOf, evidenceGroups, figure, plain, plainProblem, shareLine, tilesOf, type Difference, type EvidenceGroup, type ReadValue } from "./checkBrief";
import { Gauge, Glyph, Tiles } from "./DashboardParts";
import { checksOf, needsLook, type CheckOutcome, type DashCheck, type DashTest, type ResultFacts, type Standing } from "./dashboardModel";
import { RESULT_STANDING, RESULT_WORDS, plural, when } from "./dashboardText";

// An open assertion (osdu/docs/reference/flow/assertion-sets.md, An open assertion): what it checks in the words of its
// document, then each check that does not hold as a card: its figures in a row (what was expected, what was found, by
// how much it missed), one bar that draws them, and, a click away, the records that fail it, gathered by why. The
// checks that hold follow, a line each: what the check tests, whole, beside its mark, and the value it found. Every
// check opens on what it did: what it tested, how it found out, what it found, and the values it read. What the
// assertion is about, and how it was checked, stand beside them, so the reading column keeps a width a line can be
// read at.

/** The reasons a check's failing records are gathered under that show before it offers the rest. */
const FIRST_REASONS = 3;

/** The records under one reason that show before it offers the rest. */
const FIRST_RECORDS = 5;

/** The checks that hold a list shows before it offers the rest. */
const FIRST_HOLDS = 12;

/** The checks that hold shown under the ones that do not, before they are folded behind one line. */
const HOLDS_BESIDE_CARDS = 3;

/** The values a check read that show before it offers the rest. */
const FIRST_VALUES = 12;

/** The types of check that keep the values they read, so a result without them was recorded before results kept them. */
const KEEPS_VALUES = new Set(["count", "field", "aggregate", "unique", "groupBy", "knownValues", "legal", "indexed", "columns", "rowCount"]);

/** Where an open assertion is among the ones listed with it, and how the reader steps to the next. */
export interface BriefStep {
  at: number;
  of: number;
  /** What the assertions stepped through are: the group they are listed in. */
  among: string;
  go: (by: 1 | -1) => void;
}

/** A text as the engine wrote it, behind a line to open: for whoever looks into what the page says in other words. */
function Written({ text }: { text: string }) {
  return (
    <details className="adb-written">
      <summary>As it was written</summary>
      <code>{text}</code>
    </details>
  );
}

/** A line that stands out from the reading: why an assertion has no result, or what kept it from one. */
function Callout({ standing, lead, children, written, testId }: { standing: Standing; lead?: string; children: ReactNode; written?: string | null; testId?: string }) {
  return (
    <div className={`adb-reason adb-edge-${standing}`} data-testid={testId}>
      <Glyph standing={standing} size={14} />
      <div>
        <p>{lead !== undefined && <b>{lead} </b>}{children}</p>
        {written !== undefined && written !== null && <Written text={written} />}
      </div>
    </div>
  );
}

/** A reason as a line of its own: one that starts as the rest of a sentence about its record starts with a capital. */
function asLine(reason: string): string {
  return /^[a-z]+ /.test(reason) ? `${reason.charAt(0).toUpperCase()}${reason.slice(1)}` : reason;
}

/** The values a record differs in: one a line, what was expected, what was found and how far apart the two are. */
function Differences({ differences }: { differences: Difference[] }) {
  return (
    <div className="adb-diffs" role="table" aria-label="The values it differs in">
      <div className="adb-diffs-head" role="row">
        <span role="columnheader">Value</span>
        <span role="columnheader">Expected</span>
        <span role="columnheader">Found</span>
        <span role="columnheader">Off by</span>
      </div>
      {differences.map((difference, at) => (
        <div key={`${difference.what}-${at}`} className="adb-diff" role="row">
          <span role="cell" className="adb-diff-what">{difference.what}</span>
          <span role="cell" className="adb-diff-num">{difference.expected ?? ""}</span>
          {difference.off === null && difference.foundNote === null ? (
            <span role="cell" className="adb-diff-said" title={difference.foundExact ?? undefined}>{difference.found ?? ""}</span>
          ) : (
            <>
              <span role="cell" className="adb-diff-num adb-diff-found" title={difference.foundExact ?? undefined}>
                {difference.found ?? ""}
                {difference.foundNote !== null && <small>{difference.foundNote}</small>}
              </span>
              <span role="cell" className="adb-diff-num adb-diff-off">{difference.off ?? ""}</span>
            </>
          )}
          {difference.note !== null && <span role="cell" className="adb-diff-note">{difference.note}</span>}
        </div>
      ))}
    </div>
  );
}

/** The records that fail a check for one reason: the reason, then each record by what names it and its id. */
function Reason({ group, unit, every }: { group: EvidenceGroup; unit: [string, string]; every: boolean }) {
  const named = group.items.filter((item) => item.value !== null || item.id !== null);
  const shown = every ? named : named.slice(0, FIRST_RECORDS);
  const only = group.items.length === 1 && group.items[0]!.value !== null ? group.items[0]! : null;
  if (only !== null) {
    // One record with a name of its own: the record, then what is wrong with it.
    return (
      <li className="adb-proof">
        <p className="adb-proof-what">
          <span className="adb-proof-value">{only.value}</span>
          {only.id !== null && <code>{only.id}</code>}
        </p>
        <p className="adb-proof-why">{asLine(group.reason)}</p>
        {group.differences.length > 0 && <Differences differences={group.differences} />}
      </li>
    );
  }

  return (
    <li className="adb-proof">
      <p className="adb-proof-why">
        {asLine(group.reason)}
        {group.items.length > 1 && <span className="adb-proof-n">{plural(group.items.length, unit[0], unit[1])}</span>}
      </p>
      {shown.length > 0 && (
        <ul className="adb-proof-items">
          {shown.map((item, at) => (
            <li key={`${item.id ?? item.value ?? ""}-${at}`}>
              {item.value !== null && <span className="adb-proof-value">{item.value}</span>}
              {item.id !== null && <code>{item.id}</code>}
            </li>
          ))}
          {named.length > shown.length && <li className="adb-proof-rest">and {plural(named.length - shown.length, "more", "more")}</li>}
        </ul>
      )}
      {group.differences.length > 0 && <Differences differences={group.differences} />}
    </li>
  );
}

/**
 * The records, or rows, that fail a check, a click away: gathered by why, a reason many share said once with the
 * records under it, and each value a record differs in on a line of its own.
 */
function Proofs({ check, initiallyOpen }: { check: DashCheck; initiallyOpen: boolean }) {
  const [open, setOpen] = useState(initiallyOpen);
  const [every, setEvery] = useState(false);
  const listed = check.examples.length;
  if (listed === 0) {
    return null;
  }

  const unit: [string, string] = check.measure !== null && check.measure.form === "share" ? check.measure.unit : ["record", "records"];
  const groups = evidenceGroups(check.examples);
  const more = groups.length > FIRST_REASONS || groups.some((group) => group.items.length > FIRST_RECORDS);
  const failing = check.failing !== null && check.failing > listed ? check.failing : null;
  const count = failing !== null ? `the first ${listed} of ${figure(failing)} ${unit[1]}`
    : check.examplesTrimmed ? `the first ${plural(listed, unit[0], unit[1])}, of more`
      : plural(listed, unit[0], unit[1]);
  return (
    <div className="adb-proofs" data-testid="dashboard-examples">
      <button type="button" className="adb-proofs-head" aria-expanded={open} onClick={() => setOpen((was) => !was)} data-testid="dashboard-examples-open">
        {open ? <ChevronDown size={14} aria-hidden /> : <ChevronRight size={14} aria-hidden />}
        Where it does not hold
        <span className="adb-proof-n">{count}</span>
      </button>
      {open && (
        <>
          <ul className="adb-proof-list">
            {(every ? groups : groups.slice(0, FIRST_REASONS)).map((group, at) => <Reason key={`${group.reason}-${at}`} group={group} unit={unit} every={every} />)}
          </ul>
          {more && (
            <button type="button" className="adb-link" onClick={() => setEvery((was) => !was)} data-testid="dashboard-examples-toggle">
              {every ? "Show fewer" : `Show all ${listed} listed`}
            </button>
          )}
        </>
      )}
    </div>
  );
}

/** What qualifies a check's expectation, as someone who did not write it takes it. */
function qualifierWords(qualifier: string): string {
  const band = /^warns within (.+)$/.exec(qualifier);
  return band === null ? qualifier : `a miss within ${band[1]} only warns`;
}

/**
 * The values a check read, a line each: whether it holds, what it is, what it had to be where each value has its own,
 * what was found, how many records hold it, and the first record that does. The values of rows of proof values stand
 * under the row that states them.
 */
function Values({ values, trimmed }: { values: ReadValue[]; trimmed: boolean }) {
  const [every, setEvery] = useState(false);
  const shown = every ? values : values.slice(0, FIRST_VALUES);
  // A column is there for what tells the values apart: what was read when they are of several things, what each had to
  // be when each has its own, how many records hold it when some hold it more than once, and the record it was read
  // from when they are from several. What they all share is said once, under the table.
  const named = new Set(values.map((value) => value.what)).size > 1;
  const own = values.some((value) => value.expected !== null);
  const counted = values.some((value) => value.count > 1);
  const records = new Set(values.map((value) => value.id));
  const held = records.size > 1;
  const one = records.size === 1 ? values[0]!.id : null;
  const columns = `22px${named ? " minmax(6rem, 1.1fr)" : ""}${own ? " minmax(5rem, 1fr)" : ""} minmax(5rem, 1.4fr)${counted ? " max-content" : ""}${held ? " minmax(0, 1.4fr)" : ""}`;
  return (
    <div className="adb-values-wrap">
      <div className="adb-values" role="table" aria-label="The values it read" style={{ gridTemplateColumns: columns }} data-testid="dashboard-values">
        <div className="adb-values-head" role="row">
          <span role="columnheader" aria-label="Holds" />
          {named && <span role="columnheader">Read</span>}
          {own && <span role="columnheader">Expected</span>}
          <span role="columnheader">Found</span>
          {counted && <span role="columnheader">Records</span>}
          {held && <span role="columnheader">In</span>}
        </div>
        {shown.map((value, at) => (
          <div key={`${value.of ?? ""}-${value.what}-${at}`} className="adb-value" role="row">
            {value.of !== null && (at === 0 || shown[at - 1]!.of !== value.of) && <span role="cell" className="adb-value-of">{value.of}</span>}
            <span role="cell" className="adb-value-mark" title={value.holds ? "Holds" : "Does not hold"}><Glyph standing={value.holds ? "passed" : "failed"} size={13} /></span>
            {named && <span role="cell" className="adb-value-what">{value.what}</span>}
            {own && <span role="cell" className="adb-value-expected">{value.expected ?? ""}</span>}
            {value.found !== null
              ? <span role="cell" className="adb-value-found">{value.found}</span>
              : <span role="cell" className="adb-muted" title="What a curve measures is kept only where it differs from what was expected.">not kept</span>}
            {counted && <span role="cell" className="adb-value-n">{figure(value.count)}</span>}
            {held && <span role="cell" className="adb-value-id">{value.id ?? ""}</span>}
          </div>
        ))}
      </div>
      {one !== null && <p className="adb-fine">Read from <code className="adb-mono">{one}</code></p>}
      {values.length > FIRST_VALUES && (
        <button type="button" className="adb-link" onClick={() => setEvery((was) => !was)} data-testid="dashboard-values-toggle">
          {every ? `Show the first ${FIRST_VALUES} only` : `Show all ${figure(values.length)} values`}
        </button>
      )}
      {trimmed && <p className="adb-fine">It read more values than a result keeps: these are the first {figure(values.length)}.</p>}
    </div>
  );
}

/**
 * What a check did, a click away from its line: what it tested, whole; how it found out; what it found, as its run
 * recorded it; and the values it read, each beside whether it holds.
 */
function CheckDetail({ check }: { check: DashCheck }) {
  const qualifiers = check.qualifiers.map(qualifierWords).join(" · ");
  // One value read from no one record (a count, a measure over all of them) is what it found, which is said already.
  const listed = check.values.length > 1 || check.values.some((value) => value.id !== null || value.of !== null);
  return (
    <dl className="adb-did" data-testid="dashboard-check-detail">
      <dt>It tested</dt>
      <dd>
        <span className="adb-criterion">{check.criterion}</span>
        {qualifiers !== "" && <span className="adb-fine">{qualifiers}</span>}
        {check.description !== null && <span className="adb-fine">{check.description}</span>}
      </dd>
      {check.did !== "" && (
        <>
          <dt>How</dt>
          <dd>{check.did}</dd>
        </>
      )}
      {check.recorded !== "" && (
        <>
          <dt>It found</dt>
          <dd>{check.recorded}</dd>
        </>
      )}
      {listed && (
        <>
          <dt>Values read</dt>
          <dd><Values values={check.values} trimmed={check.valuesTrimmed} /></dd>
        </>
      )}
      {check.values.length === 0 && !check.keepsValues && KEEPS_VALUES.has(check.type) && check.result !== "disabled" && (
        <>
          <dt>Values read</dt>
          <dd className="adb-fine">This result was recorded before a check kept the values it read. Run the assertion again to see them.</dd>
        </>
      )}
    </dl>
  );
}

/**
 * A check that does not hold, as a card: what it claims and the question that is, its figures in a row with the one
 * that is the news in its tone, the bar that draws them, what is left to say in a line, and the records that fail it.
 */
function CheckCard({ check, actions, openProofs }: { check: DashCheck; actions: ReactNode; openProofs: boolean }) {
  const standing = RESULT_STANDING[check.result];
  const measure = check.measure;
  const tiles = measure === null ? [] : tilesOf(measure, check.result);
  const [did, setDid] = useState(false);
  // Under its title: what it tests, whole, when the title is a name its document gives it; and what it is for, in its
  // document's words or as the question its type asks.
  const about = check.description ?? (check.name === null ? check.asks : "");
  // What was left out is said once, by the finding that counts it.
  const counted = check.foundNotes.some((note) => note.includes("left out"));
  const fine = [
    measure !== null && measure.form === "target" && measure.of !== null && measure.of !== "records" ? `measured as ${measure.of}` : null,
    ...check.qualifiers.filter((qualifier) => !(counted && qualifier.startsWith("leaving out "))).map(qualifierWords),
    ...check.foundNotes,
  ].filter((part): part is string => part !== null).join(" · ");
  return (
    <article className={`adb-check adb-edge-${standing}`} data-testid="dashboard-check">
      <header className="adb-check-head">
        <Glyph standing={standing} size={16} />
        <div className="adb-check-titles">
          <h4>{check.label}</h4>
          {check.name !== null && <p className="adb-criterion">{check.criterion}</p>}
          {about !== "" && <p>{about}</p>}
        </div>
        <span className="adb-check-word">{RESULT_WORDS[check.result]}</span>
      </header>
      <div className="adb-check-body">
        {measure !== null && tiles.length > 0 && (
          <div className="adb-measure" style={{ "--adb-tiles": tiles.length } as CSSProperties}>
            <Tiles tiles={tiles} />
            <Gauge measure={measure} result={check.result} />
          </div>
        )}
        {measure !== null && measure.form === "share" && tiles.length === 0 && <p className="adb-said adb-said-news">{shareLine(measure)}.</p>}
        {measure === null && (
          <div className="adb-ledger">
            <div>
              <p className="adb-cap">Expected</p>
              <p className="adb-said">{check.expected}</p>
            </div>
            <div>
              <p className="adb-cap">Found</p>
              <p className="adb-said">{check.found === "-" ? "Nothing: it could not be found out." : check.found}</p>
            </div>
          </div>
        )}
        {fine !== "" && <p className="adb-fine">{fine.charAt(0).toUpperCase()}{fine.slice(1)}</p>}
        {check.why !== null && <p className="adb-check-why">{check.why}</p>}
        {check.whyWritten !== null && <Written text={check.whyWritten} />}
        <Proofs check={check} initiallyOpen={openProofs} />
        <div className="adb-proofs">
          <button type="button" className="adb-proofs-head" aria-expanded={did} onClick={() => setDid((was) => !was)} data-testid="dashboard-check-did">
            {did ? <ChevronDown size={14} aria-hidden /> : <ChevronRight size={14} aria-hidden />}
            What the check did
          </button>
          {did && <CheckDetail check={check} />}
        </div>
        {actions}
      </div>
    </article>
  );
}

/**
 * A check that needs no look, in a line: its mark, what it tests, whole (under the name its document gives it, when it
 * gives one), and the value it found. The mark says that it holds, so the line does not say it again. It opens on what
 * the check did.
 */
function HoldLine({ check }: { check: DashCheck }) {
  const [open, setOpen] = useState(false);
  const found = check.result === "disabled" ? `Disabled${check.why === null ? "" : `: ${check.why}`}` : check.value;
  return (
    <li className="adb-hold-item" data-testid="dashboard-hold">
      <button type="button" className={`adb-hold${check.name === null ? " adb-hold-plain" : ""}`} aria-expanded={open} onClick={() => setOpen((was) => !was)} title="What the check did">
        <Glyph standing={RESULT_STANDING[check.result]} size={14} />
        {check.name !== null && <span className="adb-hold-label">{check.name}</span>}
        <span className="adb-hold-criterion">{check.criterion}</span>
        <span className="adb-hold-found" title={found}>{found}</span>
        {open ? <ChevronDown size={14} aria-hidden /> : <ChevronRight size={14} aria-hidden />}
      </button>
      {open && <CheckDetail check={check} />}
    </li>
  );
}

/**
 * How an assertion's checks came out, in a line: how many of those judged hold. A check only noted (one of info severity
 * that does not hold) and a disabled one are counted beside them, since neither is held against the assertion.
 */
function headline(checks: readonly DashCheck[]): string {
  const judged = checks.filter((check) => check.result !== "disabled" && check.result !== "noted");
  const held = judged.filter((check) => check.result === "passed").length;
  const noted = checks.filter((check) => check.result === "noted").length;
  const disabled = checks.length - judged.length - noted;
  const aside = `${noted === 0 ? "" : `, ${noted} noted`}${disabled === 0 ? "" : `, ${disabled} disabled`}`;
  if (judged.length === 0) {
    return noted > 0 ? `${plural(noted, "check")} noted${disabled === 0 ? "" : `, ${disabled} disabled`}`
      : disabled === 1 ? "Its check is disabled" : `All ${disabled} checks are disabled`;
  }

  if (held === judged.length) {
    return `${judged.length === 1 ? "Its check holds" : `All ${judged.length} checks hold`}${aside}`;
  }

  if (judged.length === 1) {
    const result = judged[0]!.result;
    return `Its check ${result === "warned" ? "warns" : result === "errored" ? "could not be made" : "does not hold"}${aside}`;
  }

  return `${held} of ${plural(judged.length, "check")} hold${aside}`;
}

/** An assertion's checks as its result found them: the ones that do not hold as cards, then the rest, a line each. */
function Checks({ test, checks, outcomes, checkActions }: {
  test: DashTest;
  checks: DashCheck[];
  outcomes: CheckOutcome[];
  checkActions: ((test: DashTest, check: CheckOutcome) => ReactNode) | undefined;
}) {
  const [folded, setFolded] = useState(true);
  const [every, setEvery] = useState(false);
  const cards = checks.map((check, at) => ({ check, at })).filter(({ check }) => needsLook(check) || check.result === "noted");
  const quiet = checks.filter((check) => !needsLook(check) && check.result !== "noted");
  const fold = cards.length > 0 && quiet.length > HOLDS_BESIDE_CARDS && folded;
  const lines = every ? quiet : quiet.slice(0, FIRST_HOLDS);
  const others = quiet.every((check) => check.result === "passed") ? `the ${plural(quiet.length, "check")} that hold` : `the ${quiet.length} other checks`;
  return (
    <section className="adb-brief-checks" data-testid="dashboard-checks">
      <div className="adb-brief-head">
        <h3>{headline(checks)}</h3>
        {checks.length > 1 && checks.length <= 60 && (
          <span className="adb-pips" aria-hidden>
            {checks.map((check, at) => <i key={`${check.label}-${at}`} className={`adb-pip-${RESULT_STANDING[check.result]}`} title={`${check.label}: ${RESULT_WORDS[check.result].toLowerCase()}`} />)}
          </span>
        )}
      </div>
      {cards.map(({ check, at }) => (
        <CheckCard
          key={`${check.label}-${at}`}
          check={check}
          // The records that fail the one check that does not hold are the next thing asked for; of several, a click each.
          openProofs={cards.length === 1}
          actions={checkActions !== undefined && outcomes[at] !== undefined ? checkActions(test, outcomes[at]) : null}
        />
      ))}
      {quiet.length > 0 && cards.length > 0 && quiet.length > HOLDS_BESIDE_CARDS && (
        <button type="button" className="adb-link" aria-expanded={!folded} onClick={() => setFolded((was) => !was)} data-testid="dashboard-holds-toggle">
          {folded ? `Show ${others}` : `Hide ${others}`}
        </button>
      )}
      {quiet.length > 0 && !fold && (
        <ul className="adb-holds" data-testid="dashboard-holds">
          {lines.map((check, at) => <HoldLine key={`${check.label}-${at}`} check={check} />)}
          {quiet.length > lines.length && (
            <li>
              <button type="button" className="adb-more" onClick={() => setEvery(true)} data-testid="dashboard-holds-more">
                Show {quiet.length - lines.length} more
              </button>
            </li>
          )}
        </ul>
      )}
    </section>
  );
}

function Fact({ name, children, mono = false }: { name: string; children: ReactNode; mono?: boolean }) {
  return (
    <>
      <dt>{name}</dt>
      <dd className={mono ? "adb-mono" : undefined}>{children}</dd>
    </>
  );
}

/**
 * An open assertion: what it checks, why it stands where it does, each check that does not hold with its figures and
 * the bar that draws them, and the checks that hold; beside them what it is about, when it was checked and how.
 */
export function AssertionBrief({ test, result, failure, loading, actions, checkActions, step }: {
  test: DashTest;
  result: ResultFacts | undefined;
  failure: string | undefined;
  loading: boolean;
  actions: ((test: DashTest) => ReactNode) | undefined;
  checkActions: ((test: DashTest, check: CheckOutcome) => ReactNode) | undefined;
  step?: BriefStep;
}) {
  const checks = result === undefined ? null : checksOf(result, test.definitions);
  const waiting = checks === null && test.resultId !== null && failure === undefined && loading;
  const named = test.dimensions.filter((d) => d.name !== "Set" && d.name !== "Type" && d.name !== "Check");
  const derived = test.dimensions.filter((d) => d.name === "Set" || d.name === "Type" || d.name === "Check");
  const error = result?.error ?? null;
  // Why it could not be checked, once: its result's own account before the board's, which is the same text.
  const unchecked = test.standing === "errored" ? error ?? test.reason : null;
  const reason = test.standing === "errored" ? null : test.reason;
  const problem = unchecked === null || unchecked === "" ? null : plainProblem(unchecked);
  return (
    <div className="adb-brief" data-testid="dashboard-detail">
      <div className="adb-brief-main">
        {test.description !== null && <p className="adb-brief-lede">{test.description}</p>}
        {reason !== null && (
          <Callout standing={test.standing} lead={test.standing === "disabled" ? "Disabled:" : test.standing === "notRun" ? "Not yet checked:" : undefined} testId="dashboard-reason">
            {reason}
          </Callout>
        )}
        {problem !== null && <Callout standing="errored" lead="Could not be checked:" written={problem.written} testId="dashboard-reason">{problem.said}</Callout>}
        {test.standing !== "errored" && error !== null && error !== "" && error !== test.reason && (
          <Callout standing="errored" lead="Could not be checked:" written={plainProblem(error).written}>{plainProblem(error).said}</Callout>
        )}
        {(result?.notes ?? []).map((note) => (
          <p key={note} className="adb-note" data-testid="dashboard-note"><Info size={14} aria-hidden /><span>{plain(note)}</span></p>
        ))}
        {checks !== null && checks.length > 0 && <Checks test={test} checks={checks} outcomes={result?.assertions ?? []} checkActions={checkActions} />}
        {failure !== undefined && checks === null && <Callout standing="errored" lead="Its result could not be read:">{failure}</Callout>}
        {waiting && (
          <div className="adb-wait" role="status" data-testid="dashboard-wait">
            <span className="adb-wait-text">Reading what its checks found</span>
            <i /><i /><i />
          </div>
        )}
        {(checks === null || checks.length === 0) && !waiting && test.definitions.length > 0 && (
          <section className="adb-brief-checks" data-testid="dashboard-definitions">
            <div className="adb-brief-head">
              <h3>{test.standing === "disabled" ? "What it would check" : test.standing === "notRun" ? "What it will check" : "What it checks"}</h3>
            </div>
            <ul className="adb-holds">
              {test.definitions.map((definition, at) => {
                const { name, criterion } = criterionOf(definition);
                return (
                  <li key={`${definition.label}-${at}`} className="adb-hold-item">
                    <div className={`adb-hold adb-hold-stated${name === null ? " adb-hold-plain" : ""}`}>
                      <Glyph standing="notRun" size={14} />
                      {name !== null && <span className="adb-hold-label">{name}</span>}
                      <span className="adb-hold-criterion">{criterion}</span>
                    </div>
                  </li>
                );
              })}
            </ul>
          </section>
        )}
        {checks !== null && checks.length === 0 && error === null && test.definitions.length === 0 && (
          <p className="adb-fine" data-testid="dashboard-no-checks">Its result holds no check: the assertion states none.</p>
        )}
      </div>
      <aside className="adb-brief-side" aria-label="About this assertion">
        {(named.length > 0 || derived.length > 0) && (
          <dl className="adb-facts" data-testid="dashboard-facts">
            {[...named, ...derived].map((d) => <Fact key={d.name} name={d.name}>{d.value}</Fact>)}
          </dl>
        )}
        <dl className="adb-facts">
          {test.matched !== null && <Fact name="Records read">{test.matched.toLocaleString("en-US")}</Fact>}
          {test.checkedUtc !== null && <Fact name="Checked">{when(test.checkedUtc, true)}</Fact>}
          {test.statedAsOf !== null && <Fact name="Values stated">{test.statedAsOf}</Fact>}
          {test.validUntil !== null && <Fact name="Values hold until">{test.validUntil}</Fact>}
          <Fact name="Flow">{test.flow}</Fact>
        </dl>
        <details className="adb-how" data-testid="dashboard-how">
          <summary>How it was checked</summary>
          <dl className="adb-facts">
            <Fact name="Assertion" mono>{test.name}</Fact>
            <Fact name="Kind" mono>{test.kind}</Fact>
            {test.query !== null && <Fact name="Query" mono>{test.query}</Fact>}
            {test.template !== null && <Fact name="Template" mono>{test.template}</Fact>}
            {test.reportId !== null && <Fact name="Report" mono>#{test.reportId}</Fact>}
          </dl>
        </details>
        {actions !== undefined && <div className="adb-brief-actions">{actions(test)}</div>}
        {step !== undefined && step.of > 1 && (
          <div className="adb-step" data-testid="dashboard-step">
            <button type="button" className="adb-step-go" disabled={step.at === 0} onClick={() => step.go(-1)} title={`The one before, among ${step.among.toLowerCase()}`} aria-label="Previous assertion" data-testid="dashboard-step-previous">
              <ChevronLeft size={16} aria-hidden />
            </button>
            <span>{step.at + 1} of {step.of.toLocaleString("en-US")} in {step.among}</span>
            <button type="button" className="adb-step-go" disabled={step.at >= step.of - 1} onClick={() => step.go(1)} title={`The next one, among ${step.among.toLowerCase()}`} aria-label="Next assertion" data-testid="dashboard-step-next">
              <ChevronRight size={16} aria-hidden />
            </button>
          </div>
        )}
      </aside>
    </div>
  );
}
