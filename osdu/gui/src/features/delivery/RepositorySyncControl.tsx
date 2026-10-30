import { useEffect, useRef, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { CircleAlert, Loader2, RefreshCw } from "lucide-react";
import { toast } from "sonner";
import { isApiError } from "@/api/client";
import { repoSourceApi } from "@/api/endpoints";
import type { RepoSource } from "@/api/types";
import { useAuth } from "@/auth/AuthContext";
import { RelativeTime } from "@/components/RelativeTime";
import { Button } from "@/components/ui/button";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { announceSyncOutcome, repoSourcesRefetchInterval } from "@/features/repos/syncOutcome";
import { useSyncTracePanel } from "@/features/repos/useSyncTracePanel";
import { pickedSource, rememberedRepository, rememberRepository, syncsEnded } from "./repositorySync";

/** The page the Repos page reads, under the same key: repositories are a handful per control plane. */
const FETCH_CAP = 200;

const errorText = (error: unknown) => (isApiError(error) ? error.detail ?? error.title : String(error));

/** How a repository's last sync went, under its name in the list. */
function SyncStanding({ source }: { source: RepoSource }) {
  if (!source.enabled) {
    return <span>paused, so it does not sync</span>;
  }

  if (source.syncPending) {
    return <span>syncing</span>;
  }

  if (source.lastError !== null) {
    return <span className="text-destructive">the last sync failed</span>;
  }

  if (source.lastSyncUtc === null) {
    return <span>not synced yet</span>;
  }

  return (
    <span>
      synced <RelativeTime value={source.lastSyncUtc} absolute={false} />
      {source.lastSyncedSha !== null && <>, <span className="font-mono">{source.lastSyncedSha.slice(0, 10)}</span></>}
    </span>
  );
}

/**
 * Syncs a repository from the Delivery page (osdu/docs/reference/concepts/gui.md, Delivery), so a commit reaches the flows
 * without a trip to the Repos page: the same sync, toast and trace as that page's Sync now, for the repository picked here.
 * The pick is remembered. The page reads its flows and counts again whenever a sync ends, which also covers a sync that
 * outlasted the control plane's wait and the background sync. Nothing shows while no repository is registered: the page
 * says how to register one.
 */
export function RepositorySyncControl() {
  const queryClient = useQueryClient();
  const openSyncTrace = useSyncTracePanel();
  const { hasScope } = useAuth();
  const canOperate = hasScope("operate");
  const [picked, setPicked] = useState(rememberedRepository);

  const sources = useQuery({
    queryKey: ["repo-sources", "list", FETCH_CAP],
    queryFn: () => repoSourceApi.list({ page: 1, pageSize: FETCH_CAP }),
    refetchInterval: (query) => repoSourcesRefetchInterval(query.state.data),
  });

  const syncNow = useMutation({
    mutationFn: (source: RepoSource) => repoSourceApi.syncNow(source.id),
    onSuccess: (synced) => {
      announceSyncOutcome(synced);
      void queryClient.invalidateQueries({ queryKey: ["repo-sources"] });
      void queryClient.invalidateQueries({ queryKey: ["repos"] });
    },
    onError: (error) => toast.error(errorText(error)),
  });

  const ended = sources.data === undefined ? undefined : syncsEnded(sources.data.items);
  const seen = useRef(ended);
  useEffect(() => {
    if (ended === undefined) {
      return;
    }

    if (seen.current !== undefined && seen.current !== ended) {
      void queryClient.invalidateQueries({ queryKey: ["pipelines"] });
      void queryClient.invalidateQueries({ queryKey: ["delivery"] });
    }

    seen.current = ended;
  }, [ended, queryClient]);

  if (sources.data === undefined) {
    return sources.isError
      ? (
        <span className="text-[12px] text-destructive" title={errorText(sources.error)} data-testid="delivery-repo-sync-unread">
          Repositories could not be read
        </span>
      )
      : null;
  }

  if (sources.data.items.length === 0) {
    return null;
  }

  const source = pickedSource(sources.data.items, picked);
  const syncing = source !== undefined && (source.syncPending || (syncNow.isPending && syncNow.variables?.id === source.id));
  return (
    <div className="flex items-center gap-1.5" data-testid="delivery-repo-sync">
      <Select
        value={source?.name ?? ""}
        onValueChange={(name) => {
          rememberRepository(name);
          setPicked(name);
        }}
      >
        <SelectTrigger size="sm" className="w-44 font-mono text-[12px]" title={source?.name} aria-label="Repository to sync" data-testid="delivery-repo-sync-pick">
          <SelectValue placeholder="Repository"><span className="truncate">{source?.name}</span></SelectValue>
        </SelectTrigger>
        <SelectContent position="popper" align="start" className="max-w-96 min-w-72">
          {sources.data.items.map((item) => (
            <SelectItem key={item.id} value={item.name} disabled={!item.enabled} data-testid={`delivery-repo-sync-option-${item.name}`}>
              <span className="flex min-w-0 flex-col gap-0.5">
                <span className="truncate font-mono text-[13px] font-medium">{item.name}</span>
                <span className="text-[11px] text-muted-foreground"><SyncStanding source={item} /></span>
              </span>
            </SelectItem>
          ))}
        </SelectContent>
      </Select>
      {source !== undefined && source.lastError !== null && !syncing && (
        <Tooltip>
          <TooltipTrigger asChild>
            <button
              type="button"
              className="cursor-pointer"
              aria-label={`The last sync of ${source.name} failed: open its trace`}
              onClick={() => openSyncTrace(source.id, source.name)}
              data-testid="delivery-repo-sync-error"
            >
              <CircleAlert className="size-4 text-destructive" />
            </button>
          </TooltipTrigger>
          <TooltipContent className="max-w-lg break-words">
            {source.lastError}
            <span className="mt-1 block text-muted-foreground">Click to open the sync trace.</span>
          </TooltipContent>
        </Tooltip>
      )}
      <Button
        variant="outline"
        size="sm"
        className="min-w-28"
        disabled={source === undefined || syncing || !canOperate}
        title={source === undefined
          ? "Every repository is paused. Resume one on the Repos page to sync it."
          : `Sync ${source.name} now. This page reads its flows again when the sync ends.`}
        onClick={() => {
          if (source !== undefined) {
            syncNow.mutate(source);
            openSyncTrace(source.id, source.name);
          }
        }}
        data-testid="delivery-repo-sync-now"
      >
        {syncing ? <Loader2 className="animate-spin" /> : <RefreshCw />}
        {syncing ? "Syncing" : "Sync now"}
      </Button>
    </div>
  );
}
