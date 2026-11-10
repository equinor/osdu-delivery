import { useEffect, useRef, useState, type ReactNode } from "react";
import { useMutation } from "@tanstack/react-query";
import { toast } from "sonner";
import type { DeliveryOsduRead } from "../../api/delivery";
import { failureText } from "./answers";
import { OsduRecordInspector, type InspectorEntry, type InspectorExtras } from "./OsduRecordInspector";

/** How many records can be opened one from the other before the trail refuses to grow. */
const MAX_TRAIL = 8;

/** How a record other than the page's own is read: by its id, at its latest or at one version, answered at once. */
export type ReadRecord = (id: string, version?: number) => Promise<DeliveryOsduRead>;

/** The page's own read of its record, as it stands: its answer once there is one, or why there is none. */
export interface RootRead {
  read: DeliveryOsduRead | undefined;
  /** A refusal of the read itself (the control plane's problem, or OSDU's, with its words). */
  error?: unknown;
  /** Whether the read is under way. */
  pending: boolean;
}

/** A record to open after the page's own, as a click on the value of it naming the record would: its id, and that value's path. */
export interface RecordFollow {
  id: string;
  from: string;
}

/**
 * A record read from OSDU, and the records opened from links in it, read in turn the way the page reads records (a record
 * page through its flow's route and credentials, the explorer through its partition's connection), each answered by the
 * control plane at once. They form a trail (the page's record, then each record opened from the one before) shown in one
 * inspector, the last one in view; stepping back along the trail closes what was opened after that point. A version of
 * any record on the trail is read into the inspector in place, without disturbing the trail.
 */
export function OsduRecordPanel({ readLinked, root, targetId, readRootVersion, ledgerVersion, actions, extras, follow = null, onFollowed, fill = false }: {
  /** Reads a record opened from the page's, and a version of one; null where nothing more can be read. */
  readLinked: ReadRecord | null;
  /** The page's own read, as it stands. */
  root: RootRead;
  /** The id the page's read is for, so the trail names it before the read has answered. */
  targetId: string;
  /** Reads the page's own record at one of its versions; absent where it cannot be asked for. */
  readRootVersion?: (version: number) => Promise<DeliveryOsduRead>;
  /** The version the ledger holds as delivered by this flow, marked in the page record's version list. */
  ledgerVersion?: number | null;
  /** The page's own controls over the read, shown on the inspector's header row. */
  actions?: ReactNode;
  /** What the page adds to the inspector: the place in front of the record, its mentions, what to offer for an id not found. */
  extras?: InspectorExtras;
  /**
   * A record to open on the trail right after the page's own, closing whatever was open after it: asked once, as it is
   * given, and `onFollowed` is told so the page can let it go. The explorer's dimension builder opens the record a key
   * names so.
   */
  follow?: RecordFollow | null;
  onFollowed?: () => void;
  /** Whether the inspector fills its flex column parent (a window of its own) rather than a share of the viewport. */
  fill?: boolean;
}) {
  // Each record opened from a link is on the trail with what its read answered; the link it was opened from shows the
  // read under way until then.
  const [trail, setTrail] = useState<{ id: string; read: DeliveryOsduRead; from: string | null }[]>([]);
  const [opening, setOpening] = useState<string | null>(null);
  const open = useMutation({
    mutationFn: ({ id }: { id: string; level: number; from: string | null }) => readLinked!(id),
    onMutate: (asked) => setOpening(asked.id),
    // A record opened from the one at level N takes place N+1 and closes everything that was after it. The trail is
    // bounded: past its length the request is refused rather than the first records quietly dropped.
    onSuccess: (read, asked) => setTrail((was) => [...was.slice(0, asked.level), { id: asked.id, read, from: asked.from }]),
    onError: (error) => toast.error(failureText(error)),
    onSettled: () => setOpening(null),
  });

  // A record the page asks to open is opened once: the request itself is remembered, so a render that still carries it
  // (or React's second run of an effect in development) does not read it again.
  const { mutate: openLink } = open;
  const followed = useRef<RecordFollow | null>(null);
  useEffect(() => {
    if (follow === null || followed.current === follow || readLinked === null) {
      return;
    }

    followed.current = follow;
    openLink({ id: follow.id, level: 0, from: follow.from });
    onFollowed?.();
  }, [follow, readLinked, openLink, onFollowed]);

  const entries: InspectorEntry[] = [
    { id: targetId, read: root.read, error: root.error, pending: root.pending },
    ...trail.map((link) => ({ id: link.id, read: link.read, pending: false, from: link.from })),
  ];
  const canOpen = readLinked !== null;

  return (
    <OsduRecordInspector
      entries={entries}
      ledgerVersion={ledgerVersion}
      opening={opening}
      onOpenLink={canOpen
        ? (level, id, from) => {
          if (level + 1 >= MAX_TRAIL) {
            toast.error(`Up to ${MAX_TRAIL} records open one from the other; step back along the trail to follow this one.`);
            return;
          }

          open.mutate({ id, level, from });
        }
        : undefined}
      readVersionAt={(level) => (level === 0
        ? readRootVersion
        : canOpen ? (version) => readLinked(entries[level].id, version) : undefined)}
      onBack={(to) => setTrail((was) => was.slice(0, to))}
      actions={actions}
      extras={extras}
      fill={fill}
    />
  );
}
