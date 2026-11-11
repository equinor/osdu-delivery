import { useState, type ReactNode } from "react";
import { ChevronLeft, ChevronRight, Info } from "lucide-react";
import { figure, heldOf, holdsAs, plain, shareLine, type Evidence } from "./checkBrief";
import { Gauge, Glyph, ShareLegend } from "./DashboardParts";
import { checksOf, needsLook, type CheckOutcome, type DashCheck, type DashTest, type ResultFacts, type Standing } from "./dashboardModel";
import { RESULT_STANDING, RESULT_WORDS, plural, when } from "./dashboardText";

// An open assertion (osdu/docs/reference/flow/assertion-sets.md, An open assertion): what it checks in the words of its
// document, then each check that does not hold as a card that sets what was expected beside what was found, with the
// measure of the two drawn and the records that fail it under it, then the checks that hold, a line each. What it is
// about, and how it was checked, stand beside them, so the reading column keeps a width a line can be read at.

/** The failing records a check shows before it offers the rest. */
const FIRST_PROOFS = 3;

/** The checks that hold a list shows before it offers the rest. */
const FIRST_HOLDS = 12;

/** The checks that hold shown under the ones that do not, before they are folded behind one line. */
const HOLDS_BESIDE_CARDS = 3;

/** Where an open assertion is among the ones listed with it, and how the reader steps to the next. */
export interface BriefStep {
  at: number;
  of: number;
  /** What the assertions stepped through are: the group they are listed in. */
  among: string;
  go: (by: 1 | -1) => void;
}

/** A line that stands out from the reading: why an assertion has no result, or what kept it from one. */
function Callout({ standing, lead, children, testId }: { standing: Standing; lead?: string; children: ReactNode; testId?: string }) {
  return (
    <p className={`adb-reason adb-edge-${standing}`} data-testid={testId}>
      <Glyph standing={standing} size={14} />
      <span>{lead !== undefined && <b>{lead} </b>}{children}</span>
    </p>
  );
}

/** The records, or rows, that fail a check: the first few, each with why, and each value it differs in on a line of its own. */
function Proofs({ check }: { check: DashCheck }) {
  const [all, setAll] = useState(false);
  const listed = check.examples.length;
  if (listed === 0) {
    return null;
  }

  const failing = check.failing !== null && check.failing > listed ? check.failing : null;
  const shown = all ? check.examples : check.examples.slice(0, FIRST_PROOFS);
  return (
    <div className="adb-proofs" data-testid="dashboard-examples">
      <p className="adb-cap">
        Where it does not hold
        <span className="adb-cap-n">
          {failing !== null ? `the first ${listed} of ${figure(failing)}` : check.examplesTrimmed ? `the first ${listed}, of more` : listed}
        </span>
      </p>
      <ul>
        {shown.map((proof, at) => <Proof key={`${proof.id ?? ""}-${at}`} proof={proof} />)}
      </ul>
      {listed > FIRST_PROOFS && (
        <button type="button" className="adb-link" onClick={() => setAll((was) => !was)} data-testid="dashboard-examples-toggle">
          {all ? `Show the first ${FIRST_PROOFS} only` : `Show all ${listed} listed`}
        </button>
      )}
    </div>
  );
}

function Proof({ proof }: { proof: Evidence }) {
  return (
    <li className="adb-proof">
      {(proof.value !== null || proof.id !== null) && (
        <p className="adb-proof-what">
          {proof.value !== null && <span className="adb-proof-value">{proof.value}</span>}
          {proof.id !== null && <code>{proof.id}</code>}
        </p>
      )}
      <p className="adb-proof-why">{proof.reason}</p>
      {proof.differences.length > 0 && (
        <div className="adb-diffs" role="table" aria-label="The values it differs in">
          <div className="adb-diffs-head" role="row">
            <span role="columnheader">Value</span>
            <span role="columnheader">Found</span>
            <span role="columnheader">Expected</span>
          </div>
          {proof.differences.map((difference, at) => (
            <div key={`${difference.what}-${at}`} className="adb-diff" role="row">
              <span role="cell" className={difference.found === null ? "adb-diff-wide" : undefined}>{difference.what}</span>
              {difference.found !== null && <span role="cell" className="adb-diff-found">{difference.found}</span>}
              <span role="cell">{difference.expected ?? ""}</span>
              {difference.note !== null && <span role="cell" className="adb-diff-note">{difference.note}</span>}
            </div>
          ))}
        </div>
      )}
    </li>
  );
}

/**
 * A check that does not hold, as a card: what it claims and the question that is, what was expected beside what was
 * found, each as a figure where it is a number, the measure of the two, why, and the records that fail it.
 */
