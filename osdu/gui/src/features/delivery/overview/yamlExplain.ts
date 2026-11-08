// What the assistant is asked about, or what is copied, from the mapping's YAML in the overview: lines of it, whole, or the
// whole mapping. Kept free of imports, so the unit suite reads it.

/** Lines of the YAML, from 1 and inclusive. */
export interface LineRange {
  start: number;
  end: number;
}

/** The code an explanation is asked about, as Explain takes it (`ExplainCode`): the lines, their number, and how many there are. */
export interface ExplainedYaml {
  code: string;
  language: "yaml";
  /** The lines asked about; null for the whole mapping. */
  lines: LineRange | null;
  totalLines: number;
}

/** The mapping's lines, numbered from 1 as the pane numbers them. */
export function yamlLines(yaml: string): string[] {
  return yaml.replace(/\r\n/g, "\n").split("\n");
}

/**
 * The lines asked about, each whole, or the whole mapping when no lines are. A range is read in either order and kept
 * inside the mapping, so a selection that runs past its last line asks about the lines there are.
 */
export function explainedYaml(yaml: string, range: LineRange | null): ExplainedYaml {
  const lines = yamlLines(yaml);
  if (range === null) {
    return { code: lines.join("\n"), language: "yaml", lines: null, totalLines: lines.length };
  }

  const start = within(Math.min(range.start, range.end), lines.length);
  const end = within(Math.max(range.start, range.end), lines.length);
  return { code: lines.slice(start - 1, end).join("\n"), language: "yaml", lines: { start, end }, totalLines: lines.length };
}

/** What a menu over some lines names them by: "Line 12", "Lines 12 to 15". */
export function linesLabel(range: LineRange): string {
  return range.start === range.end ? `Line ${range.start}` : `Lines ${range.start} to ${range.end}`;
}

function within(line: number, count: number): number {
  return Math.min(Math.max(Math.trunc(line), 1), Math.max(count, 1));
}
