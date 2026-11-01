/** What picking the failing rows reads of a finding. */
interface FindingKeys {
  samples: readonly { keyJson?: string | null }[];
}

/** The most characters the keys of a query come to as a JSON array of them, under what a request may carry (a task argument's 4,000). */
const MAX_KEYS_JSON = 3900;

/** What a query can be narrowed to: the records a check named that fit a request. */
export interface FailingKeys {
  keys: string[];
  /** How many records the check named, which may be more than fit. */
  named: number;
}

/**
 * The records the findings name, each once, in the order they name them, as many as a request can carry. A check lists a
 * finding's first examples, so these are the rows it listed, which may be fewer than the rows that fail.
 */
export function failingKeys(findings: readonly FindingKeys[]): FailingKeys {
  const named = [...new Set(findings.flatMap((finding) => finding.samples.map((sample) => sample.keyJson)).filter((key): key is string => key !== null && key !== undefined))];
  const keys: string[] = [];
  let length = 2;
  for (const key of named) {
    length += key.length + 1;
    if (length > MAX_KEYS_JSON) {
      break;
    }

    keys.push(key);
  }

  return { keys, named: named.length };
}
