// What "Explain its lineage" asks the assistant about a column a page shows: where its value comes from, back to its
// source, and what happens to it on the way. The question names the column, the object it belongs to and that object's
// key in the lineage graph, so the assistant traces it (trace_column) rather than reading names off the screen, and it
// asks for every gap to be said as missing rather than filled in. Kept free of imports but a type, so the unit suite
// reads it.

import type { AssistantAsk } from "./explain.ts";

/** What Explain its lineage says it does, wherever a page offers it: on a menu entry, or under an icon button. */
export const COLUMN_LINEAGE_HINT =
  "The assistant traces this column back to its source through the flows and the lineage descriptions, and says where the lineage does not say";

/** A column whose lineage is asked about: its name, the object it belongs to as the page names it, and that object's key in the lineage graph. */
export interface LineageColumn {
  column: string;
  object: string;
  objectKey: string;
}

/**
 * A name as inline code its own backticks cannot close early: marks one longer than the longest run of backticks in it,
 * set off by a space where it starts or ends with one.
 */
function code(name: string): string {
  const longest = Math.max(0, ...Array.from(name.matchAll(/`+/g), (match) => match[0].length));
  const marks = "`".repeat(longest + 1);
  const space = name.startsWith("`") || name.endsWith("`") ? " " : "";
  return `${marks}${space}${name}${space}${marks}`;
}

/**
 * The question that traces a column back to its source, in the asker's words: the path, how each step of it is known,
 * and what is missing. Where lineage does not say, the answer says so; nothing is made up from a name.
 */
export function columnLineageQuestion(subject: LineageColumn): string {
  const column = code(subject.column);
  return [
    `Where does the value of the column ${column} of ${code(subject.object)} come from, back to its source, and what happens to it on the way?`,
    "",
    `Trace it with trace_column (object key ${code(subject.objectKey)}, column ${column}), then tell me:`,
    "",
    "1. The path from the source to this column, one step per line: the object and the column there, the flow or the described step that moves it, and what that step does to the value.",
    "2. For each step, how it is known: stated by a lineage description, declared by a flow, or only assumed because the column keeps its name.",
    "3. What is missing: where the trace ends without reaching a source, or a step is described without this column, say so, and say what would supply it.",
    "",
    "Do not fill a gap from the names or from general knowledge. If nothing describes where the data comes from before the estate reads it, say that the information is missing.",
  ].join("\n");
}

/** What explaining a column's lineage asks of the assistant, in the drawer Explain opens. */
export function columnLineageAsk(subject: LineageColumn): AssistantAsk {
  return { subject: `the lineage of ${subject.column}`, question: columnLineageQuestion(subject), wide: true };
}
