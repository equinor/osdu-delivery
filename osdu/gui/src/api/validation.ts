// What a check of a record against its schema came to (osdu/docs/reference/concepts/preflight.md, What a verdict holds),
// as an attempt's result carries it under `validation` and as the explorer answers it: the one shape every view of a
// verdict reads.

/** What a check came to. */
export type ValidationOutcome = "valid" | "invalid" | "unverified" | "notValidated";

/** One way a record breaks its schema, or one part of it no rule could be checked on. */
export interface ValidationFinding {
  /** Where, by the path every value of the property shares: `data.VerticalMeasurements[].VerticalCRSID`; empty for the record itself. */
  at: string;
  /** Exactly where: `data.VerticalMeasurements[2].VerticalCRSID`. */
  path: string;
  /** The JSON Schema keyword, `relationship`, `reference`, or for a part not checked, why. */
  rule: string;
  message: string;
  value: string;
}

/** An id the record refers to whose record was not found, and what said so. */
export interface ValidationMissingReference {
  id: string;
  at: string;
  path: string;
  detail: string;
}

/** What was found of the records a record refers to, counted by where. */
export interface ValidationReferences {
  total: number;
  ledger: number;
  cache: number;
  osdu: number;
  missing: number;
  notChecked: number;
  cut: boolean;
  missingIds: ValidationMissingReference[];
}

/** The value an assertion of a mapping judges: the one the record carries, or the one the row gives the node. */
export type AssertionStage = "record" | "incoming";

/** What a record that fails an assertion of its mapping does: held before it is sent, sent with the failure recorded, or sent without the value. */
export type AssertionAction = "hold" | "report" | "omit";

/** One value that broke an assertion of its mapping (osdu/docs/reference/flow/mapping-assertions.md). */
export interface AssertionFailure {
  /** The property every record shares: `data.Curves[].Mnemonic`. */
  at: string;
  /** Where the value was: in the record (`data.Curves[3].Mnemonic`), or for the incoming stage in the row (`dataset.curves[3].curve_id`). */
  path: string;
  /** What the assertion is called: its name, or the label read off its condition. */
  assertion: string;
  stage: AssertionStage;
  onFail: AssertionAction;
  /** Why the value fails it. */
  message: string;
  /** The value, as text, clipped and redacted. */
  value: string;
}

/**
 * What a mapping's assertions found of one record: the judgements made, those that failed by what their failure does, and
 * the failures, listed up to a bound and counted whole.
 */
export interface AssertionFindings {
  /** The mapping whose assertions were judged, as `Name@version`. */
  mapping: string;
  checked: number;
  failed: number;
  held: number;
  reported: number;
  omitted: number;
  failures: AssertionFailure[];
  /** Whether the failures were shortened to fit an attempt's result; the counts are whole. */
  shortened?: boolean;
}

export interface ValidationVerdict {
  outcome: ValidationOutcome;
  /** The schema checked against; absent when none could be had. `source` is `template` (a saved one) or `schema-service`. */
  schema?: { kind: string; version: string; source: "template" | "schema-service" };
  /** How many rules were applied. */
  rules: number;
  problemCount: number;
  problems: ValidationFinding[];
  unverifiedCount: number;
  unverified: ValidationFinding[];
  references: ValidationReferences;
  /** What the schema states that no check asserts, and why nothing was checked when nothing was. */
  notes: string[];
  /** Whether a release accepted the verdict for the document, so it was sent whatever it says. */
  accepted?: boolean;
  rulesVersion: string;
  checkedUtc: string;
  /** Whether the listings were shortened to fit an attempt's result; the counts are whole. */
  shortened?: boolean;
  /**
   * What the assertions of the record's mapping found (osdu/docs/reference/flow/mapping-assertions.md); absent when its
   * mapping states none, or when no mapping was asked to judge it. A failure that holds holds the record whatever the outcome.
   */
  assertions?: AssertionFindings;
}

/**
 * What the schema expects of the value at one place of a record (osdu/docs/reference/concepts/explorer.md, Guidance on each
 * problem): its words, its rules, its examples, and the value OSDU's own example record holds there.
 */
export interface ValueExpectation {
  /** The place, as a finding names it, with `[]` at the end for each item of the list there. */
  at: string;
  title: string | null;
  description: string | null;
  /** One line saying what a value here is. */
  summary: string;
  /** The JSON types a value may be, `null` among them when the schema allows it. */
  types: string[];
  /** Whether the object holding the value requires it. */
  required: boolean;
  patterns: string[];
  /** The values allowed, text unquoted; `allowedCount` says how many there are in all. */
  allowed: string[];
  allowedCount: number;
  formats: string[];
  minLength: number | null;
  maxLength: number | null;
  minItems: number | null;
  maxItems: number | null;
  minimum: number | null;
  maximum: number | null;
  exclusiveMinimum: number | null;
  exclusiveMaximum: number | null;
  multipleOf: string[];
  uniqueItems: boolean;
  /** The entity types an OSDU id written here may be of. */
  entityTypes: string[];
  /** For an object: the properties the schema names (`propertyCount` in all), and those it requires. */
  properties: string[];
  propertyCount: number;
  requiredProperties: string[];
  onlyNamedProperties: boolean;
  /** For a list: what each item is, in words. */
  items: string | null;
  /** How many forms a oneOf or anyOf here allows. */
  forms: number;
  /** The examples the schema gives. */
  examples: string[];
  /** The value the OSDU data definitions' example record holds here. */
  osduExample: string | null;
}

