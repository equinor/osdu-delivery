import { useMemo, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { useNavigate } from "react-router-dom";
import { CheckIcon, ChevronsUpDown } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Command, CommandEmpty, CommandGroup, CommandInput, CommandItem, CommandList } from "@/components/ui/command";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { Skeleton } from "@/components/ui/skeleton";
import { cn } from "@/lib/utils";
import { dataStreamApi } from "../../api/endpoints";
import type { DataStream } from "../../api/types";
import { statusIcons, statusInk } from "./StreamStatusBadge";
import { statusLabels } from "./streamPresentation";

/** The most streams the switcher lists: the board's own cap on one answer. */
const LISTED = 500;

/**
 * Which stream the page follows, as a searchable picker in its heading: every stream the board checks, by source, each
 * with its verdict's glyph, so the next one worth following is found without going back to the board. The list is read
 * when the picker opens, not before, since the board's analysis is the one thing on this page that costs.
 */
export function StreamSwitcher({ pipelineId, flowName, windowDays, includeBackfills }: {
  pipelineId: string;
  flowName: string;
  windowDays: number;
  includeBackfills: boolean;
}) {
  const navigate = useNavigate();
  const [open, setOpen] = useState(false);
  const query = useQuery({
    queryKey: ["datastreams", windowDays, "", "all", includeBackfills, true, "switcher"],
    queryFn: () => dataStreamApi.list({
      days: windowDays, scope: "all", includeBackfills, includeUnscheduled: true, limit: LISTED,
    }),
    enabled: open,
    staleTime: 60_000,
  });

  const sources = useMemo(() => {
    const bySource = new Map<string, DataStream[]>();
    for (const stream of query.data?.streams ?? []) {
      const list = bySource.get(stream.source) ?? bySource.set(stream.source, []).get(stream.source)!;
      list.push(stream);
    }
    return [...bySource.entries()]
      .sort(([a], [b]) => a.localeCompare(b))
      .map(([source, streams]) => ({ source, streams: streams.sort((a, b) => a.flowName.localeCompare(b.flowName)) }));
  }, [query.data]);

  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <Button
          variant="outline"
          size="sm"
          role="combobox"
          aria-expanded={open}
          aria-label="Follow another stream"
          data-testid="stream-switcher"
          className="h-8 w-[260px] justify-between font-normal"
        >
          <span className="truncate font-mono text-[12px]">{flowName}</span>
          <ChevronsUpDown className="text-muted-foreground" />
        </Button>
      </PopoverTrigger>
      <PopoverContent align="end" className="w-96 p-0">
        <Command>
          <CommandInput placeholder="Search streams by flow, table or source" />
          <CommandList>
            {query.isPending && (
              <div className="flex flex-col gap-1.5 p-2">
                <Skeleton className="h-6 rounded" />
                <Skeleton className="h-6 rounded" />
                <Skeleton className="h-6 rounded" />
              </div>
            )}
            {query.isError && (
              <div className="p-3 text-[12px] text-destructive">The streams could not be read.</div>
            )}
            {query.data !== undefined && <CommandEmpty>No stream matches.</CommandEmpty>}
            {sources.map(({ source, streams }) => (
              <CommandGroup key={source} heading={source}>
                {streams.map((stream) => {
                  const Icon = statusIcons[stream.status];
                  return (
                    <CommandItem
                      key={stream.pipelineId}
                      value={`${stream.flowName} ${stream.targetObject ?? ""} ${source}`}
                      data-testid="stream-switcher-option"
                      onSelect={() => {
                        setOpen(false);
                        if (stream.pipelineId !== pipelineId) {
                          navigate(`/datastreams/${stream.pipelineId}`);
                        }
                      }}
                    >
                      <Icon
                        className={cn("size-3.5 shrink-0", stream.status === "healthy" ? "text-muted-foreground/60" : statusInk[stream.status])}
                        aria-label={statusLabels[stream.status]}
                      />
                      <div className="min-w-0 flex-1 overflow-hidden">
                        <div className="truncate font-mono text-[12px]">{stream.flowName}</div>
                        {stream.targetObject !== null && (
                          <div className="truncate text-xs text-muted-foreground">{stream.targetObject}</div>
                        )}
                      </div>
                      {stream.pipelineId === pipelineId && <CheckIcon className="shrink-0" />}
                    </CommandItem>
                  );
                })}
              </CommandGroup>
            ))}
          </CommandList>
        </Command>
      </PopoverContent>
    </Popover>
  );
}
