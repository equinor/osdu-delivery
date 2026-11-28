// The report on moving from one version of a kind to another, as the compare sheet takes it: what its file is named, and how
// it reaches the clipboard while the control plane is still writing it (osdu/docs/reference/concepts/template-versions.md).

/** A version of a kind in a release of the OSDU data definitions: one side of the report. */
export interface ReportSide {
  release: string;
  kind: string;
}

/** The schema version a kind ends with: 1.3.0 for osdu:wks:master-data--Wellbore:1.3.0. */
function schemaVersion(kind: string): string {
  return kind.slice(kind.lastIndexOf(":") + 1);
}

/** The entity a kind is of: Wellbore for osdu:wks:master-data--Wellbore:1.3.0. */
function entity(kind: string): string {
  const parts = kind.split(":");
  const entityType = parts.length >= 3 ? parts[2] : kind;
  const at = entityType.lastIndexOf("--");
  return at < 0 ? entityType : entityType.slice(at + 2);
}

/**
 * What a report's file is named after, in order: the entity, then each side's schema version and release, as the control
 * plane names the file it serves (Wellbore-1.0.0-v0.29.0-to-1.3.0-v0.30.0-report).
 */
export function reportFileParts(from: ReportSide, to: ReportSide): string[] {
  return [entity(to.kind), schemaVersion(from.kind), from.release, "to", schemaVersion(to.kind), to.release, "report"];
}

/** What a report says it is about, before it is taken: the move, and what the report tells of it. */
export function reportAbout(from: ReportSide, to: ReportSide): string {
  return `Moving from ${schemaVersion(from.kind)} to ${schemaVersion(to.kind)}, read against what each synced mapping of this kind writes: which changes are significant, what each mapping has to change, and what no mapping uses.`;
}

/** How much a change weighs for the synced mappings, as the comparison says it. */
export type ChangeWeight = "Significant" | "Minor" | "Unused";

/** The weights, heaviest first, as the sheet lists them. */
export const WEIGHTS: readonly ChangeWeight[] = ["Significant", "Minor", "Unused"];

/** How many changes there are of each weight; a change no mapping was weighed against counts as none of them. */
export function weightCounts(changes: readonly { weight: ChangeWeight | null }[]): Record<ChangeWeight, number> {
  const counts: Record<ChangeWeight, number> = { Significant: 0, Minor: 0, Unused: 0 };
  for (const change of changes) {
    if (change.weight !== null) {
      counts[change.weight] += 1;
    }
  }

  return counts;
}

/**
 * Puts text that is still being read on the clipboard. The clipboard is handed it while it is read, so the click that asked
 * for it still counts as the person's own when the text arrives; where the browser takes no pending item, or refuses one,
 * the text is written once it has come. A text that could not be read rejects with why, and nothing is written.
 */
export async function copyPending(text: Promise<string>): Promise<void> {
  if (typeof ClipboardItem !== "undefined" && typeof navigator.clipboard.write === "function") {
    try {
      await navigator.clipboard.write([new ClipboardItem({ "text/plain": text.then((value) => new Blob([value], { type: "text/plain" })) })]);
      return;
    } catch {
      // A text that could not be read fails here with why; a clipboard that refused only the pending item is tried again
      // below with the text itself, and its own refusal is what then fails.
      await text;
    }
  }

  await navigator.clipboard.writeText(await text);
}
