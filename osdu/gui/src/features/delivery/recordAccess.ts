// The access OSDU holds a record under, as every page says it (osdu/docs/reference/flow/mapping-restricted-access.md and
// osdu/docs/reference/concepts/access-restriction.md). It is an axis of its own beside the status: a record restricted
// until complete is delivered, and one an operator restricted keeps whatever status it had. "Restricted" alone always means
// either kind, each record once; each kind is always named with its qualifier. A flow's card, the Home page, the Delivery
// page's summary, the Records tab and the Records page read their words, filters and counts from here, so they say the same.

/** The Access filter of the Records tab and the Records page: any access, or one kind of restriction, or either. */
export type AccessFilter = "any" | "restricted" | "until-complete" | "by-operator";

/** A filter's words, what it lists, and the address parameter it sets, as the API names it. */
export interface AccessFilterChoice {
  value: AccessFilter;
  label: string;
  meaning: string;
  /** The address and API parameter that narrows to it; none for any access. */
  param: AccessParam | null;
}

/** The address and API parameters of the Access filter, as the records listing and the lookup across flows name them. */
export type AccessParam = "accessRestricted" | "incomplete" | "restricted";

export const ACCESS_PARAMS: readonly AccessParam[] = ["accessRestricted", "incomplete", "restricted"];

export const ACCESS_FILTERS: readonly AccessFilterChoice[] = [
  { value: "any", label: "Any access", meaning: "Every record, whatever access OSDU holds it under.", param: null },
  {
    value: "restricted",
    label: "restricted",
    meaning: "Records whose access in OSDU is restricted either way, until complete or by an operator, each listed once.",
    param: "accessRestricted",
  },
  {
    value: "until-complete",
    label: "restricted until complete",
    meaning:
      "Records OSDU holds under their mapping's restricted access because values it marks $restrictWhenMissing are missing. Each gets its own access back once a delivery finds them all.",
    param: "incomplete",
  },
  {
    value: "by-operator",
    label: "restricted by an operator",
    meaning:
      "Records an operator restricted under a registered access restriction: readable only by its groups, and delivered by no run until the restriction is lifted.",
    param: "restricted",
  },
];

/**
 * The Access filter an address asks for. Either kind together is asked as `accessRestricted`, one kind as `incomplete` or
 * `restricted`; an address naming more than one (a link written by hand) takes the broadest, so nothing it asked for is
 * left out of the list.
 */
export function accessFilterOf(get: (name: AccessParam) => string | null): AccessFilter {
  if (get("accessRestricted") === "true" || (get("incomplete") === "true" && get("restricted") === "true")) {
    return "restricted";
  }

  if (get("incomplete") === "true") {
    return "until-complete";
  }

  return get("restricted") === "true" ? "by-operator" : "any";
}

/** The address parameters a filter sets: its own to `true`, the others cleared. */
export function accessAddress(filter: AccessFilter): Record<AccessParam, string | null> {
  const chosen = ACCESS_FILTERS.find((choice) => choice.value === filter)?.param ?? null;
  return {
    accessRestricted: chosen === "accessRestricted" ? "true" : null,
    incomplete: chosen === "incomplete" ? "true" : null,
    restricted: chosen === "restricted" ? "true" : null,
  };
}

/** The records-listing filter a choice narrows by, as the API takes it; nothing for any access. */
export function accessQuery(filter: AccessFilter): { accessRestricted?: true; incomplete?: true; restricted?: true } {
  switch (filter) {
    case "restricted":
      return { accessRestricted: true };
    case "until-complete":
      return { incomplete: true };
    case "by-operator":
      return { restricted: true };
    default:
      return {};
  }
}

/** A flow's (or the estate's) counts of restricted records, as its stats give them. */
export interface RestrictedCounts {
  restricted?: number;
  restrictedUntilComplete?: number;
  accessRestricted?: number;
}

/** The Restricted figure: every record restricted either way, once, and each kind. */
export interface RestrictedFigure {
  total: number;
  untilComplete: number;
  byOperator: number;
}

/**
 * The Restricted figure of some counts. The total is the server's count of records restricted either way, each once; a
 * server that gives none (older than the figure) is read as the two kinds added, which counts a record restricted both ways
 * twice, and never less than the larger kind.
 */
export function restrictedFigure(counts: RestrictedCounts): RestrictedFigure {
  const untilComplete = Math.max(0, counts.restrictedUntilComplete ?? 0);
  const byOperator = Math.max(0, counts.restricted ?? 0);
  const total = Math.max(counts.accessRestricted ?? untilComplete + byOperator, untilComplete, byOperator);
  return { total, untilComplete, byOperator };
}

/** What the Restricted figure says on hover: what it counts, each kind, and that its records are counted under their status too. */
export function restrictedExplanation(figure: RestrictedFigure): string {
  const kinds = figure.total === 0
    ? "None is restricted until complete or by an operator."
    : `${figure.untilComplete.toLocaleString()} restricted until complete, ${figure.byOperator.toLocaleString()} restricted by an operator.`;
  return `Records whose access in OSDU is restricted, each counted once: ${kinds} They are counted under their status too, so this is not part of the total.`;
}

/** An Access filter choice's label with its count, as the Records tab lists them; no count when the counts are not known. */
export function accessChoiceLabel(choice: AccessFilterChoice, figure: RestrictedFigure | null): string {
  if (figure === null || choice.value === "any") {
    return choice.label;
  }

  const count = choice.value === "restricted" ? figure.total : choice.value === "until-complete" ? figure.untilComplete : figure.byOperator;
  return `${choice.label} (${count.toLocaleString()})`;
}
