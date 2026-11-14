// What the Orphaned ledgers page says of a missing document (osdu/docs/reference/concepts/renaming-a-pipeline.md, Missing
// documents): what it left behind, where each of its ledgers would go under the flow picked to take it over, the totals the
// page heads with, which documents a search and a kind keep, and what a deletion of them would do and did. The shapes are
// the API's answer as far as these read it.

/** One orphaned ledger of a missing document, as the API lists it. */
export interface HeldLedger {
  ledger: string;
  partition: string;
  /** Empty for a flow in the single form, and for every kind but delivery. */
  interface: string;
  rows: number;
  claimed: number;
}

/** Where a takeover would hand one ledger: absent names mean it stays orphaned. */
export interface PlanItem {
  ledger: string;
  toInterface?: string | null;
  toLedgerName?: string | null;
}

/** A declared flow that can take a missing document over. */
export interface Candidate {
  flowName: string;
  relativePath: string;
  shares: string[];
  takes: number;
  suggested: boolean;
  plan: PlanItem[];
}

/** A missing document: a flow no repository declares any more, with what its ledgers hold and who can take them over. */
export interface MissingDocument {
  kind: string;
  flowName: string;
  relativePath?: string | null;
  rows: number;
  claimed: number;
  ledgers: HeldLedger[];
  candidates: Candidate[];
}

/** The kinds of flow that keep a ledger, in the order the page offers them. */
export const KINDS = ["delivery", "retrieval", "assertion", "dimension", "inventory", "download"] as const;

/** What each kind of ledger holds, by the rows the listing counts: one and many. */
const HELD: Record<string, [string, string]> = {
  delivery: ["record", "records"],
  retrieval: ["retrieval run", "retrieval runs"],
  assertion: ["assertion run", "assertion runs"],
  dimension: ["dimension", "dimensions"],
  inventory: ["inventory", "inventories"],
  download: ["download", "downloads"],
};

/** How each thing a flow shares with a missing document reads on its chip, and why it counts, on hover. */
export const SHARES: Record<string, { label: string; hint: string }> = {
  file: { label: "same file", hint: "Declared in the file the missing flow was last declared in: it was renamed and changed where it is." },
  mapping: { label: "same mapping", hint: "Pins a mapping document the missing flow pinned: it renders the same records." },
  "record table": { label: "same record table", hint: "Reads a record table the missing flow read." },
  interface: { label: "same interface", hint: "Has an interface of a name the missing flow had." },
  folder: { label: "same folder", hint: "Declared in the folder the missing flow was last declared in." },
};

const count = (value: number) => value.toLocaleString("en-US");

/** A number of what a kind's ledger holds, in words: `5,431 records`, `1 assertion run`. */
export function heldCount(kind: string, rows: number): string {
  const [one, many] = HELD[kind] ?? ["row", "rows"];
  return `${count(rows)} ${rows === 1 ? one : many}`;
}

/**
 * What a missing document, or one of its ledgers, left behind, in words: a delivery ledger's records and how many of them
 * claimed an OSDU id, or another kind's runs, dimensions or inventories.
 */
export function heldText(kind: string, rows: number, claimed: number): string {
  const held = heldCount(kind, rows);
  if (kind !== "delivery") {
    return held;
  }

  return claimed === rows
    ? `${held}, each with its OSDU id`
    : `${held}, ${count(claimed)} with an OSDU id`;
}

/** One orphaned ledger as a line names it: its interface and its partition, or the partition of a flow's one ledger. */
export function ledgerLabel(ledger: Pick<HeldLedger, "interface" | "partition">): string {
  return ledger.interface === "" ? ledger.partition : `${ledger.interface} in ${ledger.partition}`;
}

/** What tells one missing document from another on the page: its kind and its flow. */
export function documentKey(document: Pick<MissingDocument, "kind" | "flowName">): string {
  return `${document.kind}:${document.flowName}`;
}

