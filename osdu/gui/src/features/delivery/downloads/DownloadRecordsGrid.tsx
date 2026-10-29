import { useMemo, useState } from "react";
import { keepPreviousData, useInfiniteQuery } from "@tanstack/react-query";
import { Card } from "@/components/ui/card";
import { DataTable, type Column } from "@/components/DataTable";
import { RelativeTime } from "@/components/RelativeTime";
import { SearchInput } from "@/components/SearchInput";
import { TruncatedText } from "@/components/TruncatedText";
import { useDebouncedValue } from "@/hooks/useDebouncedValue";
import { formatBytes } from "@/lib/time";
import { downloadApi, type DownloadCounts, type DownloadRecord, type DownloadRecordState, type DownloadRef } from "../../../api/downloads";
import { ProblemView } from "../TemplateSheet";
import { DimensionGrid, GridFooter, GridViewMenu } from "../dimensions/DimensionGrid";
import { useHiddenColumns, type GridColumnChoice } from "../dimensions/dimensionGridState";
import { RecordStateStrip, StateMark } from "./DownloadBadges";
import { stateVisual } from "./downloadFormat";

const PAGE = 200;

/** How long typing rests before the grid searches what was typed. */
const SEARCH_REST_MS = 300;

/** The columns the grid starts without. */
const AT_FIRST: readonly string[] = ["listed", "sha256"];

/** A version as OSDU writes it, or a dash for none. */
function VersionText({ value }: { value: number | undefined }) {
  return value === undefined
    ? <span className="text-[12px] text-muted-foreground">-</span>
    : <span className="font-mono text-[12px] tabular-nums">{value}</span>;
}

/**
 * A download's records (osdu/docs/reference/flow/download.md, Where a record stands) as a grid that fits the window and
 * scrolls inside the page: the states lead its toolbar, each with its count, and a search finds a record by any part of its id
 * or file name, whatever its case. More records are read as it is scrolled, a page at a time after the last one read, so a
 * download of a million records reads as quickly as one of ten. Each row is the file the record is written to and its id, both
 * to copy, where it stands, the version listed and the version its file holds, its size and when it was written; why its last
 * write did not happen is on hover. Columns are left out under View.
 */
