import { Fragment, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { ChevronDown, ChevronRight, Maximize2, Minimize2, Moon, OctagonAlert, Search, Sun, X } from "lucide-react";
import { AssertionBrief, type BriefStep } from "./AssertionBrief";
import { Gauge, Glyph, Marks, Ring } from "./DashboardParts";
import {
  GROUPS, STANDING_MEANINGS, STANDING_WORDS, axisValues, along, byMiss, defaultAxes, dimensionsOf, groupOf, inScope, matches, passRate, readingLine,
  readingOf, setsOf, square, tally, verdictOf, type AxisValue, type CheckOutcome, type DashData, type DashTest, type GroupId, type ResultFacts,
  type RowReading, type Standing,
} from "./dashboardModel";
import { plural, when } from "./dashboardText";
import "./dashboard.css";

// The assertion dashboard (osdu/docs/reference/flow/assertion-sets.md, The dashboard): one page that says where the data
// stands, for the workbench and for the file it downloads alike. It imports nothing of the workbench but a time helper, so
// the file carries the same component, and it never scrolls inside itself: the page scrolls, once.

/** The rows the map lays out at most, and the columns; the rest of a dimension's values are gathered as Other. */
const MAX_ROWS = 12;
const MAX_COLS = 7;

/** The rows a group lists before it offers more. */
const PAGE = 10;

/** The results a read asks the control plane for at most. */
const RESULTS_PER_READ = 100;

/**
 * The assertions needing attention the dashboard reads the results of at once, to list them by how much of each does
 * not hold; past it they are listed by title, and each result is read as its row comes into view.
 */
const MAX_RANKED = 200;

/** The largest gaps the summary names. */
const GAPS = 3;

/** The marks a square of the map draws, one an assertion, before it counts the rest. */
const MARKS = 6;

/** What the dashboard is being read as: the set picked (undefined for every set) and whether disabled assertions are in. */
export interface DashView {
  set: string | null | undefined;
  includeDisabled: boolean;
  tests: DashTest[];
}

export interface AssertionDashboardProps {
  data: DashData;
  /** The page's title; left out on a flow's tab, whose page names it. */
  title?: string;
  /** What the host sets beside the title: the workbench's Explain for the page. Left out of a downloaded dashboard. */
  titleAside?: ReactNode;
  /** The set picked, undefined for every set: kept by the host (the workbench in its address) when it passes onSet. */
  set: string | null | undefined;
  /** Picks a set; without it the dashboard keeps the set picked itself, starting from set. */
  onSet?: (set: string | null | undefined) => void;
  /** Whether the sets are offered to pick from: on the page of every flow, not on one flow's tab. */
  showSets?: boolean;
  initialIncludeDisabled?: boolean;
  /** Results already at hand, by result id: what a downloaded dashboard carries. */
  results?: Record<string, ResultFacts>;
  /** Reads results whole by their ids, which the workbench asks the control plane for as rows come into view. */
  loadResults?: (resultIds: readonly number[]) => Promise<{ resultId: number; result: ResultFacts }[]>;
  /** The host's actions beside Present: a run, the downloads. */
  actions?: (view: DashView) => ReactNode;
  /** The host's actions on an open assertion: run it again, open its flow. */
  testActions?: (test: DashTest) => ReactNode;
  /** The host's actions on one check of an open assertion, as its result recorded it: the T-SQL of the staged rows it read. */
  checkActions?: (test: DashTest, check: CheckOutcome) => ReactNode;
  /** True in a downloaded file: a theme switch, and a note of when it was downloaded. */
  standalone?: boolean;
  /** What shows when there is no assertion at all. */
  empty?: ReactNode;
}

/** Results whole for the result ids asked for, read a hundred at a time and each once, with why one could not be read. */
function useResults(initial: Record<string, ResultFacts> | undefined, load: AssertionDashboardProps["loadResults"], wanted: string) {
  const [results, setResults] = useState<Record<string, ResultFacts>>(initial ?? {});
  const [failures, setFailures] = useState<Record<string, string>>({});
  const asked = useRef(new Set<number>(Object.keys(initial ?? {}).map(Number)));
  useEffect(() => {
    if (load === undefined || wanted === "") {
      return;
    }

    const missing = wanted.split(",").map(Number).filter((id) => !asked.current.has(id));
    for (let at = 0; at < missing.length; at += RESULTS_PER_READ) {
      const chunk = missing.slice(at, at + RESULTS_PER_READ);
      chunk.forEach((id) => asked.current.add(id));
      load(chunk).then(
        (rows) => setResults((was) => ({ ...was, ...Object.fromEntries(rows.map((row) => [String(row.resultId), row.result])) })),
        (error: unknown) => {
          const message = error instanceof Error ? error.message : String(error);
          setFailures((was) => ({ ...was, ...Object.fromEntries(chunk.map((id) => [String(id), message])) }));
        },
      );
    }
  }, [load, wanted]);
  return { results, failures };
}

/**
 * The assertion dashboard: the verdict and the figures, the map of where the data stands by two dimensions, and the
 * assertions grouped by what they need, each opening on its checks. Disabled assertions are left out until the reader
 * includes them, and then say why each is disabled.
 */
export function AssertionDashboard(props: AssertionDashboardProps) {
  const { data, title, onSet, showSets = false, actions, testActions, checkActions, standalone = false } = props;
  const [ownSet, setOwnSet] = useState(props.set);
  const set = onSet === undefined ? ownSet : props.set;
  const pickSet = onSet ?? setOwnSet;
  const root = useRef<HTMLDivElement>(null);
  const [includeDisabled, setIncludeDisabled] = useState(props.initialIncludeDisabled ?? false);
  const [axes, setAxes] = useState<{ row: string | null; col: string | null } | null>(null);
  const [cell, setCell] = useState<{ row: string; col: string | null } | null>(null);
  const [only, setOnly] = useState<Standing | null>(null);
  const [term, setTerm] = useState("");
  const [open, setOpen] = useState<string | null>(null);
  const [groupsOpen, setGroupsOpen] = useState<Partial<Record<GroupId, boolean>>>({});
  const [shown, setShown] = useState<Partial<Record<GroupId, number>>>({});
  const [presenting, setPresenting] = useState(false);
  const [dark, setDark] = useState(() => standalone && document.documentElement.classList.contains("dark"));

  useEffect(() => {
    const changed = () => setPresenting(document.fullscreenElement === root.current && root.current !== null);
    document.addEventListener("fullscreenchange", changed);
    return () => document.removeEventListener("fullscreenchange", changed);
  }, []);

  const sets = useMemo(() => setsOf(data, includeDisabled), [data, includeDisabled]);
  const scoped = useMemo(() => inScope(data, set, includeDisabled), [data, includeDisabled, set]);
  const disabledHere = useMemo(() => inScope(data, set, true).length - inScope(data, set, false).length, [data, set]);
  const dims = useMemo(() => dimensionsOf(scoped).filter((d) => d.values >= 2), [scoped]);
  const fallback = useMemo(() => defaultAxes(scoped), [scoped]);
  // The axes the reader picked while the assertions in scope have them, else the ones the dashboard picks itself.
  const valid = (name: string | null) => name !== null && dims.some((d) => d.name === name);
  const rowDim = axes !== null && valid(axes.row) ? axes.row : fallback.row;
  const colDim = axes === null ? (fallback.col !== rowDim ? fallback.col : null) : valid(axes.col) && axes.col !== rowDim ? axes.col : null;
  const rows = useMemo(() => (rowDim === null ? [] : axisValues(scoped, rowDim, MAX_ROWS)), [rowDim, scoped]);
  const cols = useMemo(() => (colDim === null || colDim === rowDim ? [] : axisValues(scoped, colDim, MAX_COLS)), [colDim, rowDim, scoped]);
  const verdict = useMemo(() => verdictOf(scoped, rows, rowDim, cols, colDim), [cols, colDim, rowDim, rows, scoped]);
  const n = useMemo(() => tally(scoped), [scoped]);

  const cellRow = cell === null ? undefined : rows.find((r) => r.label === cell.row);
  const cellCol = cell === null || cell.col === null ? undefined : cols.find((c) => c.label === cell.col);
  const listed = useMemo(() => scoped.filter((test) =>
    (cellRow === undefined || rowDim === null || along(test, rowDim, cellRow))
    && (cellCol === undefined || colDim === null || along(test, colDim, cellCol))
    && (only === null || test.standing === only)
    && matches(test, term)), [cellCol, cellRow, colDim, only, rowDim, scoped, term]);

  const listedGroups = useMemo(() => GROUPS
    .filter((g) => g.id !== "disabled" || includeDisabled)
    .map((g) => ({ ...g, ...groupOf(listed, g.id) }))
    .filter((g) => g.tests.length > 0), [includeDisabled, listed]);
  const narrowed = cellRow !== undefined || only !== null || term.trim() !== "";
  const isOpen = (id: GroupId, initially: boolean) => groupsOpen[id] ?? (initially || narrowed || listedGroups.length === 1);

  // Every assertion in scope that needs attention, whatever the list is narrowed to: what the summary names the largest
  // gaps of, and what the list is ordered by.
  const attention = useMemo(() => groupOf(scoped, "attention").tests, [scoped]);
  const rankable = attention.length <= MAX_RANKED;

  // The results the dashboard reads: of every assertion needing attention while they can be ranked, else of the rows in
  // view; and of the open assertion.
  const wanted = useMemo(() => {
    const ids = new Set<number>();
    const inView = listedGroups.find((g) => g.id === "attention");
    if (rankable) {
      attention.forEach((t) => t.resultId !== null && ids.add(t.resultId));
    } else if (inView !== undefined && (groupsOpen.attention ?? true)) {
      inView.tests.slice(0, shown.attention ?? PAGE).forEach((t) => t.resultId !== null && ids.add(t.resultId));
    }

    const opened = scoped.find((t) => t.key === open);
    if (opened !== undefined && opened.resultId !== null) {
      ids.add(opened.resultId);
    }

    return [...ids].sort((a, b) => a - b).join(",");
  }, [attention, groupsOpen.attention, listedGroups, open, rankable, scoped, shown.attention]);
  const { results, failures } = useResults(props.results, props.loadResults, wanted);

  // Ranked once every result is at hand (or could not be read), so the rows change places once and not as each arrives.
  const settled = rankable && attention.every((t) => t.resultId === null || results[String(t.resultId)] !== undefined || failures[String(t.resultId)] !== undefined);
  const ranked = useMemo(() => (settled ? byMiss(attention, results) : attention), [attention, results, settled]);
  const groups = useMemo(() => {
    const place = new Map(ranked.map((test, at) => [test.key, at]));
    return listedGroups.map((g) => (g.id === "attention" ? { ...g, tests: [...g.tests].sort((a, b) => place.get(a.key)! - place.get(b.key)!) } : g));
  }, [listedGroups, ranked]);
  const gaps = useMemo(() => (settled
    ? ranked.filter((t) => t.standing === "failed")
      .map((test) => ({ test, reading: readingOf(test, test.resultId === null ? undefined : results[String(test.resultId)]) }))
      .filter(({ reading }) => reading.miss > 0)
      .slice(0, GAPS)
    : []), [ranked, results, settled]);

  // Opens an assertion that needs attention wherever the list stands: nothing narrows it, its group is open, and the
  // rows listed reach it.
  const openGap = (key: string) => {
    const at = ranked.findIndex((t) => t.key === key);
    setCell(null);
    setOnly(null);
    setTerm("");
    setGroupsOpen((was) => ({ ...was, attention: true }));
    setShown((was) => ({ ...was, attention: Math.max(was.attention ?? PAGE, PAGE * Math.ceil((at + 1) / PAGE)) }));
    setOpen(key);
  };

  const present = () => {
    if (document.fullscreenElement !== null) {
      void document.exitFullscreen();
    } else {
      void root.current?.requestFullscreen();
    }
  };

  const toggleTheme = () => {
    const next = !dark;
    document.documentElement.classList.toggle("dark", next);
    setDark(next);
  };

  const view: DashView = { set, includeDisabled, tests: scoped };
  const pills = showSets && sets.length > 1;
  const problems = data.flows.filter((f) => f.problem !== null && (set === undefined || f.set === set));

  if (data.tests.length === 0 && problems.length === 0) {
    return (
      <div className="adb" ref={root} data-testid="assertion-dashboard">
        {title !== undefined && (
          <header className="adb-head">
            <div className="adb-titles">
              <h1>{title}</h1>
              {props.titleAside !== undefined && <span className="adb-title-aside">{props.titleAside}</span>}
            </div>
          </header>
        )}
        {props.empty ?? <p className="adb-empty" data-testid="dashboard-empty">No assertion checks this partition yet.</p>}
      </div>
    );
  }

  return (
    <div className={`adb${presenting ? " adb-presenting" : ""}`} ref={root} data-testid="assertion-dashboard">
      <header className="adb-head">
        <div className="adb-titles">
          {title !== undefined && <h1>{title}{set !== undefined && !pills ? <span className="adb-title-set">{set ?? "No set"}</span> : null}</h1>}
          {title !== undefined && props.titleAside !== undefined && <span className="adb-title-aside">{props.titleAside}</span>}
          {pills && (
            <nav className="adb-sets" aria-label="Sets">
              <button type="button" className="adb-set" aria-pressed={set === undefined} onClick={() => pickSet(undefined)} data-testid="dashboard-set-all">
                All sets
              </button>
              {sets.map((s) => (
                <button
                  key={s.label}
                  type="button"
                  className="adb-set"
                  aria-pressed={set === s.set}
                  onClick={() => {
                    pickSet(s.set);
                    setCell(null);
                  }}
                  title={`${s.label}: ${s.n.judged === 0 ? "nothing checked yet" : `${s.n.passed} of ${s.n.judged} checked pass`}`}
                  data-testid={`dashboard-set-${s.label}`}
                >
                  <Glyph standing={s.n.total === 0 ? "notRun" : s.n.worst} size={13} />
                  {s.label}
                  <span className="adb-set-n">{s.n.judged === 0 ? "-" : `${s.n.passed}/${s.n.judged}`}</span>
                </button>
              ))}
            </nav>
          )}
        </div>
        <div className="adb-actions">
          {standalone && (
            <span className="adb-stamp" data-testid="dashboard-stamp">
              {data.partition === null ? "" : `${data.partition}, `}downloaded {when(data.generatedUtc, true)}
            </span>
          )}
          {disabledHere > 0 && (
            <label className="adb-switch" title="Disabled assertions are left out of the figures, the map and the list until they are included; each then says why it is disabled.">
              <input
                type="checkbox"
                role="switch"
                checked={includeDisabled}
                onChange={(e) => {
                  setIncludeDisabled(e.target.checked);
                  setOnly((was) => (was === "disabled" ? null : was));
                }}
                data-testid="dashboard-include-disabled"
              />
              <span className="adb-switch-track" aria-hidden><span /></span>
              Include disabled ({disabledHere})
            </label>
          )}
          {standalone && (
            <button type="button" className="adb-button" onClick={toggleTheme} aria-label={dark ? "Light theme" : "Dark theme"} data-testid="dashboard-theme">
              {dark ? <Sun size={14} /> : <Moon size={14} />}
            </button>
          )}
          <button type="button" className="adb-button" onClick={present} data-testid="dashboard-present">
            {presenting ? <Minimize2 size={14} /> : <Maximize2 size={14} />}
            {presenting ? "Exit" : "Present"}
          </button>
          {actions?.(view)}
        </div>
      </header>

      {problems.length > 0 && (
        <div className="adb-problems" role="alert" data-testid="dashboard-problems">
          <OctagonAlert size={15} className="adb-tone-errored" aria-hidden />
          <div>
            <strong>{problems.length === 1 ? "A flow cannot be read, so its assertions are not here." : `${problems.length} flows cannot be read, so their assertions are not here.`}</strong>
            <ul>
              {problems.slice(0, 3).map((f) => <li key={f.pipelineId}><b>{f.title}</b>: {f.problem}</li>)}
              {problems.length > 3 && <li>and {problems.length - 3} more.</li>}
            </ul>
          </div>
        </div>
      )}

      {scoped.length === 0 ? (
        <p className="adb-empty" data-testid="dashboard-all-disabled">
          {disabledHere > 0 ? `Every assertion here is disabled (${disabledHere}). Include disabled to see each one and why.` : "No assertion checks this partition here."}
        </p>
      ) : (
        <>
          <Summary
            verdict={verdict}
            n={n}
            includeDisabled={includeDisabled}
            only={only}
            onOnly={(s) => setOnly((was) => (was === s ? null : s))}
            gaps={gaps}
            onGap={openGap}
            checkedUtc={scoped.reduce<string | null>((last, t) => (t.checkedUtc !== null && (last === null || t.checkedUtc > last) ? t.checkedUtc : last), null)}
          />

          {rowDim !== null && (rows.length > 1 || cols.length > 1) && (
            <section className="adb-panel" data-testid="dashboard-map">
              <div className="adb-panel-head">
                <h2>Where the data stands</h2>
                <div className="adb-axes" title="Set, Type and Check every assertion has; the other dimensions are the ones its flow names.">
                  <AxisPicker
                    label="Rows"
                    value={rowDim}
                    options={dims.some((d) => d.name === rowDim) ? dims.map((d) => d.name) : [rowDim, ...dims.map((d) => d.name)]}
                    onChange={(value) => {
                      setAxes({ row: value, col: value === colDim ? null : colDim });
                      setCell(null);
                    }}
                    testId="dashboard-rows"
                  />
                  <AxisPicker
                    label="Columns"
                    value={colDim}
                    options={dims.map((d) => d.name).filter((name) => name !== rowDim)}
                    none="None"
                    onChange={(value) => {
                      setAxes({ row: rowDim, col: value });
                      setCell(null);
                    }}
                    testId="dashboard-cols"
                  />
                </div>
              </div>
              <HeatMap
                tests={scoped}
                rowDim={rowDim}
                rows={rows}
                colDim={colDim}
                cols={cols}
                picked={cell}
                onPick={(row, col) => {
                  setCell((was) => (was !== null && was.row === row && was.col === col ? null : { row, col }));
                  setOpen(null);
                }}
              />
            </section>
          )}

          <section className="adb-panel" data-testid="dashboard-list">
            <div className="adb-panel-head">
              <h2>Assertions</h2>
              <div className="adb-find">
                <label className="adb-search">
                  <Search size={14} aria-hidden />
                  <input
                    type="search"
                    value={term}
                    onChange={(e) => setTerm(e.target.value)}
                    placeholder="Find by title, flow or dimension"
                    aria-label="Find an assertion"
                    data-testid="dashboard-search"
                  />
                </label>
                {cellRow !== undefined && (
                  <button type="button" className="adb-chip adb-chip-on" onClick={() => setCell(null)} data-testid="dashboard-cell-filter">
                    {cellCol === undefined ? cellRow.label : `${cellRow.label} · ${cellCol.label}`}
                    <X size={12} aria-label="Clear" />
                  </button>
                )}
                {only !== null && (
                  <button type="button" className="adb-chip adb-chip-on" onClick={() => setOnly(null)} data-testid="dashboard-standing-filter">
                    {STANDING_WORDS[only]}
                    <X size={12} aria-label="Clear" />
                  </button>
                )}
                {narrowed && <span className="adb-count">{listed.length.toLocaleString("en-US")} of {scoped.length.toLocaleString("en-US")}</span>}
              </div>
            </div>
            {groups.length === 0 && <p className="adb-empty" data-testid="dashboard-none">No assertion matches. Clear the filters to see every one.</p>}
            {groups.map((g) => {
              const opened = isOpen(g.id, g.open);
              const limit = shown[g.id] ?? PAGE;
              const worst = g.tests[0]!.standing;
              return (
                <div key={g.id} className="adb-group" data-testid={`dashboard-group-${g.id}`}>
                  <button type="button" className="adb-group-head" aria-expanded={opened} onClick={() => setGroupsOpen((was) => ({ ...was, [g.id]: !opened }))}>
                    {opened ? <ChevronDown size={14} aria-hidden /> : <ChevronRight size={14} aria-hidden />}
                    <Glyph standing={worst} />
                    <span className="adb-group-title">{g.title}</span>
                    <span className="adb-group-n">{g.tests.length.toLocaleString("en-US")}</span>
                    {!opened && g.reasons.length > 0 && (
                      <span className="adb-group-why" title={g.reasons.map((r) => `${g.reasons.length > 1 ? `${r.count}: ` : ""}${r.reason}`).join(" · ")}>
                        {g.reasons.slice(0, 2).map((r) => `${g.reasons.length > 1 ? `${r.count}: ` : ""}${r.reason}`).join("  ·  ")}
                        {g.reasons.length > 2 && `  ·  ${g.reasons.length - 2} more reasons`}
                      </span>
                    )}
                    {!opened && g.id === "passed" && <span className="adb-group-why">Every check of each holds.</span>}
                  </button>
                  {opened && (
                    <ul className="adb-rows">
                      {g.tests.slice(0, limit).map((test, at) => (
                        <TestRow
                          key={test.key}
                          test={test}
                          result={test.resultId === null ? undefined : results[String(test.resultId)]}
                          failure={test.resultId === null ? undefined : failures[String(test.resultId)]}
                          loading={props.loadResults !== undefined}
                          open={open === test.key}
                          onToggle={() => setOpen((was) => (was === test.key ? null : test.key))}
                          actions={testActions}
                          checkActions={checkActions}
                          step={{
                            at,
                            of: g.tests.length,
                            among: g.title,
                            // The next one is listed before it opens: a step past the rows shown lists the next rows too.
                            go: (by) => {
                              const next = g.tests[at + by];
                              if (next !== undefined) {
                                setShown((was) => ({ ...was, [g.id]: Math.max(limit, PAGE * Math.ceil((at + by + 1) / PAGE)) }));
                                setOpen(next.key);
                              }
                            },
                          }}
                        />
                      ))}
                      {g.tests.length > limit && (
                        <li>
                          <button type="button" className="adb-more" onClick={() => setShown((was) => ({ ...was, [g.id]: limit + PAGE }))} data-testid="dashboard-more">
                            Show {Math.min(PAGE, g.tests.length - limit)} more of {(g.tests.length - limit).toLocaleString("en-US")} left
                          </button>
                        </li>
                      )}
                    </ul>
                  )}
                </div>
              );
            })}
          </section>
        </>
      )}
    </div>
  );
}

/**
 * The summary, read left to right: how the assertions stand as a ring with the share that passes in its middle, the
 * verdict in a sentence with what was checked under it and the largest gaps named in words, each opening its assertion,
 * and each standing's count, which narrows the list to it and says, on a hover, what the standing means: the counts are
 * the legend of the ring and of every glyph below them.
 */
function Summary({ verdict, n, includeDisabled, only, onOnly, gaps, onGap, checkedUtc }: {
  verdict: { standing: Standing; text: string };
  n: ReturnType<typeof tally>;
  includeDisabled: boolean;
  only: Standing | null;
  onOnly: (s: Standing) => void;
  gaps: { test: DashTest; reading: RowReading }[];
  onGap: (key: string) => void;
  checkedUtc: string | null;
}) {
  const rate = passRate(n);
  const figures: { standing: Standing; label: string }[] = [
    { standing: "failed", label: "Failing" },
    { standing: "errored", label: "Could not be checked" },
    { standing: "warned", label: n.warned === 1 ? "Warning" : "Warnings" },
    { standing: "notRun", label: "Not yet checked" },
    { standing: "passed", label: "Passing" },
    ...(includeDisabled ? [{ standing: "disabled" as const, label: "Disabled" }] : []),
  ];
  const checked = [
    n.judged === 0 ? "Nothing checked yet" : `${n.passed.toLocaleString("en-US")} of ${n.judged.toLocaleString("en-US")} checked pass`,
    n.notRun > 0 ? `${n.notRun.toLocaleString("en-US")} not yet checked` : null,
    checkedUtc === null ? null : `last checked ${when(checkedUtc)}`,
  ].filter((part) => part !== null).join(" · ");
  return (
    <section className={`adb-summary adb-edge-${verdict.standing}`}>
      <div className="adb-health" data-testid="dashboard-health" title="Health: the share of the assertions checked that pass. Those not yet checked, and disabled ones, are counted beside it, never in it.">
        <Ring counts={{ ...n, disabled: includeDisabled ? n.disabled : 0 }} size={80}>
          <span className="adb-health-rate">{rate === null ? "-" : `${rate}%`}</span>
          <span className="adb-health-of">pass</span>
        </Ring>
      </div>
      <div className="adb-verdict" data-testid="dashboard-verdict">
        <p className="adb-verdict-text">{verdict.text}</p>
        <p className="adb-verdict-of">{checked}</p>
        {gaps.length > 0 && (
          <div className="adb-gaps" data-testid="dashboard-gaps">
            <p className="adb-cap">{gaps.length === 1 ? "The gap" : "Largest gaps"}</p>
            <ol>
              {gaps.map(({ test, reading }) => (
                <li key={test.key}>
                  <button type="button" className="adb-gap" onClick={() => onGap(test.key)} title={`${test.title}: ${readingLine(reading)}. Click to open it.`} data-testid="dashboard-gap">
                    <Glyph standing={test.standing} size={13} />
                    <span className="adb-gap-title">{test.title}</span>
                    <span className="adb-gap-says">{reading.says}</span>
                    <span className="adb-gap-gauge">
                      {reading.measure !== null && reading.result !== null && <Gauge measure={reading.measure} result={reading.result} compact />}
                    </span>
                    <ChevronRight size={13} aria-hidden />
                  </button>
                </li>
              ))}
            </ol>
          </div>
        )}
      </div>
      <div className="adb-figures" data-testid="dashboard-figures">
        {figures.map(({ standing, label }) => {
          const count = n[standing];
          return (
            <button
              key={standing}
              type="button"
              className={`adb-figure${count === 0 ? " adb-quiet" : ""}`}
              aria-pressed={only === standing}
              disabled={count === 0}
              onClick={() => onOnly(standing)}
              title={`${STANDING_WORDS[standing]}: ${STANDING_MEANINGS[standing]}${count === 0 ? "" : " Click to list them."}`}
              data-testid={`dashboard-figure-${standing}`}
            >
              <Glyph standing={standing} size={14} />
              <span className="adb-figure-n">{count.toLocaleString("en-US")}</span>
              <span className="adb-figure-label">{label}</span>
            </button>
          );
        })}
      </div>
    </section>
  );
}

function AxisPicker({ label, value, options, none, onChange, testId }: {
  label: string;
  value: string | null;
  options: string[];
  none?: string;
  onChange: (value: string | null) => void;
  testId: string;
}) {
  return (
    <label className="adb-axis">
      {label}
      <select value={value ?? ""} onChange={(e) => onChange(e.target.value === "" ? null : e.target.value)} data-testid={testId}>
        {none !== undefined && <option value="">{none}</option>}
        {options.map((name) => <option key={name} value={name}>{name}</option>)}
      </select>
    </label>
  );
}

/** The map: a row per value of one dimension, a column per value of another, each square how its assertions stand. */
function HeatMap({ tests, rowDim, rows, colDim, cols, picked, onPick }: {
  tests: DashTest[];
  rowDim: string;
  rows: AxisValue[];
  colDim: string | null;
  cols: AxisValue[];
  picked: { row: string; col: string | null } | null;
  onPick: (row: string, col: string | null) => void;
}) {
  const columns = cols.length > 0 ? cols : [null];
  return (
    <div className="adb-map" style={{ gridTemplateColumns: `fit-content(12rem) repeat(${columns.length}, minmax(0, 1fr))` }} role="group" aria-label="Where the data stands">
      <div className="adb-map-corner">{colDim === null ? "" : `${rowDim} by ${colDim}`}</div>
      {columns.map((col) => <div key={col?.label ?? "all"} className="adb-map-col" title={col?.label}>{col?.label ?? "All"}</div>)}
      {rows.map((row) => {
        const inRow = tests.filter((t) => along(t, rowDim, row));
        const rowN = tally(inRow);
        return (
          <Fragment key={row.label}>
            <div className="adb-map-row" title={row.label}>
              <span className="adb-map-row-label">{row.label}</span>
              <span className="adb-map-row-n">{rowN.judged === 0 ? "-" : `${rowN.passed}/${rowN.judged}`}</span>
            </div>
            {columns.map((col) => {
              const here = col === null || colDim === null ? inRow : inRow.filter((t) => along(t, colDim, col));
              if (here.length === 0) {
                return <div key={col?.label ?? "all"} className="adb-cell adb-cell-none" title={`No assertion in ${col === null ? row.label : `${row.label} · ${col.label}`}`} />;
              }

              const sq = square(here);
              const on = picked !== null && picked.row === row.label && picked.col === (col?.label ?? null);
              const where = col === null ? row.label : `${row.label} · ${col.label}`;
              return (
                <button
                  key={col?.label ?? "all"}
                  type="button"
                  className={`adb-cell adb-cell-${sq.n.worst}`}
                  aria-pressed={on}
                  onClick={() => onPick(row.label, col?.label ?? null)}
                  title={`${where}: ${plural(sq.n.total, "assertion")}; ${sq.n.judged === 0 ? "none checked yet" : `${sq.n.passed} of ${sq.n.judged} checked pass`}${sq.n.failed > 0 ? `, ${sq.n.failed} failing` : ""}${sq.n.errored > 0 ? `, ${sq.n.errored} could not be checked` : ""}${sq.n.warned > 0 ? `, ${plural(sq.n.warned, "warning")}` : ""}${sq.n.notRun > 0 ? `, ${sq.n.notRun} not yet checked` : ""}. Click to list them.`}
                  data-testid="dashboard-cell"
                >
                  <Marks standings={here.map((t) => t.standing)} max={MARKS} />
                  {here.length > MARKS && <span className="adb-cell-main">{sq.main}</span>}
                  <span className="adb-cell-sub">{sq.sub}</span>
                </button>
              );
            })}
          </Fragment>
        );
      })}
    </div>
  );
}

/**
 * One assertion: where it stands, the claim that does not hold with what was found against it, and the measure of the
 * two, in one line; it opens on its brief, which is brought into view when it does not fit under the row.
 */
function TestRow({ test, result, failure, loading, open, onToggle, actions, checkActions, step }: {
  test: DashTest;
  result: ResultFacts | undefined;
  failure: string | undefined;
  loading: boolean;
  open: boolean;
  onToggle: () => void;
  actions: ((test: DashTest) => ReactNode) | undefined;
  checkActions: ((test: DashTest, check: CheckOutcome) => ReactNode) | undefined;
  step: BriefStep;
}) {
  const row = useRef<HTMLLIElement>(null);
  const read = result !== undefined;
  useEffect(() => {
    const element = row.current;
    if (!open || element === null) {
      return;
    }

    const box = element.getBoundingClientRect();
    if (box.top < 0 || box.bottom > window.innerHeight) {
      const still = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
      element.scrollIntoView({ block: box.height > window.innerHeight ? "start" : "nearest", behavior: still ? "auto" : "smooth" });
    }
  }, [open, read]);

  const others = test.dimensions.filter((d) => d.name !== "Set" && d.name !== "Check" && d.name !== "Type");
  const reading = readingOf(test, result);
  const quiet = test.standing === "passed" || test.standing === "disabled" || test.standing === "notRun";
  return (
    <li className={`adb-row${open ? ` adb-row-open adb-edge-${test.standing}` : ""}`} ref={row} data-testid="dashboard-row">
      <button type="button" className="adb-row-head" aria-expanded={open} onClick={onToggle}>
        <Glyph standing={test.standing} size={14} />
        <span className="adb-row-main">
          <span className="adb-row-title" title={test.title}>{test.title}</span>
          <span className="adb-row-where">{[...others.map((d) => d.value), test.dimensions.find((d) => d.name === "Check")?.value].filter(Boolean).join(" · ")}</span>
        </span>
        <span className="adb-row-reading" title={readingLine(reading)}>
          <span className={`adb-row-says${quiet ? " adb-muted" : ""}`}>
            {reading.says}
            {reading.more > 0 && <span className="adb-row-more">+{reading.more} more</span>}
          </span>
          {reading.claim !== null && <span className="adb-row-claim">{reading.claim}</span>}
        </span>
        <span className="adb-row-gauge">
          {reading.measure !== null && reading.result !== null && <Gauge measure={reading.measure} result={reading.result} compact />}
        </span>
        <span className="adb-row-when">{test.checkedUtc === null ? "" : when(test.checkedUtc)}</span>
        {open ? <ChevronDown size={14} aria-hidden /> : <ChevronRight size={14} aria-hidden />}
      </button>
      {open && <AssertionBrief test={test} result={result} failure={failure} loading={loading} actions={actions} checkActions={checkActions} step={step} />}
    </li>
  );
}
