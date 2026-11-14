import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { DatabaseZap, Loader2, RefreshCw } from "lucide-react";
import { Button } from "@/components/ui/button";
import { DataTable, type Column } from "@/components/DataTable";
import { RichTooltip } from "@/components/RichTooltip";
import { cn } from "@/lib/utils";
import { previewCacheType, type CacheSample, type CacheTypeDraft } from "../../../../api/explorer";
import { failureText } from "../../answers";
import { Cell } from "../dimension/BuilderPreview";
import { NoteLine } from "../dimension/StoryFrame";
import { cellText, sampleWords, typeNameOf } from "./cacheStory";
import type { CacheBuild } from "./useCacheBuild";

/** How long a preview read stands before the same draft is read again. */
const FRESH_MS = 60_000;

/** How much of an id's end a cell keeps in view: enough to tell records apart, the whole id being on hover. */
const ID_WIDTH = 150;

/** The least width each value keeps before the grid scrolls sideways. */
const VALUE_FLOOR = 104;

type SampleRecord = CacheSample["records"][number];

/**
 * The records the drafted type would hold, previewed (osdu/docs/reference/concepts/explorer-cache-builder.md, The
 * preview): the first records the search finds, made into what the cache keeps of each through the partition's
 * connection exactly as a refresh makes them, a column for each value under the name a mapping reads it by. Read while it
 * is shown, once the YAML loads, and again when the draft changes; nothing is written.
 */
export function CachePreview({ build, draft }: { build: CacheBuild; draft: CacheTypeDraft }) {
  const compose = build.compose;
  const settled = build.settled;
  // The draft as it settled, once a refresh would cache it: what the compose of that same draft found.
  const loads = settled !== null && compose.data !== undefined && !compose.isPlaceholderData && compose.data.valid;
  const preview = useQuery({
    queryKey: ["explorer", "cache", "preview", build.partition, settled],
    queryFn: () => previewCacheType(build.partition, settled!),
    enabled: build.reachable && loads,
    placeholderData: keepPreviousData,
    staleTime: FRESH_MS,
    retry: false,
    refetchOnWindowFocus: false,
  });

  const answer = preview.data?.answer;
  const columns: Column<SampleRecord>[] = [
    { id: "id", header: "id", render: (row) => <Cell text={row.id} fromEnd max={ID_WIDTH} />, width: ID_WIDTH },
    ...(answer?.columns ?? []).map((column, index): Column<SampleRecord> => ({
      id: `value:${column.name}`,
      header: column.name,
      render: (row) => <Cell text={cellText(row.fields[column.name])} strong={index === 0} />,
      fill: true,
      floor: VALUE_FLOOR,
    })),
  ];

  // A draft a refresh would refuse shows no records, even those of the draft before it; one still being checked keeps them.
  const refused = compose.data !== undefined && !compose.isPlaceholderData && !compose.data.valid;
  if (refused || (!loads && preview.data === undefined)) {
    return (
      <div className="flex min-h-0 flex-1 flex-col items-center justify-center gap-2 px-6 text-center" data-testid="builder-preview">
        <DatabaseZap className="size-6 text-muted-foreground" aria-hidden />
        <p className="max-w-80 text-[12.5px] text-muted-foreground" data-testid="builder-preview-waiting">
          {refused
            ? "The records are previewed once the YAML loads. Put right what the line below names, and they are read."
            : "Reading the draft."}
        </p>
      </div>
    );
  }

  return (
    <div className="flex min-h-0 flex-1 flex-col" data-testid="builder-preview">
      <div className="flex min-w-0 shrink-0 items-start gap-2 px-4 pb-2 pt-3">
        <div className="flex min-w-0 flex-col">
          <span className="truncate font-mono text-[13px] font-medium" data-testid="builder-preview-table">{`$cache.${answer?.name ?? typeNameOf(draft)}`}</span>
          <span className="text-[12px] text-muted-foreground" data-testid="builder-preview-count">
            {answer === undefined
              ? "Reading the first records, as a refresh reads them"
              : `${sampleWords(answer.records.length, answer.total)}, as a refresh would cache them. Nothing is written.`}
          </span>
        </div>
        <span className="ml-auto flex shrink-0 items-center gap-1">
          {preview.isFetching && <Loader2 className="size-3.5 animate-spin text-muted-foreground" aria-label="reading the records" />}
          <RichTooltip body="Read the records again from OSDU.">
            <Button
              variant="ghost"
              size="icon-xs"
              onClick={() => void preview.refetch()}
              disabled={!loads || preview.isFetching}
              aria-label="Read the records again"
              data-testid="builder-preview-refresh"
            >
              <RefreshCw />
            </Button>
          </RichTooltip>
        </span>
      </div>

      {preview.isError && preview.data === undefined
        ? <div className="px-4" data-testid="builder-preview-problem"><NoteLine>{`The records could not be read: ${failureText(preview.error)}`}</NoteLine></div>
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
                rows={answer?.records}
                rowKey={(row) => row.id}
                emptyMessage="No record matches the kind and the query, so the type would be empty."
                data-testid="builder-preview-grid"
              />
            </div>
          )}

      <div className="flex shrink-0 flex-col px-4 pb-3">
        {(answer?.notes ?? []).map((note, index) => <NoteLine key={`${index}-${note}`}>{note}</NoteLine>)}
      </div>
    </div>
  );
}