/** The flow suggested to take the document over: the one that alone shares the most with it, if any. */
export function suggestedOf(document: Pick<MissingDocument, "candidates">): Candidate | undefined {
  return document.candidates.find((c) => c.suggested);
}

/**
 * Where a takeover by `candidate` would hand `ledger`: the flow's interface
 * (`flow/interface`), the flow itself for one ledger in the single form, or another kind's ledger name (`flow@partition`);
 * null when the flow keeps no counterpart of it and it stays orphaned.
 */
export function destinationOf(kind: string, candidate: Pick<Candidate, "flowName" | "plan">, ledger: Pick<HeldLedger, "ledger">): string | null {
  const item = candidate.plan.find((p) => p.ledger === ledger.ledger);
  if (item?.toLedgerName == null) {
    return null;
  }

  if (kind !== "delivery") {
    return item.toLedgerName;
  }

  return item.toInterface ? `${candidate.flowName}/${item.toInterface}` : candidate.flowName;
}

/** Whether a document is one a search (of its flow's name and file) and a kind keep. */
export function matches(document: Pick<MissingDocument, "kind" | "flowName" | "relativePath">, term: string, kind: string | null): boolean {
  if (kind !== null && document.kind !== kind) {
    return false;
  }

  const wanted = term.trim().toLowerCase();
  return wanted === ""
    || document.flowName.toLowerCase().includes(wanted)
    || (document.relativePath ?? "").toLowerCase().includes(wanted);
}

/** The facts the page heads with: how many documents, the delivery records and claimed ids they left, and how many have a suggestion. */
export function totals(documents: readonly Pick<MissingDocument, "kind" | "rows" | "claimed" | "candidates">[]) {
  const delivery = documents.filter((d) => d.kind === "delivery");
  return {
    documents: documents.length,
    records: delivery.reduce((sum, d) => sum + d.rows, 0),
    claimed: delivery.reduce((sum, d) => sum + d.claimed, 0),
    history: documents.filter((d) => d.kind !== "delivery").length,
    suggested: documents.filter((d) => suggestedOf(d) !== undefined).length,
  };
}

/** A missing document as a deletion reads it: where its ledgers are kept and when a sync last found it declared. */
export interface DeletableDocument {
  kind: string;
  flowName: string;
  leftUtc?: string | null;
  ledgers: Pick<HeldLedger, "partition">[];
}

/** One ledger as a deletion's preview answers it: `refusal` says why it stays, when it does. */
export interface PreviewedLedger {
  ledger: string;
  rows: number;
  refusal?: string | null;
}

/** What one ledger's deletion came to. */
export interface DeletedLedger {
  outcome: string;
  rows: number;
  summary: string;
}

/**
 * The one partition a deletion of `documents` acts in: the workbench's, else the one partition all their ledgers are kept
 * in. Null when the workbench names none and they are kept in several, since a deletion is made, and confirmed, a
 * partition at a time.
 */
export function deletePartition(documents: readonly Pick<DeletableDocument, "ledgers">[], workbench: string | null): string | null {
  if (workbench !== null) {
    return workbench;
  }

  const partitions = new Set(documents.flatMap((d) => d.ledgers.map((l) => l.partition)));
  return partitions.size === 1 ? [...partitions][0] : null;
}

/** How long after a flow left a deletion still asks whether it is coming back: a day. */
export const RECENT_MS = 24 * 60 * 60 * 1000;

/**
 * Whether a document that left at `leftMs` (its `leftUtc`, read as UTC) left so recently that it may be on its way back (a
 * repository synced a wrong branch, a file moved in two commits): its deletion says so first. A document whose departure
 * is not on record (null, or a time that does not read) never counts.
 */
export function leftRecently(leftMs: number | null, nowMs: number): boolean {
  return leftMs !== null && Number.isFinite(leftMs) && nowMs - leftMs < RECENT_MS;
}

