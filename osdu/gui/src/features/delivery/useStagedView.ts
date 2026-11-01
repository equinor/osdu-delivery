import { useState } from "react";
import type { DeliveryDryRunOutcome } from "../../api/delivery";
import type { StagedLine } from "./dryRun";
import type { ValueCheckSession } from "./useValueCheck";
import { flowKey } from "./valueCheck";

/** What a dry run's list narrows to; null for every row. */
export type StagedNarrowing = DeliveryDryRunOutcome | null;

/** What the reader has picked on the Staged records tab, kept with the sheet so moving to another tab and back loses none of it. */
export interface StagedView {
  picked: StagedLine | null;
  pick: (line: StagedLine | null) => void;
  narrowing: StagedNarrowing;
  narrow: (narrowing: StagedNarrowing) => void;
  typed: string;
  type: (text: string) => void;
}

/** The reader's place on the tab: which record, which outcome and what search; another flow's rows start afresh. */
export function useStagedView(session: ValueCheckSession): StagedView {
  const [picked, pick] = useState<StagedLine | null>(null);
  const [narrowing, narrow] = useState<StagedNarrowing>(null);
  const [typed, type] = useState("");
  const shown = session.flow === null ? null : flowKey(session.flow);
  const [seen, setSeen] = useState(shown);
  if (shown !== seen) {
    setSeen(shown);
    pick(null);
    narrow(null);
    type("");
  }

  return { picked, pick, narrowing, narrow, typed, type };
}
