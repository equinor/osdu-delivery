/**
 * What **Write the flow file** asks the assistant (osdu/docs/reference/concepts/explorer-dimension-builder.md,
 * explorer-cache-builder.md): the whole flow file holding what the builder made, modelled on the flows of that kind the
 * estate already has. A dimension flow keeps its table in the database the estate keeps its data in rather than the
 * catalog's; a cached type may belong in the cache flow that declares it already. The question is the person's message,
 * said as a person would ask it; how the assistant works it out (the patterns it reads, the check it runs) is the MCP
 * server's, the one place the assistant's behaviour is written. Only the block the answer is shown in is asked for here.
 */

/** What the builder made, and so the kind of flow file asked for. */
export type FlowFileKind = "dimension" | "cache";

/** What each kind of flow file is asked in: the thing built, the list it sits under, and what the person wants of the file. */
const ASKS: Record<FlowFileKind, { built: string; file: string; under: string; wants: string[] }> = {
  dimension: {
    built: "dimension",
    file: "dimension flow file",
    under: "dimensions:",
    wants: [
      "Follow the dimension flows this estate already has: their source, auth, partitions, parameters and schedule, and how their files are named and where they sit.",
      "Keep its table in the database the estate keeps its data in, not in the catalog's database.",
    ],
  },
  cache: {
    built: "cached type",
    file: "cache flow file",
    under: "types:",
    wants: [
      "Follow the cache flows this estate already has: their source, auth, partitions, retention and schedule, and how their files are named and where they sit.",
      "If a cache flow of the estate caches a type of this name already, say so, and say whether this type belongs in that flow instead of a new one.",
    ],
  },
};

/** The question, with the item the builder wrote (as it is listed under `dimensions:` or `types:`) and the partition it was built over. */
export function flowFileQuestion(kind: FlowFileKind, item: string, partition: string | null): string {
  const ask = ASKS[kind];
  const listed = item.replace(/\s+$/, "");
  const where = partition === null || partition === "" ? "in the explorer" : `in the explorer over partition ${partition}`;
  return [
    `Write the whole ${ask.file} for the ${ask.built} below, ready to add to a repository as it is.`,
    "",
    ...ask.wants.map((want) => `- ${want}`),
    "- Show the file in one YAML block.",
    "",
    `I built the ${ask.built} ${where}:`,
    "",
    "```yaml",
    ask.under,
    listed,
    "```",
  ].join("\n");
}

/** What the conversation is about, in a few words, for the header above it. */
export function flowFileSubject(kind: FlowFileKind, name: string): string {
  const trimmed = name.trim();
  return trimmed === "" ? `The flow file for the ${ASKS[kind].built}` : `The flow file for ${trimmed}`;
}
