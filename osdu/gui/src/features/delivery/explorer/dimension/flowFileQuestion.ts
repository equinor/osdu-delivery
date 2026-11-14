/**
 * What **Write the flow file** asks the assistant (osdu/docs/reference/concepts/explorer-dimension-builder.md): the whole
 * dimension flow file holding the builder's dimension, modelled on the dimension flows the estate already has, its table
 * kept in the database the estate keeps its data in rather than the catalog's. The question is the person's message, said
 * as a person would ask it; how the assistant works it out (the patterns it reads, the check it runs) is the MCP server's,
 * the one place the assistant's behaviour is written. Only the block the answer is shown in is asked for here.
 */

/** The question, with the item the builder wrote (as it is listed under `dimensions:`) and the partition it was built over. */
export function flowFileQuestion(item: string, partition: string | null): string {
  const listed = item.replace(/\s+$/, "");
  const where = partition === null || partition === "" ? "in the explorer" : `in the explorer over partition ${partition}`;
  return [
    "Write the whole dimension flow file for the dimension below, ready to add to a repository as it is.",
    "",
    "- Follow the dimension flows this estate already has: their source, auth, partitions, parameters and schedule, and how their files are named and where they sit.",
    "- Keep its table in the database the estate keeps its data in, not in the catalog's database.",
    "- Show the file in one YAML block.",
    "",
    `I built the dimension ${where}:`,
    "",
    "```yaml",
    "dimensions:",
    listed,
    "```",
  ].join("\n");
}

/** What the conversation is about, in a few words, for the header above it. */
export function flowFileSubject(name: string): string {
  const trimmed = name.trim();
  return trimmed === "" ? "The flow file for the dimension" : `The flow file for ${trimmed}`;
}