function CheckCard({ check, actions }: { check: DashCheck; actions: ReactNode }) {
  const standing = RESULT_STANDING[check.result];
  const target = check.measure !== null && check.measure.form === "target" ? check.measure : null;
  const share = check.measure !== null && check.measure.form === "share" ? check.measure : null;
  const about = check.description ?? check.asks;
  return (
    <article className={`adb-check adb-edge-${standing}`} data-testid="dashboard-check">
      <header className="adb-check-head">
        <Glyph standing={standing} size={16} />
        <div className="adb-check-titles">
          <h4>{check.label}</h4>
          {about !== "" && <p>{about}</p>}
        </div>
        <span className="adb-check-word">{RESULT_WORDS[check.result]}</span>
      </header>
      <div className="adb-ledger">
        <div className="adb-ledger-side">
          <p className="adb-cap">Expected</p>
          {target !== null ? (
            <p className="adb-fig">
              {target.lead !== null && <span className="adb-fig-lead">{target.lead}</span>}
              {target.expected}
              {target.trail !== null && <span className="adb-fig-trail">{target.trail}</span>}
            </p>
          ) : <p className="adb-said">{check.expected}</p>}
          {check.qualifiers.length > 0 && <p className="adb-fine">{check.qualifiers.join(" · ")}</p>}
        </div>
        <div className="adb-ledger-side">
          <p className="adb-cap">Found</p>
          {target !== null && (
            <p className="adb-fig">
              {figure(target.found)}
              {target.of !== null && <span className="adb-fig-trail">{target.of}</span>}
            </p>
          )}
          {share !== null && share.whole > 1 && (
            <p className="adb-fig">
              {figure(heldOf(share))}
              <span className="adb-fig-trail">of {figure(share.whole)} {share.unit[1]} {holdsAs(share)[1]}</span>
            </p>
          )}
          {share !== null && share.whole === 1 && <p className="adb-said">{shareLine(share)}.</p>}
          {check.measure === null && <p className="adb-said">{check.found === "-" ? "Nothing: it could not be found out." : check.found}</p>}
          {check.measure !== null && <Gauge measure={check.measure} result={check.result} />}
          {target !== null && target.gap !== null && <p className="adb-gap">{target.gap}</p>}
          {share !== null && share.whole > 1 && <ShareLegend measure={share} />}
          {check.foundNotes.length > 0 && <p className="adb-fine">{check.foundNotes.join(" · ")}</p>}
        </div>
      </div>
      {check.why !== null && <p className="adb-check-why">{check.why}</p>}
      <Proofs check={check} />
      {actions}
    </article>
  );
}

/** A check that needs no look, in a line: that it holds, what it expected, and what it found. */
function HoldLine({ check }: { check: DashCheck }) {
  return (
    <li className="adb-hold" data-testid="dashboard-hold">
      <Glyph standing={RESULT_STANDING[check.result]} size={14} />
      <span className="adb-hold-label" title={check.description ?? (check.asks === "" ? undefined : check.asks)}>{check.label}</span>
      <span className="adb-hold-expected">{check.expected}</span>
      <span className="adb-hold-found">{check.result === "disabled" ? `Disabled${check.why === null ? "" : `: ${check.why}`}` : check.gist}</span>
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
        <CheckCard key={`${check.label}-${at}`} check={check} actions={checkActions !== undefined && outcomes[at] !== undefined ? checkActions(test, outcomes[at]) : null} />
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
 * An open assertion: what it checks, why it stands where it does, each check that does not hold with what it expected
 * and found, and the checks that hold; beside them what it is about, when it was checked and how.
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
  const checks = result === undefined ? null : checksOf(result);
  const waiting = checks === null && test.resultId !== null && failure === undefined && loading;
  const named = test.dimensions.filter((d) => d.name !== "Set" && d.name !== "Type" && d.name !== "Check");
  const derived = test.dimensions.filter((d) => d.name === "Set" || d.name === "Type" || d.name === "Check");
  const error = result?.error ?? null;
  return (
    <div className="adb-brief" data-testid="dashboard-detail">
      <div className="adb-brief-main">
        {test.description !== null && <p className="adb-brief-lede">{test.description}</p>}
        {test.reason !== null && (
          <Callout
            standing={test.standing}
            lead={test.standing === "disabled" ? "Disabled:" : test.standing === "notRun" ? "Not yet checked:" : test.standing === "errored" ? "Could not be checked:" : undefined}
            testId="dashboard-reason"
          >
            {test.reason}
          </Callout>
        )}
        {error !== null && error !== "" && error !== test.reason && <Callout standing="errored" lead="Could not be checked:">{error}</Callout>}
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
              {test.definitions.map((definition, at) => (
                <li key={`${definition.label}-${at}`} className="adb-hold adb-hold-stated">
                  <Glyph standing="notRun" size={14} />
                  <span className="adb-hold-label">{plain(definition.label)}</span>
                  <span className="adb-hold-expected">{plain(definition.expected)}</span>
                </li>
              ))}
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
