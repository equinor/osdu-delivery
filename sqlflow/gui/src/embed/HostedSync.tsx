import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Loader2, RefreshCw } from "lucide-react";
import { toast } from "sonner";
import { isApiError } from "@/api/client";
import { repoSourceApi } from "@/api/endpoints";
import type { RepoSource } from "@/api/types";
import { useAuth } from "@/auth/AuthContext";
import { ConfirmDialog } from "@/components/ConfirmDialog";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { heldCommit, syncNowConfirmText, syncNowMoves } from "../features/repos/autoSync";
import { announceSyncOutcome } from "../features/repos/syncOutcome";
import { SyncStanding } from "../features/repos/SyncStanding";
import { pageHost } from "./hostBridge";
import type { HostStart } from "./protocol";

/** The page the Repos page reads, under the same key: repositories are a handful per control plane. */
const FETCH_CAP = 200;

/** How often the list is read again while a sync runs, so the button stops turning soon after it ends. */
const SYNCING_REFETCH_MS = 2000;

const errorText = (error: unknown) => (isApiError(error) ? error.detail ?? error.title : String(error));

/** The environment a sync acts on, as a person tells one from another: its saved name and stage, or else its host. */
function placeOf(start: HostStart): string {
  if (start.environment !== null) {
    return `${start.environment.name} (${start.environment.stage})`;
  }

  if (start.apiBaseUrl === null) {
    return "this control plane";
  }

  try {
    return new URL(start.apiBaseUrl).host;
  } catch {
    return start.apiBaseUrl;
  }
}

/** What syncing a source does, said before it is done. */
function syncText(source: RepoSource, where: string): string {
  if (!source.enabled) {
    return `${source.name} is paused on ${where}, so it does not sync. Resume it on the Repos page.`;
  }

  return syncNowMoves(source)
    ? `Moves ${source.name} on ${where} from ${heldCommit(source)} to the latest commit of ${source.branch}, after you confirm.`
    : `Reads the latest commit of ${source.name} (${source.branch}) into ${where}.`;
}

/** The icon button the control is drawn as, turning while a sync runs. */
function SyncGlyph({ syncing }: { syncing: boolean }) {
  return syncing ? <Loader2 className="size-4 animate-spin" /> : <RefreshCw className="size-4" />;
}

const BUTTON = "flex size-8 shrink-0 items-center justify-center rounded-md text-muted-foreground hover:bg-sidebar-accent hover:text-sidebar-accent-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring disabled:pointer-events-none disabled:opacity-50";

/**
 * Syncs a repository on the control plane the editor is signed in to, from the side bar's header: the control plane
 * reads the latest commit of the repository's branch, and its flows run from that commit afterwards. Syncing is how a
 * commit reaches an environment, so the button names the control plane it acts on, and a repository that holds its
 * commit until asked moves only after the person confirms, in the words the Repos page confirms it with. One repository
 * is synced by the button itself; several are picked from its menu, each with how its last sync went.
 */
