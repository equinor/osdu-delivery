import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Archive, Loader2 } from "lucide-react";
import {
  AlertDialog,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { useAuth } from "@/auth/AuthContext";
import { useDebouncedValue } from "@/hooks/useDebouncedValue";
import { deliveryApi } from "../../api/delivery";
import { daysText, versionsText } from "./cacheVersionsKept";
import { problemText } from "./problemText";

/** The longest a purge keeps, in days, as the module bounds it. */
const MAX_KEEP_DAYS = 36500;

/** How long the field waits after the last keystroke before it asks what the purge would prune. */
const PREVIEW_DELAY_MS = 300;

/** The days typed, when they are a whole number a purge takes; null otherwise. */
function keepDaysOf(text: string): number | null {
  const trimmed = text.trim();
  if (!/^\d{1,5}$/.test(trimmed)) {
    return null;
  }

  const days = Number(trimmed);
  return days <= MAX_KEEP_DAYS ? days : null;
}

/**
 * Purges the history of a partition's cache now, rather than at the next refresh: an admin names how many days of replaced
 * versions keep their records (the partition's retention to start with), sees what that prunes, and confirms. It is the
 * retention every refresh applies, with the number of versions the partition keeps, so the current version, the one it
 * replaced and every pinned version keep theirs whatever the days, a pruned version stays listed with what it changed, and
 * it records who pruned it.
 */
export function CachePruneButton({ scope, retentionDays, versionsKept }: {
  scope: string;
  /** The retention the partition keeps: what the field starts at. */
  retentionDays: number;
  /** How many versions the partition keeps, which the purge keeps at most too; unknown to an older control plane. */
  versionsKept?: number;
}) {
  const { hasScope } = useAuth();
  const queryClient = useQueryClient();
  const [open, setOpen] = useState(false);
  const [typed, setTyped] = useState(String(retentionDays));
  const keepDays = keepDaysOf(typed);
  const asked = useDebouncedValue(keepDays, PREVIEW_DELAY_MS);
  const settled = asked === keepDays;

  const preview = useQuery({
    queryKey: ["delivery", "cache", "prune-preview", scope, asked],
    queryFn: () => deliveryApi.cachePrunePreview({ scope, keepDays: asked ?? 0 }),
    enabled: open && asked !== null,
    staleTime: 0,
  });
  const prune = useMutation({
    mutationFn: (days: number) => deliveryApi.cachePrune({ scope, keepDays: days }),
    onSuccess: (result) => {
      toast.success(result.summary);
      setOpen(false);
      void queryClient.invalidateQueries({ queryKey: ["delivery", "cache"] });
    },
    onError: (error) => toast.error(problemText(error)),
  });

  if (!hasScope("admin")) {
    return null;
  }

  const result = settled && keepDays !== null ? preview.data : undefined;
  const prunes = result !== undefined && !result.deferred && result.pruned.length > 0;
  const busy = prune.isPending;

  return (
    <>
      <Button
        variant="outline"
        size="xs"
        onClick={() => {
          setTyped(String(retentionDays));
          setOpen(true);
        }}
        data-testid="delivery-cache-prune"
      >
        <Archive />
        Prune history
      </Button>
      <AlertDialog
        open={open}
        onOpenChange={(next) => {
          if (!next && !busy) {
            setOpen(false);
          }
        }}
      >
        <AlertDialogContent className="max-w-lg" data-testid="delivery-cache-prune-dialog">
          <AlertDialogHeader>
            <AlertDialogTitle>
              Prune the cache history of <span className="font-mono">{scope}</span>?
            </AlertDialogTitle>
            <AlertDialogDescription>
              Removes the records of every version replaced longer ago than the days below
              {versionsKept === undefined ? "" : `, and of every version older than the newest ${versionsText(versionsKept)} the partition keeps`}.
              The current version, the one it replaced and every version a delivery flow pins keep theirs. A pruned version
              stays listed with what it changed, and its records cannot be brought back; delivered records are not touched.
            </AlertDialogDescription>
          </AlertDialogHeader>

          <div className="flex flex-col gap-2">
            <label className="flex flex-wrap items-center gap-2 text-[13px]" htmlFor="delivery-cache-prune-days">
              Keep history for
              <Input
                id="delivery-cache-prune-days"
                inputMode="numeric"
                value={typed}
                onChange={(event) => setTyped(event.target.value)}
                disabled={busy}
                aria-invalid={keepDays === null}
                className="h-8 w-24 font-mono"
                data-testid="delivery-cache-prune-days"
              />
              days
            </label>
            <span className="text-[12px] text-muted-foreground">
              Every refresh keeps a replaced version {daysText(retentionDays)}; 0 keeps only the versions that are always kept.
            </span>
            <div className="min-h-10 rounded-md border border-border bg-muted/40 px-3 py-2 text-[12.5px]" role="status" data-testid="delivery-cache-prune-preview">
              {keepDays === null
                ? <span className="text-destructive">Name a whole number of days from 0 to {MAX_KEEP_DAYS.toLocaleString()}.</span>
                : preview.isError
                  ? <span className="text-destructive">{problemText(preview.error)}</span>
                  : result === undefined
                    ? <Skeleton className="h-4 w-3/4" />
                    : (
                      <span className="flex flex-col gap-1">
                        <span>{result.summary}</span>
                        {prunes && (
                          <span className="font-mono text-[11.5px] text-muted-foreground">
                            {result.pruned.length <= 4
                              ? result.pruned.join(", ")
                              : `${result.pruned.slice(0, 2).join(", ")} ... ${result.pruned.slice(-2).join(", ")}`}
                          </span>
                        )}
                      </span>
                    )}
            </div>
          </div>

          <AlertDialogFooter>
            <Button variant="ghost" size="sm" onClick={() => setOpen(false)} disabled={busy} data-testid="delivery-cache-prune-cancel">
              Cancel
            </Button>
            <Button
              variant="destructive"
              size="sm"
              disabled={busy || !prunes || keepDays === null}
              onClick={() => {
                if (keepDays !== null) {
                  prune.mutate(keepDays);
                }
              }}
              data-testid="delivery-cache-prune-confirm"
            >
              {busy && <Loader2 className="animate-spin" />}
              {prunes ? `Prune ${result.pruned.length.toLocaleString()} version${result.pruned.length === 1 ? "" : "s"}` : "Prune"}
            </Button>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </>
  );
}
