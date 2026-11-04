import { useState } from "react";
import { ArrowLeft, ChevronRight, DatabaseSearch } from "lucide-react";
import { Button } from "@/components/ui/button";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import { CopyButton } from "@/components/CopyButton";
import { useAuth } from "@/auth/AuthContext";
import { cn } from "@/lib/utils";
import type { DeliveryAssertionOutcome } from "../../../api/delivery";
import { SeverityChip, TestOutcomeIcon } from "./AssertionBadges";
import { StagedRowsSql, type CheckSource } from "./AssertionSourceSql";
import { stagedRowsOf, type StagedRows } from "./assertionReading";
import { STANDING_TEXT, assertionStanding, checkReading, counted, needsLook } from "./assertionFormat";

/** Which checks a list shows: the ones that ask for a look, or every one. */
type CheckView = "look" | "all";

/**
 * What a check that asks for a look found, in the few words a row has room for: what it expected and what it found, else
 * why it did not get as far. A check that holds needs no words beside its glyph.
 */
function finding(outcome: DeliveryAssertionOutcome): string | null {
  if (!needsLook(outcome)) {
    return null;
  }

  const reading = checkReading(outcome);
  return outcome.actual ? `expected ${reading.expected}, found ${reading.found}` : outcome.message ?? null;
}

/**
 * One check on one line: where it stands, what it asserts, and what it found, toned as its severity weighs it. A severity
 * other than error is named, since error is what a check is unless it says otherwise.
 */
function CheckRow({ outcome, expanded, onClick }: { outcome: DeliveryAssertionOutcome; expanded?: boolean; onClick: () => void }) {
  const standing = assertionStanding(outcome);
  const found = finding(outcome);
  return (
    <button
      type="button"
      onClick={onClick}
      aria-expanded={expanded}
      className={cn(
        "grid w-full grid-cols-[auto_minmax(0,1fr)_auto] items-center gap-x-2.5 px-3 py-1.5 text-left outline-none transition-colors hover:bg-accent/50 focus-visible:bg-accent/50",
        "md:grid-cols-[auto_minmax(0,1.1fr)_minmax(0,1fr)_auto]",
        expanded && "bg-muted/30",
      )}
      data-testid="check-row"
      data-outcome={standing}
    >
      <TestOutcomeIcon outcome={standing} />
      <span className="flex min-w-0 items-center gap-1.5">
        <span className="truncate text-[12.5px]" title={outcome.label}>{outcome.label}</span>
        {outcome.severity !== "error" && standing !== "warned" && standing !== "noted" && <SeverityChip severity={outcome.severity} />}
      </span>
      <span className={cn("hidden truncate text-right font-mono text-[11.5px] md:block", STANDING_TEXT[standing])} title={found ?? undefined}>
        {found}
      </span>
      <ChevronRight className={cn("size-4 text-muted-foreground transition-transform", expanded && "rotate-90")} />
    </button>
  );
}

/**
 * A record id as a narrow cell shows it: the entity type and the id after it (`WellLog:5596f42c30e95cac`), the part that
 * differs from row to row; the cell's title and its copy button keep the whole id.
 */
function shortId(id: string): string {
  const at = id.lastIndexOf("--");
  return at < 0 ? id : id.slice(at + 2);
}

/**
 * The records that failed a check: the id (copyable), what it held there, and why that fails it. Known values name rows
 * rather than records, so a row comes first (the values it is located by), then the record it found, or none, and what
 * it came to. A check that only warns says why it warns.
 */
