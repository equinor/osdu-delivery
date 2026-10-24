// What the Orphaned ledgers page says of a missing document (osdu/docs/reference/concepts/renaming-a-pipeline.md, Missing
// documents): what it left behind, where each of its ledgers would go under the flow picked to take it over, the totals the
// page heads with, and which documents a search and a kind keep. The shapes are the API's answer as far as these read it.

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
export const KINDS = ["delivery", "retrieval", "assertion", "dimension", "inventory"] as const;

/** What each kind of ledger holds, by the rows the listing counts: one and many. */
const HELD: Record<string, [string, string]> = {
  delivery: ["record", "records"],
  retrieval: ["retrieval run", "retrieval runs"],
  assertion: ["assertion run", "assertion runs"],
  dimension: ["dimension", "dimensions"],
  inventory: ["inventory", "inventories"],
};

/** How each thing a flow shares with a missing document reads on its chip, and why it counts, on hover. */
export const SHARES: Record<string, { label: string; hint: string }> = {
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

/** The Records page on one orphaned ledger, in its partition. */
export function recordsHref(ledger: Pick<HeldLedger, "ledger" | "partition">): string {
  return `/delivery/records?flow=${encodeURIComponent(ledger.ledger)}&partition=${encodeURIComponent(ledger.partition)}`;
}

/** The audit trail of one ledger, in its partition. */
export function trailHref(ledger: string, partition: string): string {
  return `/delivery/activity?flow=${encodeURIComponent(ledger)}&partition=${encodeURIComponent(partition)}`;
}
