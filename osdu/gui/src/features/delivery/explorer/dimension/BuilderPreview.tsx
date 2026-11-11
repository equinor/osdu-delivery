import { useMemo } from "react";
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { Loader2, RefreshCw, TableProperties } from "lucide-react";
import { Button } from "@/components/ui/button";
import { DataTable, type Column } from "@/components/DataTable";
import { RichTooltip } from "@/components/RichTooltip";
import { cn } from "@/lib/utils";
import { previewDimension, type DimensionDraft } from "../../../../api/explorer";
import { failureText } from "../../answers";
import { lastProperty, previewRows, previewWords, shortId, type PreviewRow } from "./dimensionStory";
import { NoteLine } from "./StoryFrame";
import type { DimensionBuild } from "./useDimensionBuild";

/** How long a preview read stands before the same draft is read again. */
const FRESH_MS = 60_000;

/** The keys a preview names where it says which are no row; the rest are counted. */
const NAMED_LEFT_OUT = 3;

/** How much of a key's end a cell keeps in view: enough to tell ids apart, the whole key being on hover. */
const KEY_WIDTH = 136;

/** The least width the name and each attribute keep before the grid scrolls sideways. */
const VALUE_FLOOR = 110;
const ATTRIBUTE_FLOOR = 88;

/**
 * A cell's text, cut short where the column ends (or at `max` pixels, for a column that does not share the width), with
 * the whole of it on hover; an id cut at its start, which its end tells apart.
 */
function Cell({ text, fromEnd = false, strong = false, max }: { text: string | null; fromEnd?: boolean; strong?: boolean; max?: number }) {
  if (text === null) {
    return null;
  }

  return (
    <RichTooltip body={text} mono>
      <span
        dir={fromEnd ? "rtl" : undefined}
        className={cn("block truncate text-left font-mono text-[12px]", strong && "font-medium")}
        style={max === undefined ? undefined : { maxWidth: max }}
      >
        {fromEnd ? <bdi dir="ltr">{text}</bdi> : text}
      </span>
    </RichTooltip>
  );
}

/**
 * The table the drafted dimension makes, previewed (osdu/docs/reference/concepts/explorer-dimension-builder.md, The
 * preview): the keys most records hold, made into their rows through the partition's connection exactly as a build makes
 * them, laid out as the table is, under the names its columns take. Read while it is shown, once the YAML loads, and again
 * when the draft changes; nothing is written.
 */
