// A branch of a record as JSON, as the OSDU record inspector and the pages that show a document show it.
// OsduRecordInspector.tsx re-exports JsonView, so its importers import it from there.

import { useState, type ReactNode } from "react";
import { ChevronDown, ChevronRight } from "lucide-react";
import { cn } from "@/lib/utils";
import { CopyButton } from "@/components/CopyButton";
import { withoutVersion } from "./osduDocument";
import { describeBranch, isReferenceNode, type RecordNode } from "./osduRecordModel";
import { type OpenLink, PAGE } from "./osduRecordInspectorShared";
import { Highlight } from "./OsduRecordInspectorValues";

const PUNCTUATION = "text-muted-foreground";

/**
 * A branch of the record as JSON that can be worked with, not only read: every object and list folds at its caret, a
 * key that holds a branch steps into it (and the brace of a list's item into that item), and a value that names another
 * OSDU record opens it. Lists show their first hundred items and the rest on a click; the search term is marked. Without
 * `onSelect` a key only reads: a document that is not a stored record has nowhere to step to. `collapsed` names the branches
 * that start folded, and `absent` what an object lacks, which it says at the end of the object, marked like a hit. A marked
 * line says so (`data-hit`).
 */
export function JsonView({ node, term, hits, ownId, onOpenLink, opening, onSelect, actions, collapsed, absent }: {
  node: RecordNode; term: string; hits: ReadonlySet<string>; ownId: string | null; onOpenLink?: OpenLink; opening?: string | null; onSelect?: (path: string) => void;
  /** What a page shows beside a value or a section, after the line it starts on. */
  actions?: (node: RecordNode) => ReactNode;
  collapsed?: ReadonlySet<string>;
  absent?: ReadonlyMap<string, string>;
}) {
  const [folded, setFolded] = useState<ReadonlySet<string>>(() => new Set(collapsed));
  const [shownItems, setShownItems] = useState<ReadonlyMap<string, number>>(() => new Map());
  const fold = (path: string) => setFolded((current) => {
    const next = new Set(current);
    if (next.has(path)) {
      next.delete(path);
    } else {
      next.add(path);
    }

    return next;
  });

  const lines: ReactNode[] = [];
  const line = (key: string, depth: number, content: ReactNode, caret?: { open: boolean; path: string }, hit = false) => {
    lines.push(
      <div key={key} className={cn("flex min-w-0 items-start rounded-sm pr-2", hit && "bg-warning/10")} style={{ paddingLeft: depth * 16 }} data-hit={hit || undefined}>
        <span className="flex h-5 w-4 shrink-0 items-center justify-center">
          {caret !== undefined && (
            <button type="button" className="text-muted-foreground hover:text-foreground" onClick={() => fold(caret.path)} aria-label={caret.open ? "Fold" : "Unfold"} data-testid="osdu-json-fold">
              {caret.open ? <ChevronDown className="size-3.5" /> : <ChevronRight className="size-3.5" />}
            </button>
          )}
        </span>
        <span className="min-w-0 whitespace-pre-wrap break-all">{content}</span>
      </div>,
    );
  };

  const leafValue = (leaf: RecordNode): ReactNode => {
    const value = leaf.value;
    if (isReferenceNode(leaf)) {
      const id = withoutVersion(leaf.value);
      const self = ownId !== null && id === withoutVersion(ownId);
      return (
        <span>
          <span className="text-foreground">&quot;</span>
          {onOpenLink !== undefined && !self
            ? (
              <button type="button" className="cursor-pointer text-left text-primary underline-offset-2 hover:underline disabled:cursor-wait" onClick={() => onOpenLink(id, leaf.path)} disabled={opening === id} title="Open this record here" data-testid="osdu-json-link">
                <Highlight text={leaf.value} term={term} />
              </button>
            )
            : <span className="text-foreground"><Highlight text={leaf.value} term={term} /></span>}
          <span className="text-foreground">&quot;</span>
        </span>
      );
    }

    if (typeof value === "string") {
      return <span className="text-foreground">&quot;<Highlight text={value} term={term} />&quot;</span>;
    }

    if (typeof value === "number") {
      return <span className="tabular-nums text-info"><Highlight text={String(value)} term={term} /></span>;
    }

    if (typeof value === "boolean") {
      return <span className="text-warning"><Highlight text={String(value)} term={term} /></span>;
    }

    return <span className="text-muted-foreground">null</span>;
  };

  const walk = (current: RecordNode, depth: number, keyOf: RecordNode | null, last: boolean) => {
    const comma = last ? null : <span className={PUNCTUATION}>,</span>;
    const label = keyOf === null
      ? null
      : current.kind === "leaf" || onSelect === undefined
        ? <span className="text-muted-foreground">&quot;<Highlight text={current.key} term={term} />&quot;<span className={PUNCTUATION}>: </span></span>
        : (
          <span>
            <button type="button" className="cursor-pointer text-muted-foreground hover:text-primary hover:underline" onClick={() => onSelect(current.path)} title={`Go to ${current.path}`} data-testid="osdu-json-key">
              &quot;<Highlight text={current.key} term={term} />&quot;
            </button>
            <span className={PUNCTUATION}>: </span>
          </span>
        );
    const hit = hits.has(current.path);
    if (current.kind === "leaf") {
      line(current.path, depth, <>{label}{leafValue(current)}{comma}{actions !== undefined && <span className="ml-1.5 inline-flex align-middle">{actions(current)}</span>}</>, undefined, hit);
      return;
    }

    const [open, close] = current.kind === "array" ? ["[", "]"] : ["{", "}"];
    // An item of a list has no key to step into it by, so its opening brace does that work.
    const opener = keyOf === null && depth > 0 && onSelect !== undefined
      ? <button type="button" className={cn(PUNCTUATION, "cursor-pointer hover:text-primary")} onClick={() => onSelect(current.path)} title={`Go to ${current.path}`}>{open}</button>
      : <span className={PUNCTUATION}>{open}</span>;
    if (current.children.length === 0 && !absent?.has(current.path)) {
      line(current.path, depth, <>{label}<span className={PUNCTUATION}>{open}{close}</span>{comma}</>, undefined, hit);
      return;
    }

    if (folded.has(current.path)) {
      line(
        current.path,
        depth,
        <>
          {label}{opener}
          <button type="button" className="mx-1 rounded bg-muted px-1 text-[11px] text-muted-foreground hover:text-foreground" onClick={() => fold(current.path)}>{describeBranch(current)}</button>
          <span className={PUNCTUATION}>{close}</span>{comma}
        </>,
        { open: false, path: current.path },
        hit,
      );
      return;
    }

    line(
      `${current.path}:open`,
      depth,
      <>{label}{opener}{actions !== undefined && depth > 0 && <span className="ml-1.5 inline-flex align-middle">{actions(current)}</span>}</>,
      { open: true, path: current.path },
      hit,
    );
    const limit = current.kind === "array" ? Math.min(current.children.length, shownItems.get(current.path) ?? PAGE) : current.children.length;
    current.children.slice(0, limit).forEach((child, index) => {
      walk(child, depth + 1, current.kind === "object" ? child : null, index === current.children.length - 1);
    });
    if (limit < current.children.length) {
      lines.push(
        <div key={`${current.path}:more`} style={{ paddingLeft: (depth + 1) * 16 + 16 }}>
          <button type="button" className="cursor-pointer text-[11px] text-primary hover:underline" onClick={() => setShownItems((was) => new Map(was).set(current.path, current.children.length))} data-testid="osdu-json-more">
            {`${current.children.length - limit} more items`}
          </button>
        </div>,
      );
    }

    const lacking = absent?.get(current.path);
    if (lacking !== undefined) {
      line(
        `${current.path}:absent`,
        depth + 1,
        <span className="text-muted-foreground italic" data-testid="osdu-json-absent">{`"${lacking}" left out`}</span>,
        undefined,
        true,
      );
    }

    line(`${current.path}:close`, depth, <><span className={PUNCTUATION}>{close}</span>{comma}</>);
  };

  walk(node, 0, null, true);
  return (
    <div className="relative p-2 font-mono text-[12px] leading-5" data-testid="osdu-json">
      <div className="sticky top-0 z-10 float-right">
        <CopyButton iconOnly label="Copy this branch as JSON" text={JSON.stringify(node.value, null, 2)} testId="copy-osdu-json" />
      </div>
      {lines}
    </div>
  );
}
