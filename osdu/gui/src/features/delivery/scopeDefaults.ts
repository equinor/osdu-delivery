/** What deciding a parameter's start reads of how its flow declares it. */
interface DeclaredParameter {
  name: string;
  required: boolean;
  default?: unknown;
}

/**
 * Whether a read of a flow's rows cannot go without a value for the parameter and nothing but a pick would give it one:
 * required, with no default in its document. An optional parameter left empty narrows nothing, and a default fills itself.
 */
export function wantsFirstOption(parameter: Omit<DeclaredParameter, "name">): boolean {
  return parameter.required && (parameter.default === null || parameter.default === undefined);
}

/**
 * The fields of a read that only looks: what the reader gave, and for a parameter that wants one and was left alone, its first
 * option, so a page opens on rows rather than on a refusal. A field the reader emptied stays empty: it is in `fields`, as
 * undefined. A run form never does this, since a value nobody picked must not decide what a run writes.
 */
export function withFirstOptions<V>(
  parameters: readonly DeclaredParameter[],
  fields: Readonly<Record<string, V | undefined>>,
  first: Readonly<Record<string, V>>,
): Record<string, V | undefined> {
  const given: Record<string, V | undefined> = { ...fields };
  for (const parameter of parameters) {
    if (!(parameter.name in fields) && wantsFirstOption(parameter) && parameter.name in first) {
      given[parameter.name] = first[parameter.name];
    }
  }

  return given;
}