export function DownloadRecordsGrid({ reference, counts, state, onState, testId = "download-records" }: {
  reference: DownloadRef;
  counts: DownloadCounts | undefined;
  /** The state in view; null for every record. */
  state: DownloadRecordState | null;
  onState: (state: DownloadRecordState | null) => void;
  testId?: string;
}) {
  const [hidden, toggleColumn] = useHiddenColumns("osdu.downloads.records.columns", AT_FIRST);
  const [search, setSearch] = useState("");
  const typed = useDebouncedValue(search.trim(), SEARCH_REST_MS);
  const pages = useInfiniteQuery({
    queryKey: ["delivery", "downloads", "records", reference.partition, reference.downloadId, state, typed],
    queryFn: ({ pageParam }) => downloadApi.records(reference, { state: state ?? undefined, q: typed === "" ? undefined : typed, after: pageParam, limit: PAGE }),
    initialPageParam: undefined as number | undefined,
    getNextPageParam: (last) => last.next,
    placeholderData: keepPreviousData,
  });
  const rows = useMemo(() => pages.data?.pages.flatMap((page) => page.items), [pages.data]);
  const total = typed !== "" ? null : state === null
    ? (counts === undefined ? null : counts.written + counts.due + counts.problem + counts.gone)
    : counts?.[state] ?? null;
  const more = () => {
    if (pages.hasNextPage && !pages.isFetchingNextPage) {
      void pages.fetchNextPage();
    }
  };

  const choices: GridColumnChoice[] = [
    { id: "listed", label: "Version listed" },
    { id: "holds", label: "File holds" },
    { id: "size", label: "Size" },
    { id: "written", label: "Written" },
    { id: "sha256", label: "SHA-256" },
  ];
  const all: Column<DownloadRecord>[] = [
    {
      id: "file",
      header: "File",
      fill: true,
      floor: 260,
      render: (row) => <TruncatedText text={row.fileName} mono maxWidth={420} copy copyTestId={`${testId}-copy-file`} />,
    },
    {
      id: "id",
      header: "OSDU id",
      fill: true,
      floor: 220,
      render: (row) => <TruncatedText text={row.targetId} mono maxWidth={420} copy copyTestId={`${testId}-copy-id`} />,
    },
    { id: "state", header: "State", render: (row) => <StateMark state={row.state} detail={row.problem} testId={`${testId}-state`} /> },
    { id: "listed", header: "Version listed", align: "right", render: (row) => <VersionText value={row.version} /> },
    { id: "holds", header: "File holds", align: "right", render: (row) => <VersionText value={row.writtenVersion} /> },
    {
      id: "size",
      header: "Size",
      align: "right",
      render: (row) => (row.bytes === undefined
        ? <span className="text-[12px] text-muted-foreground">-</span>
        : <span className="font-mono text-[12px] tabular-nums">{formatBytes(row.bytes)}</span>),
    },
    {
      id: "written",
      header: "Written",
      render: (row) => (row.writtenUtc === undefined
        ? <span className="text-[12px] text-muted-foreground">not yet</span>
        : <span className="whitespace-nowrap text-[12px] text-muted-foreground"><RelativeTime value={row.writtenUtc} absolute={false} /></span>),
    },
    {
      id: "sha256",
      header: "SHA-256",
      render: (row) => (row.sha256 === undefined
        ? <span className="text-[12px] text-muted-foreground">-</span>
        : <TruncatedText text={row.sha256} mono maxWidth={160} />),
    },
    {
      id: "problem",
      header: "Problem",
      render: (row) => (row.problem === undefined
        ? null
        : <TruncatedText text={row.problem} title="Why it was not written" maxWidth={260} copy copyTestId={`${testId}-copy-problem`} className="text-[12px] text-muted-foreground" />),
    },
  ];
  // A grid of one state says it once, in its tab, not on every row; why a write did not happen is a column of the problems
  // alone, and on hover of a problem's state everywhere else.
  const columns = all.filter((column) => !hidden.has(column.id)
    && !(column.id === "state" && state !== null)
    && !(column.id === "problem" && state !== "problem"));
  const noun = state === null ? "record" : `${stateVisual(state).label.toLowerCase()} record`;

  const toolbar = (
    <div className="flex min-w-0 items-center gap-1 border-b border-border pl-1.5 pr-2">
      <RecordStateStrip counts={counts} selected={state} onSelect={onState} testId={`${testId}-states`} />
      <div className="ml-auto flex shrink-0 items-center gap-1 py-1">
        <SearchInput
          value={search}
          onChange={setSearch}
          placeholder="Find by id or file name"
          label="Find a record by its id or file name"
          className="sm:w-60"
          testId={`${testId}-search`}
        />
        <GridViewMenu columns={choices} hidden={hidden} atFirst={AT_FIRST} onToggle={toggleColumn} compact testId={`${testId}-view`} />
      </div>
    </div>
  );

  const emptyMessage = typed !== ""
    ? `No ${noun} has an id or file name holding "${typed}".`
    : state === null
      ? "The download lists no record yet: its first run lists the kind and writes every record."
      : `No record is ${stateVisual(state).label.toLowerCase()}.`;

  return (
    <div className="flex min-w-0 flex-col" data-testid={testId}>
      {pages.isError
        ? (
          <Card className="gap-0 overflow-hidden rounded-lg p-0">
            {toolbar}
            <div className="p-3"><ProblemView error={pages.error} testId={`${testId}-error`} /></div>
          </Card>
        )
        : (
          <DimensionGrid onNearEnd={more} className="[&_tbody_tr]:scroll-mt-8" testId={`${testId}-grid`}>
            <DataTable
              toolbar={toolbar}
              columns={columns}
              rows={rows}
              rowKey={(row) => String(row.downloadRecordId)}
              footer={(
                <GridFooter
                  shown={rows?.length ?? 0}
                  total={total}
                  noun={noun}
                  hasMore={pages.hasNextPage}
                  loading={pages.isFetchingNextPage}
                  onMore={more}
                  testId={`${testId}-footer`}
                />
              )}
              emptyMessage={emptyMessage}
              data-testid={`${testId}-table`}
            />
          </DimensionGrid>
        )}
    </div>
  );
}