export function BuilderPreview({ build, draft }: { build: DimensionBuild; draft: DimensionDraft }) {
  const compose = build.compose;
  const settled = build.settled;
  // The draft as it settled, once a build would make it: what the compose of that same draft found.
  const loads = settled !== null && compose.data !== undefined && !compose.isPlaceholderData && compose.data.valid;
  const preview = useQuery({
    queryKey: ["explorer", "dimension", "preview", build.partition, settled],
    queryFn: () => previewDimension(build.partition, settled!),
    enabled: build.reachable && loads,
    placeholderData: keepPreviousData,
    staleTime: FRESH_MS,
    retry: false,
    refetchOnWindowFocus: false,
  });

  const dimension = compose.data?.dimension ?? null;
  const answer = preview.data?.answer;
  const laid = useMemo(() => (answer === undefined ? null : previewRows(answer.rows, draft.attributes)), [answer, draft.attributes]);
  const keyName = dimension?.keyColumn ?? lastProperty(draft.path ?? "");
  const valueName = dimension?.valueColumn ?? (draft.label.length > 0 ? lastProperty(draft.label[draft.label.length - 1]) : draft.name || "value");
  const columns: Column<PreviewRow>[] = [
    { id: "key", header: keyName, render: (row) => <Cell text={row.key} fromEnd max={KEY_WIDTH} />, width: KEY_WIDTH },
    { id: "value", header: valueName, render: (row) => <Cell text={row.value} strong />, fill: true, floor: VALUE_FLOOR },
    ...draft.attributes.map((attribute): Column<PreviewRow> => ({
      id: `attribute:${attribute.name}`,
      header: attribute.name,
      render: (row) => <Cell text={row.attributes[attribute.name] ?? null} />,
      fill: true,
      floor: ATTRIBUTE_FLOOR,
    })),
    {
      id: "records",
      header: "records",
      align: "right",
      width: 64,
      render: (row) => <span className="font-mono text-[12px] tabular-nums">{row.records === null ? "" : row.records.toLocaleString("en-US")}</span>,
    },
  ];

  // A draft a build would refuse shows no rows, even those of the draft before it; one still being checked keeps them.
  const refused = compose.data !== undefined && !compose.isPlaceholderData && !compose.data.valid;
  if (refused || (!loads && preview.data === undefined)) {
    return (
      <div className="flex min-h-0 flex-1 flex-col items-center justify-center gap-2 px-6 text-center" data-testid="builder-preview">
        <TableProperties className="size-6 text-muted-foreground" aria-hidden />
        <p className="max-w-80 text-[12.5px] text-muted-foreground" data-testid="builder-preview-waiting">
          {refused
            ? "The table is previewed once the YAML loads. Put right what the line below names, and its rows are read."
            : "Reading the draft."}
        </p>
      </div>
    );
  }

  return (
    <div className="flex min-h-0 flex-1 flex-col" data-testid="builder-preview">
      <div className="flex min-w-0 shrink-0 items-start gap-2 px-4 pb-2 pt-3">
        <div className="flex min-w-0 flex-col">
          <span className="truncate font-mono text-[13px] font-medium" data-testid="builder-preview-table">{compose.data?.table ?? ""}</span>
          <span className="text-[12px] text-muted-foreground" data-testid="builder-preview-count">
            {answer === undefined || laid === null
              ? "Reading the keys most records hold, as a build reads them"
              : `${previewWords(laid.rows.length, answer.rows.length, answer.keys, answer.everyKey)}, as a build makes them. Nothing is written.`}
          </span>
        </div>
        <span className="ml-auto flex shrink-0 items-center gap-1">
          {preview.isFetching && <Loader2 className="size-3.5 animate-spin text-muted-foreground" aria-label="reading the rows" />}
          <RichTooltip body="Read the rows again from OSDU.">
            <Button
              variant="ghost"
              size="icon-xs"
              onClick={() => void preview.refetch()}
              disabled={!loads || preview.isFetching}
              aria-label="Read the rows again"
              data-testid="builder-preview-refresh"
            >
              <RefreshCw />
            </Button>
          </RichTooltip>
        </span>
      </div>

      {preview.isError && preview.data === undefined
        ? <div className="px-4" data-testid="builder-preview-problem"><NoteLine>{`The rows could not be read: ${failureText(preview.error)}`}</NoteLine></div>
        : answer?.refusal
          ? <div className="px-4" data-testid="builder-preview-problem"><NoteLine>{`The search service refused the query: ${answer.refusal}`}</NoteLine></div>
          : (
            <div
              className={cn(
                "flex min-h-0 flex-col px-4 transition-opacity",
                preview.isPlaceholderData && "opacity-60",
                "[&>div]:min-h-0 [&_[data-slot=table-container]]:min-h-0 [&_[data-slot=table-container]]:flex-1 [&_[data-slot=table-container]]:overflow-auto",
                "[&_th]:sticky [&_th]:top-0 [&_th]:z-10 [&_th]:bg-card [&_th]:shadow-[inset_0_-1px_0_var(--border)]",
                "[&_td]:px-2 [&_td]:py-1 [&_th]:px-2 [&_td:first-child]:pl-3 [&_th:first-child]:pl-3 [&_td:last-child]:pr-3 [&_th:last-child]:pr-3",
              )}
            >
              <DataTable
                columns={columns}
                rows={laid?.rows}
                rowKey={(row) => row.id}
                emptyMessage="No record of the type holds a key, so the table would be empty."
                data-testid="builder-preview-grid"
              />
            </div>
          )}

      <div className="flex shrink-0 flex-col px-4 pb-3">
        {laid !== null && laid.leftOut.length > 0 && (
          <NoteLine>
            {`${laid.leftOut.length} ${laid.leftOut.length === 1 ? "key makes" : "keys make"} no row, having no name: ${laid.leftOut
              .slice(0, NAMED_LEFT_OUT)
              .map((left) => `${shortId(left.key)} (${left.why})`)
              .join(", ")}${laid.leftOut.length > NAMED_LEFT_OUT ? ", ..." : ""}.`}
          </NoteLine>
        )}
        {(answer?.notes ?? []).map((note, index) => <NoteLine key={`${index}-${note}`}>{note}</NoteLine>)}
      </div>
    </div>
  );
}
