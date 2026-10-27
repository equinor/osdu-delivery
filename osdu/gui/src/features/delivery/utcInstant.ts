// Whether a UTC instant the control plane sends has passed. The API sends a DateTime the ledger read back without a zone
// (EF reads datetime2 as DateTimeKind.Unspecified), and Date.parse reads a date-time without a zone as the browser's local
// time: east of UTC, a lease its worker still renews read as run out. parseUtc reads it as the UTC it is.
import { parseUtc } from "../../../../../sqlflow/gui/src/lib/time.ts";

/** Whether `instantUtc` is at or before `nowMs`; false for no instant, or one that does not parse. */
export function hasPassed(instantUtc: string | null, nowMs: number): boolean {
  if (instantUtc === null) {
    return false;
  }

  const at = parseUtc(instantUtc).getTime();
  return Number.isFinite(at) && nowMs >= at;
}