/** What the previews of a deletion add up to: the ledgers that can go, what they hold by kind, and the ledgers that stay. */
export function deletionTotals(previews: readonly { kind: string; ledgers: readonly PreviewedLedger[] }[]) {
  const going = previews.flatMap((p) => p.ledgers.filter((l) => !l.refusal).map((l) => ({ kind: p.kind, rows: l.rows })));
  const held = new Map<string, number>();
  for (const ledger of going) {
    held.set(ledger.kind, (held.get(ledger.kind) ?? 0) + ledger.rows);
  }

  return {
    ledgers: going.length,
    staying: previews.reduce((sum, p) => sum + p.ledgers.filter((l) => l.refusal).length, 0),
    documents: previews.filter((p) => p.ledgers.some((l) => !l.refusal)).length,
    held: KINDS.filter((k) => held.has(k)).map((k) => heldCount(k, held.get(k) ?? 0)),
  };
}

/** What a deletion's button says it deletes: what one kind holds (`Delete 21 assertion runs`), else how many ledgers. */
export function deleteLabel(totals: { ledgers: number; held: string[] }): string {
  if (totals.ledgers === 0) {
    return "Delete";
  }

  return totals.held.length === 1 ? `Delete ${totals.held[0]}` : `Delete ${totals.ledgers.toLocaleString("en-US")} ledgers`;
}

/** What a deletion takes of a ledger of each kind, and what it leaves: one sentence each, for the kinds a dialog deletes. */
const DELETES: Record<string, string> = {
  delivery: "A delivery ledger goes only once OSDU holds none of its records, a line kept of each record.",
  retrieval: "Retrieval runs go; the files they wrote stay where they are.",
  assertion: "Assertion runs go with their results.",
  dimension: "Dimensions go with their values, changes and builds, their tables where no other partition writes them, and the flow's views.",
  inventory: "Inventories go with their runs, ids, selections and duplicate checks.",
  download: "Downloads go with their runs and the records they list; the files they wrote stay where they are.",
};

/** What deleting documents of `kinds` takes and leaves, in the order the page offers kinds, and that the audit trail stays. */
export function deleteEffect(kinds: readonly string[]): string {
  const said = KINDS.filter((k) => kinds.includes(k)).map((k) => DELETES[k]);
  return [...said, "Each ledger's audit trail stays, with this deletion in it as yours."].join(" ");
}

/** The heading of a deletion: the one document's ledger or ledgers, or the ledgers of several flows. */
export function deleteTitle(documents: readonly Pick<DeletableDocument, "ledgers">[]): string {
  if (documents.length === 1) {
    const ledgers = documents[0].ledgers.length;
    return ledgers === 1 ? "Delete the orphaned ledger" : `Delete ${ledgers} orphaned ledgers`;
  }

  return `Delete the orphaned ledgers of ${documents.length.toLocaleString("en-US")} flows`;
}

/** What one ledger's deletion came to, in a line: what went, that it waits, or why it stays. */
export function deletedText(kind: string, result: DeletedLedger): string {
  switch (result.outcome) {
    case "completed":
      return kind === "delivery"
        ? `${heldCount(kind, result.rows)} deleted, a line kept of ${result.rows === 1 ? "it" : "each"}.`
        : `${heldCount(kind, result.rows)} deleted, with all ${result.rows === 1 ? "it" : "they"} held.`;
    case "running":
      return `It waits (${result.summary}); the control plane finishes it.`;
    default:
      return result.summary;
  }
}

/** The Records page on one orphaned ledger, in its partition. */
export function recordsHref(ledger: Pick<HeldLedger, "ledger" | "partition">): string {
  return `/delivery/records?flow=${encodeURIComponent(ledger.ledger)}&partition=${encodeURIComponent(ledger.partition)}`;
}

/** The audit trail of one ledger, in its partition. */
export function trailHref(ledger: string, partition: string): string {
  return `/delivery/activity?flow=${encodeURIComponent(ledger)}&partition=${encodeURIComponent(partition)}`;
}
