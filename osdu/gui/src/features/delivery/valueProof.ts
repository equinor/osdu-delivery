/** The most characters a list of a proof's request comes to as JSON, under what a task argument carries (4,000). */
const MAX_LIST_JSON = 3900;

/** A piece of an explanation: text, or code the server set in backticks. */
export interface CodePart {
  text: string;
  code: boolean;
}

/**
 * An explanation's text in pieces, the code the server set in backticks apart from the words around it. A backtick left
 * open closes at the end, so no text is lost.
 */
export function codeParts(text: string): CodePart[] {
  return text.split("`").map((piece, i) => ({ text: piece, code: i % 2 === 1 })).filter((part) => part.text.length > 0);
}

/** What a proof request is made of: a finding as the check answered it, its cause of whatever shape the API gives it. */
export interface ProvableFinding<C> {
  values: readonly { value: string }[];
  causes?: readonly C[];
  sourceValues?: readonly { column: string; value: string }[];
}

/** The request of a finding's proof (the API's `DeliveryValueProofRequest`). */
export interface ProofRequest<C, V> {
  target: string;
  values: V;
  cause: C;
  sourceValues?: { column: string; value: string }[];
  looked?: string[];
  cacheVersion: string | null;
  mapping?: string;
  run?: "values" | "records" | "cache";
}

/** What a proof request carries of a finding, and how much of what the check listed it fits. */
export interface ProofAsk<C, V> {
  request: ProofRequest<C, V>;
  /** The source values sent, and how many the check listed. */
  sourceValues: { sent: number; listed: number };
  /** The values looked for sent, and how many the check listed. */
  looked: { sent: number; listed: number };
}

/** The first items of a list whose JSON fits a request's argument, in the order given. */
function fitting<T>(items: readonly T[]): T[] {
  const kept: T[] = [];
  let length = 2;
  for (const item of items) {
    length += JSON.stringify(item).length + 1;
    if (length > MAX_LIST_JSON) {
      break;
    }

    kept.push(item);
  }

  return kept;
}

/**
 * The proof request of a finding as the check that found it answered it: its first cause, the source values behind it and the
 * values looked for (as many of each as a request carries, the most frequent first), the scope's values and the cache version
 * the check read, and the mapping it was asked of. Null when the finding says nothing of its cause (an older node).
 */
export function proofRequest<C, V>(
  finding: ProvableFinding<C>,
  target: string,
  check: { values: V; cacheVersion: string | null },
  mapping: string | null,
  run?: ProofRequest<C, V>["run"],
): ProofAsk<C, V> | null {
  const cause = finding.causes?.[0];
  if (cause === undefined) {
    return null;
  }

  const listedSource = (finding.sourceValues ?? []).map((value) => ({ column: value.column, value: value.value }));
  const listedLooked = finding.values.map((value) => value.value);
  const sourceValues = fitting(listedSource);
  const looked = fitting(listedLooked);
  return {
    request: {
      target,
      values: check.values,
      cause,
      sourceValues: sourceValues.length > 0 ? sourceValues : undefined,
      looked: looked.length > 0 ? looked : undefined,
      cacheVersion: check.cacheVersion,
      mapping: mapping ?? undefined,
      run,
    },
    sourceValues: { sent: sourceValues.length, listed: listedSource.length },
    looked: { sent: looked.length, listed: listedLooked.length },
  };
}