export function HostedSync({ start }: { start: HostStart }) {
  const queryClient = useQueryClient();
  const { hasScope } = useAuth();
  const canOperate = hasScope("operate");
  const [confirming, setConfirming] = useState<RepoSource | null>(null);
  const where = placeOf(start);

  // Read when the side bar opens and whenever it is asked to sync, and every two seconds only while a sync runs: the
  // side bar stays open all day, and a list that only matters at a click is not read all day.
  const sources = useQuery({
    queryKey: ["repo-sources", "list", FETCH_CAP],
    queryFn: () => repoSourceApi.list({ page: 1, pageSize: FETCH_CAP }),
    refetchInterval: (query) => (query.state.data?.items.some((source) => source.syncPending) ? SYNCING_REFETCH_MS : false),
  });

  const syncNow = useMutation({
    mutationFn: (source: RepoSource) => repoSourceApi.syncNow(source.id),
    onSuccess: (synced) => {
      announceSyncOutcome(synced);
      void queryClient.invalidateQueries({ queryKey: ["repo-sources"] });
      // What the editor itself keeps of the control plane (a flow's lenses, what a file is compared with) is read again.
      pageHost().send({ type: "act", action: "refreshEstate" });
    },
    onError: (error) => toast.error(`The sync could not be started: ${errorText(error)}`),
  });

  /** Syncs a source as it stands now: a source read a while ago may have moved since, and the confirmation names its commit. */
  const ask = async (picked: RepoSource) => {
    const read = await sources.refetch();
    const source = read.data?.items.find((item) => item.id === picked.id) ?? picked;
    if (!source.enabled || source.syncPending) {
      return;
    }

    if (syncNowMoves(source)) {
      setConfirming(source);
    } else {
      syncNow.mutate(source);
    }
  };

  const items = sources.data?.items ?? [];
  const syncing = items.some((source) => source.syncPending) || syncNow.isPending;
  const confirm = (
    <ConfirmDialog
      open={confirming !== null}
      title={`Sync ${confirming?.name ?? "the repository"} on ${where}`}
      message={confirming === null ? "" : syncNowConfirmText(confirming)}
      confirmLabel="Sync now"
      onConfirm={() => {
        if (confirming !== null) {
          syncNow.mutate(confirming);
        }

        setConfirming(null);
      }}
      onClose={() => setConfirming(null)}
    />
  );

  // Why the button does nothing, when it does nothing: said on the button, never by leaving it out.
  const blocked = sources.isError
    ? `The repositories on ${where} could not be read: ${errorText(sources.error)}`
    : sources.data === undefined
      ? `Reading the repositories on ${where}.`
      : !canOperate
        ? "Syncing a repository takes the operate scope."
        : items.length === 0
          ? `No repository is registered on ${where}. Register one on the Repos page.`
          : null;

  if (blocked !== null || items.length === 1) {
    const only = blocked === null ? items[0] : null;
    return (
      <>
        <Tooltip>
          <TooltipTrigger asChild>
            {/* A disabled button takes no pointer, so the tooltip sits on a span that does. */}
            <span className="inline-flex shrink-0" tabIndex={blocked === null ? undefined : 0}>
              <button
                type="button"
                aria-label={only === null ? "Sync a repository" : `Sync ${only.name}`}
                disabled={only === null || !only.enabled || syncing}
                onClick={() => {
                  if (only !== null) {
                    void ask(only);
                  }
                }}
                className={BUTTON}
                data-testid="side-bar-sync"
              >
                <SyncGlyph syncing={syncing} />
              </button>
            </span>
          </TooltipTrigger>
          <TooltipContent side="bottom" className="max-w-72">
            {only === null ? blocked : (
              <>
                <span className="block">{syncing ? `Syncing ${only.name} on ${where}.` : syncText(only, where)}</span>
                <span className="mt-1 block text-muted-foreground"><SyncStanding source={only} /></span>
              </>
            )}
          </TooltipContent>
        </Tooltip>
        {confirm}
      </>
    );
  }

  return (
    <>
      <DropdownMenu>
        <Tooltip>
          <TooltipTrigger asChild>
            <DropdownMenuTrigger aria-label="Sync a repository" disabled={syncing} className={BUTTON} data-testid="side-bar-sync">
              <SyncGlyph syncing={syncing} />
            </DropdownMenuTrigger>
          </TooltipTrigger>
          <TooltipContent side="bottom">{syncing ? `A repository is syncing on ${where}.` : `Sync a repository on ${where}`}</TooltipContent>
        </Tooltip>
        <DropdownMenuContent align="end" className="w-72">
          <DropdownMenuLabel className="font-normal text-muted-foreground">{`Read the latest commit into ${where}`}</DropdownMenuLabel>
          <DropdownMenuSeparator />
          {items.map((source) => (
            <DropdownMenuItem
              key={source.id}
              disabled={!source.enabled}
              onSelect={() => void ask(source)}
              title={syncText(source, where)}
              data-testid={`side-bar-sync-${source.name}`}
            >
              <span className="flex min-w-0 flex-col gap-0.5">
                <span className="truncate font-mono text-[13px] font-medium">{source.name}</span>
                <span className="text-[11px] text-muted-foreground"><SyncStanding source={source} /></span>
              </span>
            </DropdownMenuItem>
          ))}
        </DropdownMenuContent>
      </DropdownMenu>
      {confirm}
    </>
  );
}