function Examples({ outcome }: { outcome: DeliveryAssertionOutcome }) {
  const examples = outcome.examples ?? [];
  if (examples.length === 0) {
    return null;
  }

  const known = outcome.type === "knownValues";
  const why = known ? "What it came to" : assertionStanding(outcome) === "warned" ? "Why it warns" : "Why it fails";

  return (
    <div className="flex flex-col gap-1">
      <div className="overflow-x-auto rounded-md border bg-card">
        <table className="w-full text-[12px]" data-testid="assertion-examples">
          <thead>
            <tr className="border-b text-left text-[11px] text-muted-foreground">
              {known ? <th className="px-2 py-1.5 font-medium">Row</th> : null}
              <th className="px-2 py-1.5 font-medium">Record</th>
              {known ? null : <th className="px-2 py-1.5 font-medium">Value</th>}
              <th className="px-2 py-1.5 font-medium">{why}</th>
            </tr>
          </thead>
          <tbody>
            {examples.map((example, index) => {
              const record = (
                <td className="max-w-[13rem] px-2 py-1.5">
                  {example.id
                    ? (
                      <span className="flex items-center gap-1">
                        <span className="truncate font-mono text-[11.5px]" title={example.id}>{shortId(example.id)}</span>
                        <CopyButton label="Copy the id" text={example.id} testId="copy-example-id" iconOnly />
                      </span>
                    )
                    : <span className="text-muted-foreground">{known ? "none found" : "-"}</span>}
                </td>
              );
              return (
                <tr key={`${example.id ?? ""}-${index}`} className="border-b align-top last:border-b-0">
                  {known
                    ? (
                      <td className="min-w-[11rem] max-w-[16rem] px-2 py-1.5">
                        <span className="break-words font-mono text-[11.5px]" data-testid="example-row">{example.value ?? "(nothing)"}</span>
                      </td>
                    )
                    : null}
                  {record}
                  {known
                    ? null
                    : (
                      <td className="max-w-[14rem] px-2 py-1.5">
                        <span className="line-clamp-3 break-all font-mono text-[11.5px]" title={example.value ?? undefined}>{example.value ?? "(nothing)"}</span>
                      </td>
                    )}
                  <td className="px-2 py-1.5">{example.reason}</td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
      {outcome.examplesTrimmed && (
        <p className="text-[11px] text-muted-foreground">
          Fewer examples are kept than the run met, so the result stays within what the ledger keeps for one test.
        </p>
      )}
    </div>
  );
}

/**
 * The staged rows a check read, in SQL, written once the reader asks for it: a check opens on what it found. Writing it reads
 * the delivery flow's tables, so it is offered to those who may operate.
 */
function StagedRowsSection({ source, rows }: { source: CheckSource; rows: StagedRows }) {
  const [shown, setShown] = useState(false);
  const { hasScope } = useAuth();
  if (!hasScope("operate")) {
    return null;
  }

  return shown
    ? <StagedRowsSql source={source} rows={rows} />
    : (
      <Button variant="outline" size="xs" className="w-fit" onClick={() => setShown(true)} data-testid="check-detail-sql">
        <DatabaseSearch />
        Show the rows in SQL
      </Button>
    );
}

/**
 * Everything one check found now, each fact once: what it expected (and what qualifies that), what it found, why it came out
 * as it did when that adds to the two, and the records that failed it. Given where its result is kept (`source`), a check
 * that read a delivery flow's staged rows offers them in SQL.
 */
export function CheckDetail({ outcome, onBack, source }: { outcome: DeliveryAssertionOutcome; onBack?: () => void; source?: CheckSource }) {
  const standing = assertionStanding(outcome);
  const reading = checkReading(outcome);
  const staged = source === undefined ? null : stagedRowsOf(outcome);
  return (
    <div className="flex flex-col gap-3" data-testid="check-detail">
      {onBack !== undefined && (
        <Button variant="ghost" size="xs" className="w-fit" onClick={onBack} data-testid="check-detail-back">
          <ArrowLeft />
          All checks
        </Button>
      )}
      {onBack !== undefined && (
        <div className="flex flex-wrap items-center gap-2">
          <TestOutcomeIcon outcome={standing} />
          <span className="text-[14px] font-medium">{outcome.label}</span>
          <SeverityChip severity={outcome.severity} />
          <span className="font-mono text-[11px] text-muted-foreground">{outcome.type}</span>
        </div>
      )}
      {outcome.description && <p className="text-[12.5px] text-muted-foreground">{outcome.description}</p>}
      <dl className="grid grid-cols-1 gap-2 sm:grid-cols-2">
        <div className="rounded-md border bg-card px-3 py-2">
          <dt className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">Expected</dt>
          <dd className="mt-0.5 break-words font-mono text-[12px]">{reading.expected}</dd>
          {reading.qualifiers.length > 0 && <dd className="mt-0.5 text-[11.5px] text-muted-foreground">{reading.qualifiers.join(" · ")}</dd>}
        </div>
        <div className="rounded-md border bg-card px-3 py-2">
          <dt className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">Found</dt>
          <dd className={cn("mt-0.5 break-words font-mono text-[12px]", needsLook(outcome) && STANDING_TEXT[standing])}>{reading.found}</dd>
        </div>
      </dl>
      {reading.why !== null && <p className="text-[12.5px]" data-testid="assertion-message">{reading.why}</p>}
      <Examples outcome={outcome} />
      {source !== undefined && staged !== null && <StagedRowsSection source={source} rows={staged} />}
    </div>
  );
}

/**
 * A test's checks as one list, opening on the ones that ask for a look (every one, when all hold), the rest a click away.
 * In a sheet a check opens on its own (`onOpen`); in a report it opens in place, under its line.
 */
export function CheckList({ outcomes, onOpen, source, testId = "check-list" }: {
  outcomes: readonly DeliveryAssertionOutcome[];
  /** Opens one check on its own; without it a check opens in place. */
  onOpen?: (index: number) => void;
  /** Where the checks' result is kept, which a check that read staged rows names to show them in SQL. */
  source?: CheckSource;
  testId?: string;
}) {
  const looking = outcomes.filter(needsLook);
  const [view, setView] = useState<CheckView>(() => (looking.length > 0 ? "look" : "all"));
  const [open, setOpen] = useState<ReadonlySet<number>>(new Set());
  if (outcomes.length === 0) {
    return <p className="text-[12.5px] text-muted-foreground">The assertion recorded no check.</p>;
  }

  // The choice is only offered when it changes something: some checks ask for a look and some do not.
  const choosing = looking.length > 0 && looking.length < outcomes.length;
  const shown = view === "look" ? looking : outcomes;
  const hidden = outcomes.length - shown.length;
  const toggle = (index: number) => setOpen((was) => {
    const next = new Set(was);
    if (!next.delete(index)) {
      next.add(index);
    }

    return next;
  });

  return (
    <div className="flex flex-col gap-2" data-testid={testId}>
      {choosing && (
        <ToggleGroup
          type="single"
          variant="outline"
          size="sm"
          value={view}
          onValueChange={(value) => { if (value === "look" || value === "all") { setView(value); } }}
          aria-label="Which checks to show"
          data-testid={`${testId}-view`}
        >
          <ToggleGroupItem value="look" className="h-7 gap-1.5 px-2.5 text-[12.5px]">
            Needs a look
            <span className="rounded-full bg-muted px-1.5 font-mono text-[11px] tabular-nums">{looking.length}</span>
          </ToggleGroupItem>
          <ToggleGroupItem value="all" className="h-7 gap-1.5 px-2.5 text-[12.5px]">
            All
            <span className="rounded-full bg-muted px-1.5 font-mono text-[11px] tabular-nums">{outcomes.length}</span>
          </ToggleGroupItem>
        </ToggleGroup>
      )}
      <div className="divide-y overflow-hidden rounded-lg border">
        {shown.map((outcome) => (
          <div key={outcome.index}>
            <CheckRow
              outcome={outcome}
              expanded={onOpen === undefined ? open.has(outcome.index) : undefined}
              onClick={() => (onOpen === undefined ? toggle(outcome.index) : onOpen(outcome.index))}
            />
            {onOpen === undefined && open.has(outcome.index) && (
              <div className="border-t bg-muted/20 px-3 py-3"><CheckDetail outcome={outcome} source={source} /></div>
            )}
          </div>
        ))}
      </div>
      {view === "look" && hidden > 0 && (
        <button
          type="button"
          onClick={() => setView("all")}
          className="w-fit text-[12px] text-muted-foreground underline-offset-2 hover:text-foreground hover:underline"
          data-testid={`${testId}-show-all`}
        >
          {`Show ${counted(hidden, "passed check")}`}
        </button>
      )}
    </div>
  );
}
