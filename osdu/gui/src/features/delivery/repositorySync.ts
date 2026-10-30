/** What the pick and the watch on syncs read of a repository's source. */
export interface SyncableSource {
  id: string;
  name: string;
  enabled: boolean;
  lastSyncUtc: string | null;
}

/** Where the repository picked on the Delivery page is remembered between visits. */
const STORAGE_KEY = "sqlflow.osdu.sync-repository";

/** The repository picked on an earlier visit, or null when none was. */
export function rememberedRepository(): string | null {
  try {
    return window.localStorage.getItem(STORAGE_KEY);
  } catch {
    // Storage that cannot be read (a private window, blocked site data) only means nothing was remembered.
    return null;
  }
}

/** Remembers the repository picked, so the next visit is one press of Sync now away from syncing it again. */
export function rememberRepository(name: string): void {
  try {
    window.localStorage.setItem(STORAGE_KEY, name);
  } catch {
    // The pick still holds for this visit when it cannot be remembered.
  }
}

/**
 * The repository Sync now is offered for: the one picked while it can sync, else the first that can. The control plane
 * refuses a paused source, so one is never offered, and undefined means every repository is paused.
 */
export function pickedSource<T extends SyncableSource>(sources: T[], name: string | null): T | undefined {
  const enabled = sources.filter((source) => source.enabled);
  return enabled.find((source) => source.name === name) ?? enabled[0];
}

/**
 * A value that changes when a sync of any repository ends, whoever asked for it and however it ended, and when a
 * repository comes or goes: the control plane stamps `lastSyncUtc` once the sync's catalog work is done.
 */
export function syncsEnded(sources: SyncableSource[]): string {
  return sources.map((source) => `${source.id}:${source.lastSyncUtc ?? ""}`).join(" ");
}
