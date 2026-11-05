import type { ComponentProps } from "react";
import { CodeView } from "@/components/CodeView";

/**
 * A code view of record content: a source row, a rendered or previewed record, a cache item or a cache change's values.
 * Explain is off on it, so the GUI never sends a record's content or a source row to the assistant's language model;
 * the assistant reads data only for a `!` question, over the columns an approved AI context opens (decision 0046). Every
 * other code view (a mapping's YAML, a template, a statement) keeps Explain.
 */
export function RecordContentView(props: Omit<ComponentProps<typeof CodeView>, "explain">) {
  return <CodeView {...props} explain={false} />;
}