/** What one finding comes to for the person fixing it. */
export interface FindingGuide {
  path: string;
  rule: string;
  /** The key of what the schema expects there, in `expectations`; null when the schema describes nothing there. */
  expected: string | null;
  /** The value found, in words: `null`, `absent`, `'Wellbore A-1'`, `a list of 3 items`. */
  found: string;
  /** How to make the value meet the schema. */
  advice: string | null;
}

/** What a verdict comes to for the person fixing the record, one guide per finding in the verdict's order. */
export interface ValidationGuidance {
  expectations: Record<string, ValueExpectation>;
  problems: FindingGuide[];
  unverified: FindingGuide[];
  /** The OSDU data definitions' example record the expectations quote. */
  example: { release: string; path: string; webUrl: string } | null;
  /** Why no example of the data definitions is quoted, when one was looked for. */
  exampleNote: string | null;
}

/** A guidance as read from an answer, every part a task's answer may leave out read as null or empty. */
export function guidanceOf(raw: Partial<ValidationGuidance> | null | undefined): ValidationGuidance | null {
  if (raw === null || raw === undefined || typeof raw !== "object") {
    return null;
  }

  const guide = (g: Partial<FindingGuide>): FindingGuide => ({
    path: g.path ?? "",
    rule: g.rule ?? "",
    expected: g.expected ?? null,
    found: g.found ?? "",
    advice: g.advice ?? null,
  });
  const expectations: Record<string, ValueExpectation> = {};
  for (const [key, e] of Object.entries(raw.expectations ?? {})) {
    const value = e as Partial<ValueExpectation>;
    expectations[key] = {
      at: value.at ?? key,
      title: value.title ?? null,
      description: value.description ?? null,
      summary: value.summary ?? "",
      types: value.types ?? [],
      required: value.required ?? false,
      patterns: value.patterns ?? [],
      allowed: value.allowed ?? [],
      allowedCount: value.allowedCount ?? 0,
      formats: value.formats ?? [],
      minLength: value.minLength ?? null,
      maxLength: value.maxLength ?? null,
      minItems: value.minItems ?? null,
      maxItems: value.maxItems ?? null,
      minimum: value.minimum ?? null,
      maximum: value.maximum ?? null,
      exclusiveMinimum: value.exclusiveMinimum ?? null,
      exclusiveMaximum: value.exclusiveMaximum ?? null,
      multipleOf: value.multipleOf ?? [],
      uniqueItems: value.uniqueItems ?? false,
      entityTypes: value.entityTypes ?? [],
      properties: value.properties ?? [],
      propertyCount: value.propertyCount ?? 0,
      requiredProperties: value.requiredProperties ?? [],
      onlyNamedProperties: value.onlyNamedProperties ?? false,
      items: value.items ?? null,
      forms: value.forms ?? 0,
      examples: value.examples ?? [],
      osduExample: value.osduExample ?? null,
    };
  }

  return {
    expectations,
    problems: (raw.problems ?? []).map(guide),
    unverified: (raw.unverified ?? []).map(guide),
    example: raw.example ?? null,
    exampleNote: raw.exampleNote ?? null,
  };
}

/** The verdict an attempt's result carries, or null for an attempt that carries none. */
export function verdictOf(resultJson: string | null | undefined): ValidationVerdict | null {
  if (resultJson === null || resultJson === undefined || resultJson === "") {
    return null;
  }

  try {
    const parsed: unknown = JSON.parse(resultJson);
    return typeof parsed === "object" && parsed !== null ? verdictIn((parsed as { validation?: unknown }).validation) : null;
  } catch {
    // A result that is not JSON is not the shape anything wrote; it carries no verdict.
    return null;
  }
}

/** A verdict as an attempt's parsed result carries it under `validation`, or null for anything else. */
export function verdictIn(validation: unknown): ValidationVerdict | null {
  return isVerdict(validation) ? validation : null;
}

function isVerdict(value: unknown): value is ValidationVerdict {
  if (typeof value !== "object" || value === null) {
    return false;
  }

  const outcome = (value as { outcome?: unknown }).outcome;
  return outcome === "valid" || outcome === "invalid" || outcome === "unverified" || outcome === "notValidated";
}

/** How a finding names where it is: its exact path, or the record itself. */
export function whereOf(finding: { at: string; path: string }): string {
  return finding.path !== "" ? finding.path : finding.at !== "" ? finding.at : "the record";
}

/** How a source of a schema reads to a person. */
export function schemaSourceName(source: string | undefined): string {
  return source === "schema-service" ? "the Schema service" : source === "template" ? "a template" : "no schema";
}
