// A value of a record as the OSDU record inspector shows it: the search term marked in it, short values as chips, a
// value naming another record as the link that opens it, and a leaf as it reads. OsduRecordInspector.tsx re-exports
// Highlight, so its importers import it from there.

import type { ReactNode } from "react";
import { Loader2, type LucideIcon } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { cn } from "@/lib/utils";
import { CopyButton } from "@/components/CopyButton";
import { TruncatedText } from "@/components/TruncatedText";
import { withoutVersion } from "./osduDocument";
import { isReferenceNode, type RecordNode } from "./osduRecordModel";
import { RecordName } from "./RecordName";
import type { OpenLink } from "./osduRecordInspectorShared";

/** Text with every occurrence of the search term marked, so a match shows where it is rather than only that it is. */
export function Highlight({ text: value, term }: { text: string; term: string }) {
  if (term === "") {
    return <>{value}</>;
  }

  const parts: ReactNode[] = [];
  const lower = value.toLowerCase();
  let from = 0;
  for (let at = lower.indexOf(term, from); at >= 0; at = lower.indexOf(term, from)) {
    if (at > from) {
      parts.push(value.slice(from, at));
    }

    parts.push(<mark key={at} className="rounded-sm bg-warning/40 text-inherit">{value.slice(at, at + term.length)}</mark>);
    from = at + term.length;
  }

  if (from < value.length) {
    parts.push(value.slice(from));
  }

  return <>{parts}</>;
}

/** A list of short values as chips, each carrying the glyph and hover title that say what it is; nothing when there are none. */
export function Chips({ values, icon: Icon, what, testId }: { values: string[]; icon: LucideIcon; what: string; testId?: string }) {
  if (values.length === 0) {
    return null;
  }

  return (
    <span className="inline-flex flex-wrap gap-1" data-testid={testId}>
      {values.map((value) => (
        <Badge key={value} variant="outline" className="gap-1 font-mono text-[11px] font-normal" title={what}>
          <Icon className="size-3 text-muted-foreground" />
          {value}
        </Badge>
      ))}
    </span>
  );
}

/**
 * A value that names another OSDU record: the name itself is the link, and a click opens that record here, read
 * through the same flow's route, after this one on the trail. The copy beside it hands over the id verbatim; in a list
 * of records (`quietCopy`) it shows on the row's hover, as the explorer's grids show theirs.
 */
export function ReferenceLink({ value, path, ownId, onOpenLink, opening, term, className, withoutType = false, quietCopy = false }: {
  value: string; path: string; ownId: string | null; onOpenLink?: OpenLink; opening?: string | null; term: string; className?: string;
  /** Names the record by its unique part alone, where a heading names its type. */
  withoutType?: boolean;
  /** Shows the copy on the hover of the row (`group/link`) rather than always. */
  quietCopy?: boolean;
}) {
  const id = withoutVersion(value);
  const self = ownId !== null && id === withoutVersion(ownId);
  const matched = term !== "" && value.toLowerCase().includes(term);
  const name = matched
    ? <span className="min-w-0 break-all font-mono text-[12px]"><Highlight text={value} term={term} /></span>
    : <RecordName id={value} withoutType={withoutType} className="text-[12px]" />;
  return (
    <span className={cn("inline-flex min-w-0 max-w-full items-center gap-1", className)} data-testid="osdu-record-link" data-value={value}>
      {onOpenLink !== undefined && !self
        ? (
          <button
            type="button"
            className="inline-flex min-w-0 max-w-full cursor-pointer items-center gap-1 rounded-sm text-primary underline-offset-2 hover:underline focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none disabled:cursor-wait"
            onClick={(event) => { event.stopPropagation(); onOpenLink(id, path); }}
            disabled={opening === id}
            title="Open this record here"
            data-testid="osdu-link-read"
          >
            {opening === id && <Loader2 className="size-3.5 shrink-0 animate-spin" />}
            {name}
          </button>
        )
        : <span className="inline-flex min-w-0 max-w-full items-center gap-1 text-muted-foreground">{name}{self && <span className="shrink-0 text-[11px]">(this record)</span>}</span>}
      <span className={cn("shrink-0", quietCopy && "opacity-0 transition-opacity group-hover/link:opacity-100 focus-within:opacity-100")}>
        <CopyButton iconOnly label="Copy the id" text={value} testId="copy-osdu-link" />
      </span>
    </span>
  );
}

/**
 * A leaf as it reads: a reference as a link, text as text, numbers and booleans tinted, the search term marked. A compact
 * leaf, in a table cell whose width it clips to, leaves long text without its copy.
 */
export function Leaf({ node, term, ownId, onOpenLink, opening, compact = false }: {
  node: RecordNode; term: string; ownId: string | null; onOpenLink?: OpenLink; opening?: string | null; compact?: boolean;
}) {
  const value = node.value;
  if (isReferenceNode(node)) {
    return <ReferenceLink value={node.value} path={node.path} ownId={ownId} onOpenLink={onOpenLink} opening={opening} term={term} />;
  }

  const matched = term !== "" && node.valueText.includes(term);
  if (typeof value === "string") {
    return matched
      ? <span className="min-w-0 break-all text-[12px]"><Highlight text={value} term={term} /></span>
      : <TruncatedText text={value} maxWidth={640} copy={!compact && value.length > 40} className="text-[12px]" />;
  }

  if (typeof value === "number") {
    return <span className="font-mono text-[12px] tabular-nums text-info"><Highlight text={String(value)} term={term} /></span>;
  }

  if (typeof value === "boolean") {
    return <span className="font-mono text-[12px] text-warning"><Highlight text={String(value)} term={term} /></span>;
  }

  return <span className="font-mono text-[12px] text-muted-foreground"><Highlight text="null" term={term} /></span>;
}
