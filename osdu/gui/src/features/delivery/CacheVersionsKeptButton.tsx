import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Layers, Loader2 } from "lucide-react";
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
import {
  DEFAULT_VERSIONS_KEPT, MAX_VERSIONS_KEPT, MIN_VERSIONS_KEPT, daysText, versionsKeptOf, versionsText,
} from "./cacheVersionsKept";
import { problemText } from "./problemText";

/** How long the field waits after the last keystroke before it asks what the number would prune. */
const PREVIEW_DELAY_MS = 300;

/**
 * Sets how many versions of a partition's cache keep their records, the current one counted: an admin types the number,
 * sees what it prunes now under the partition's own days, and saves it. It is saved for the partition and applied at once,
 * then by every refresh. A version a delivery flow pins keeps its records whatever the number, a pruned version stays
 * listed under History with what it changed, and delivered records keep the cached values they were built from.
 */
export function CacheVersionsKeptButton({ scope, versionsKept, retentionDays }: {
  scope: string;
  /** The number the partition keeps now: what the field starts at. */
  versionsKept: number;
  /** The days a replaced version keeps its records at most, which the number never extends. */
  retentionDays: number;
}) {
  const { hasScope } = useAuth();
  const queryClient = useQueryClient();
  const [open, setOpen] = useState(false);
  const [typed, setTyped] = useState(String(versionsKept));
  const versions = versionsKeptOf(typed);
  const asked = useDebouncedValue(versions, PREVIEW_DELAY_MS);
  const settled = asked === versions;

  const preview = useQuery({
    queryKey: ["delivery", "cache", "keep-preview", scope, asked],
    queryFn: () => deliveryApi.cachePrunePreview({ scope, versionsKept: asked ?? versionsKept }),
    enabled: open && asked !== null,
    staleTime: 0,
  });
  const keep = useMutation({
    mutationFn: (count: number) => deliveryApi.cacheKeepVersions({ scope, versionsKept: count }),
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

  const result = settled && versions !== null ? preview.data : undefined;
  const prunes = result !== undefined && !result.deferred && result.pruned.length > 0 ? result.pruned.length : 0;
  const busy = keep.isPending;
  const unchanged = versions === versionsKept;

  return (
    <>
      <Button
        variant="outline"
        size="xs"
        onClick={() => {
          setTyped(String(versionsKept));
          setOpen(true);
        }}
        data-testid="delivery-cache-versions-kept"
      >
        <Layers />
        Set versions kept
      </Button>
      <AlertDialog
        open={open}
        onOpenChange={(next) => {
          if (!next && !busy) {
            setOpen(false);
          }
        }}
      >
        <AlertDialogContent className="max-w-lg" data-testid="delivery-cache-versions-kept-dialog">
          <AlertDialogHeader>
            <AlertDialogTitle>
              Versions kept by the cache of <span className="font-mono">{scope}</span>
            </AlertDialogTitle>
            <AlertDialogDescription>
              Every refresh keeps the records of the newest versions, the current one counted, and prunes the older ones. The
              number is saved for the partition and applied now. A version a delivery flow pins keeps its records whatever the
              number; a pruned version stays listed under History with what it changed, and its records cannot be brought back.
              Delivered records are not touched.
            </AlertDialogDescription>
          </AlertDialogHeader>

          <div className="flex flex-col gap-2">
            <label className="flex flex-wrap items-center gap-2 text-[13px]" htmlFor="delivery-cache-versions-kept-count">
              Keep the newest
              <Input
                id="delivery-cache-versions-kept-count"
                inputMode="numeric"
                value={typed}
                onChange={(event) => setTyped(event.target.value)}
                disabled={busy}
                aria-invalid={versions === null}
                className="h-8 w-24 font-mono"
                data-testid="delivery-cache-versions-kept-count"
              />
              versions
            </label>
            <span className="text-[12px] text-muted-foreground">
              From {MIN_VERSIONS_KEPT} to {MAX_VERSIONS_KEPT.toLocaleString()}; {DEFAULT_VERSIONS_KEPT} until set. A replaced version
              also loses its records {daysText(retentionDays)} after it was replaced, however many are kept.
            </span>
            <div className="min-h-10 rounded-md border border-border bg-muted/40 px-3 py-2 text-[12.5px]" role="status" data-testid="delivery-cache-versions-kept-preview">
              {versions === null
                ? <span className="text-destructive">Name a whole number of versions from {MIN_VERSIONS_KEPT} to {MAX_VERSIONS_KEPT.toLocaleString()}.</span>
                : preview.isError
                  ? <span className="text-muted-foreground">{problemText(preview.error)}</span>
                  : result === undefined
                    ? <Skeleton className="h-4 w-3/4" />
                    : (
                      <span className="flex flex-col gap-1">
                        <span>{result.summary}</span>
                        {prunes > 0 && (
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
            <Button variant="ghost" size="sm" onClick={() => setOpen(false)} disabled={busy} data-testid="delivery-cache-versions-kept-cancel">
              Cancel
            </Button>
            <Button
              variant={prunes > 0 ? "destructive" : "default"}
              size="sm"
              disabled={busy || versions === null || unchanged}
              onClick={() => {
                if (versions !== null) {
                  keep.mutate(versions);
                }
              }}
              data-testid="delivery-cache-versions-kept-confirm"
            >
              {busy && <Loader2 className="animate-spin" />}
              {versions === null
                ? "Keep"
                : prunes > 0
                  ? `Keep ${versionsText(versions)}, prune ${versionsText(prunes)}`
                  : `Keep ${versionsText(versions)}`}
            </Button>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </>
  );
}
