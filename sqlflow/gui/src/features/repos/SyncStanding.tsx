import type { RepoSource } from "../../api/types";
import { RelativeTime } from "../../components/RelativeTime";
import { heldCommit, syncsOnRequest } from "./autoSync";

/**
 * How a repository's last sync went, in a few words under its name wherever a sync control lists repositories to sync:
 * paused, syncing, failed, not yet synced, or when it synced and the commit it holds.
 */
export function SyncStanding({ source }: { source: RepoSource }) {
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

  const held = heldCommit(source);
  return (
    <span>
      synced <RelativeTime value={source.lastSyncUtc} absolute={false} />
      {held !== null && <>, <span className="font-mono">{held}</span></>}
      {syncsOnRequest(source) && <>, syncs on request</>}
    </span>
  );
}
