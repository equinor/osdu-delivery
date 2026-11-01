import { useState } from "react";
import type { DeliveryDryRunOutcome } from "../../api/delivery";
import type { StagedLine } from "./dryRun";
import type { ValueCheckSession } from "./useValueCheck";
import { flowKey } from "./valueCheck";

/** What a dry run's list narrows to; null for every row. */
export type StagedNarrowing = DeliveryDryRunOutcome | null;

/** The pages of the section beside the records: the record as rendered, the mapping as written, the shape it gives, what a dry run found. */
export type StagedSection = "render" | "yaml" | "shape" | "findings";

/** What the reader has picked beside the records, kept with the page so moving to Properties and back loses none of it. */
export interface StagedView {
  /** The record the reader picked; none shows the first one listed. */
  picked: StagedLine | null;
  /** Picks a record and shows it, from whichever page of the section the reader was on. */
  pick: (line: StagedLine | null) => void;
  narrowing: StagedNarrowing;
  narrow: (narrowing: StagedNarrowing) => void;
  typed: string;
  type: (text: string) => void;
  /** The page of the section the reader chose; null until they have. */
  section: StagedSection | null;
  show: (section: StagedSection) => void;
}

/** The reader's place beside the records: which record, which outcome, what search and which page; another flow's rows start afresh. */
export function useStagedView(session: ValueCheckSession): StagedView {
  const [picked, setPicked] = useState<StagedLine | null>(null);
  const [narrowing, setNarrowing] = useState<StagedNarrowing>(null);
  const [typed, type] = useState("");
  const [section, show] = useState<StagedSection | null>(null);
  const shown = session.flow === null ? null : flowKey(session.flow);
  const [seen, setSeen] = useState(shown);
  if (shown !== seen) {
    setSeen(shown);
    setPicked(null);
    setNarrowing(null);
    type("");
    show(null);
  }

  // A dry run that lands opens on what it found, which is what the reader pressed it for.
  const run = session.dryRun?.meta.taskId ?? null;
  const [seenRun, setSeenRun] = useState(run);
  if (run !== seenRun) {
    setSeenRun(run);
    if (run !== null) {
      show("findings");
    }
  }

  return {
    picked,
    pick: (line) => {
      setPicked(line);
      show("render");
    },
    narrowing,
    narrow: (next) => {
      setNarrowing(next);
      setPicked(null);
      show("render");
    },
    typed,
    type,
    section,
    show,
  };
}
