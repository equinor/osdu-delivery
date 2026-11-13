import { Link } from "react-router-dom";
import { CircleCheck, CircleX, PencilLine } from "lucide-react";
import { Card } from "@/components/ui/card";
import type { RunDetail } from "@/api/types";
import { CopyButton } from "@/components/CopyButton";
import { DataTable, type Column } from "@/components/DataTable";
import { ExplainTip } from "@/components/ExplainTip";
import { runResult } from "../runOutcome";
import { CheckGlyph, NotesMark } from "./DimensionViewBadges";
import { runViewsOf, viewHref, viewObject, type RunView } from "./dimensionViewFormat";

/** What the build did with a view, as a glyph in its tone and a word; why it could not write it on hover, with Copy. */
function WriteMark({ view }: { view: RunView }) {
  switch (view.status) {
    case "written":
      return <span className="inline-flex items-center gap-1.5 align-middle text-[12px]"><PencilLine className="size-3.5 shrink-0 text-info" aria-hidden />written</span>;
    case "unchanged":
      return <span className="inline-flex items-center gap-1.5 align-middle text-[12px] text-muted-foreground"><CircleCheck className="size-3.5 shrink-0" aria-hidden />unchanged</span>;
    case "failed":
      return (
        <ExplainTip title="Why it was not written" text={view.error ?? "The run kept no reason."} testId={`run-dimension-view-error-${view.view}`}>
          <span className="inline-flex items-center gap-1.5 align-middle text-[12px]" data-testid={`run-dimension-view-error-${view.view}`}>
            <CircleX className="size-3.5 shrink-0 text-destructive" aria-hidden />
            failed
          </span>
        </ExplainTip>
      );
    default:
      return <span className="text-[12px] text-muted-foreground">planned</span>;
  }
}

/**
 * The views a dimension run wrote and checked in its partition, each with what the build did with it (written, unchanged
 * or failed), what its check found (the rows, whether it could be read, its notes), and a way to its page; the views it
 * dropped because its flow no longer declares them; and, for a plan, what a build would write and what would stop it.
 */
export function DimensionRunViews({ run }: { run: RunDetail }) {
  const reported = runViewsOf(runResult(run));
  if (reported === null || (reported.views.length === 0 && reported.dropped.length === 0)) {
    return null;
  }

  const plan = run.operation === "plan";
  const columns: Column<RunView>[] = [
    {
      id: "view",
      header: "View",
      render: (row) => (
        <span className="flex min-w-0 items-baseline gap-2">
          <Link to={viewHref(row.view)} className="font-medium hover:underline" data-testid={`run-dimension-view-${row.view}`}>{row.view}</Link>
          {row.viewName !== "" && <span className="truncate font-mono text-[11.5px] text-muted-foreground">{viewObject(row)}</span>}
        </span>
      ),
    },
    { id: "write", header: plan ? "Build" : "Write", render: (row) => <WriteMark view={row} /> },
    {
      id: "check",
      header: "Check",
      render: (row) => (row.check === null
        ? <span className="text-muted-foreground">-</span>
        : <span className="inline-flex items-center gap-1.5 align-middle text-[12px]"><CheckGlyph status={row.check} />{row.check}</span>),
    },
    {
      id: "rows",
      header: "Rows",
      align: "right",
      render: (row) => (row.rows === null
        ? <span className="text-muted-foreground">-</span>
        : <span className="font-mono text-[12px] tabular-nums">{row.rows.toLocaleString("en-US")}</span>),
    },
    {
      id: "notes",
      header: "Notes",
      fill: true,
      floor: 120,
      render: (row) => (row.notes.length === 0 && row.problems.length === 0
        ? <span className="text-[12px] text-muted-foreground">none</span>
        : (
          <span className="inline-flex items-center gap-3 align-middle">
            <NotesMark notes={row.problems} title="What stops a build writing it" testId={`run-dimension-view-problems-${row.view}`} />
            <NotesMark notes={row.notes} title={plan ? "What a build will do" : "What the check found"} testId={`run-dimension-view-notes-${row.view}`} />
          </span>
        )),
    },
  ];
  if (plan) {
    columns.push({
      id: "sql",
      header: "",
      align: "right",
      render: (row) => (row.sql === null ? null : <CopyButton iconOnly label="Copy the statement a build writes it with" text={row.sql} testId={`run-dimension-view-sql-${row.view}`} />),
    });
  }

  return (
    <Card className="gap-2 rounded-lg p-3" data-testid="run-dimension-views">
      <div className="flex flex-wrap items-baseline gap-x-2">
        <h2 className="text-[13px] font-medium">Views</h2>
        <span className="text-[12px] text-muted-foreground">
          {plan ? "as a build would write them; the plan wrote nothing" : "as the run wrote them and checked them in its partition"}
        </span>
      </div>
      {reported.views.length > 0 && (
        <DataTable columns={columns} rows={reported.views} rowKey={(row) => row.view} emptyMessage="The run reported no view." data-testid="run-dimension-views-table" />
      )}
      {reported.dropped.length > 0 && (
        <p className="text-[12px] text-muted-foreground" data-testid="run-dimension-views-dropped">
          Dropped, as the flow no longer declares them:{" "}
          {reported.dropped.map((name, index) => (
            <span key={name}>
              {index > 0 && ", "}
              <span className="font-mono text-foreground">{name}</span>
            </span>
          ))}
        </p>
      )}
    </Card>
  );
}
