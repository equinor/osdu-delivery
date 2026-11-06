// The colours a downloaded assertion dashboard is drawn in. dashboard.css gives the file SQLFlow's light and dark values
// (html.adb-standalone); a download adds the values the workbench's tokens hold in the design worn, in both of its modes,
// so the file opens in the design it was downloaded in and its own light and dark switch stays within that design.

/** The workbench tokens dashboard.css draws the downloaded file in. */
export const FILE_TOKENS = [
  "--radius",
  "--background",
  "--foreground",
  "--card",
  "--muted",
  "--muted-foreground",
  "--accent",
  "--border",
  "--input",
  "--primary",
  "--ring",
  "--destructive",
  "--success",
  "--warning",
] as const;

export type FileToken = (typeof FILE_TOKENS)[number];

/** The values the file's tokens hold in the design worn, in its light and dark mode, as read off the workbench. */
export interface WornTokens {
  light: Partial<Record<FileToken, string>>;
  dark: Partial<Record<FileToken, string>>;
}

/**
 * What a token's value may hold to be written into the file's style element: a colour or a length in any CSS notation,
 * and nothing that could close the declaration, the rule or the element.
 */
const SAFE_VALUE = /^[#\w\s.,%()/+-]+$/;

function block(selector: string, tokens: Partial<Record<FileToken, string>>): string {
  const declarations = FILE_TOKENS.flatMap((name) => {
    const value = tokens[name]?.trim() ?? "";
    return value !== "" && SAFE_VALUE.test(value) ? [`${name}: ${value};`] : [];
  });
  return declarations.length === 0 ? "" : `${selector} { ${declarations.join(" ")} }`;
}

/**
 * The style rules that put the worn design's values on the file, after dashboard.css's own, which they outweigh by order.
 * A token the workbench gave no value, or a value that is not a plain colour or length, is left out, so the file keeps
 * SQLFlow's value for it.
 */
export function wornTokenStyle(tokens: WornTokens): string {
  return [block("html.adb-standalone", tokens.light), block("html.adb-standalone.dark", tokens.dark)]
    .filter((rule) => rule !== "")
    .join("\n");
}
