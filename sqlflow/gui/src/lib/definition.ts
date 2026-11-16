// Helpers over a pipeline's stored definition JSON (the parsed flow document the catalog sync serializes).

/** The derived health-check flow an ing document's embedded healthCheck: block declares, or null. The catalog
 * stores the parsed document as JSON, so the derived flow's name is read straight from the definition. */
/** What a lineage description says of the step it describes: who runs it, where it is found there, who to ask, and
 * how many objects it reads and writes. */
export interface DescribedStepFacts {
  system: string | null;
  unit: string | null;
  owner: string | null;
  reads: number;
  writes: number;
  mappedColumns: number;
}

/** The facts of a lineage description (flowType: lin) from its stored definition, or null when the definition does not
 * read as one: a description whose document stopped loading keeps its text and has no parsed definition. */
export function describedStepFacts(definitionJson: string): DescribedStepFacts | null {
  try {
    const flow = (JSON.parse(definitionJson) as { flow?: Record<string, unknown> }).flow;
    if (flow === undefined || flow === null || typeof flow !== "object") {
      return null;
    }
    const text = (value: unknown) => (typeof value === "string" && value.trim() !== "" ? value : null);
    const list = (value: unknown) => (Array.isArray(value) ? value : []);
    return {
      system: text(flow.system),
      unit: text(flow.unit),
      owner: text(flow.owner),
      reads: list(flow.reads).length,
      writes: list(flow.writes).length,
      mappedColumns: list(flow.writes).reduce(
        (sum: number, write) => sum + list((write as { columns?: unknown } | null)?.columns).length, 0),
    };
  } catch {
    return null;
  }
}

export function embeddedHealthCheckName(definitionJson: string): string | null {
  try {
    const parsed = JSON.parse(definitionJson) as { document?: { healthCheck?: { sysAlias?: string } } };
    return parsed.document?.healthCheck?.sysAlias ?? null;
  } catch {
    return null;
  }
}
