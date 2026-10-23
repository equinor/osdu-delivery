import { type ReactNode } from "react";
import { ArrowDown, Filter, Info } from "lucide-react";
import { cn } from "@/lib/utils";
import { GlyphRef } from "@/components/GlyphRef";
import { RichTooltip } from "@/components/RichTooltip";
import type { MappingDraftInput } from "../../api/delivery";
import { AssertionActionBadge } from "./AssertionFindingsView";
import { assertionText } from "./mappingAssertionText";
import type { PropertyRow } from "./mappingDraft";

/** What the source of a value is called, so the node says the kind and the value says only itself. */
const SOURCE_LABEL: Record<MappingDraftInput, string> = {
  Dataset: "Dataset column",
  Repeat: "One item per row of",
  Cache: "Cached record",
  Static: "Static value",
  Search: "Record found by searching the platform",
  Expression: "Computed by the expression",
  Coalesce: "The first of these that gives a value",
  Lookup: "Field of the record a lookup finds",
  List: "Every value these give, in order, each once",
  Group: "The object its properties give",
  Parent: "Section of the record's parent",
  Child: "Link to the record's child, once OSDU holds it",
};

/** One end of the pipeline: where the value comes from, or the property it lands on. */
function Node({
  label, value, target = false, children, testId,
}: { label: string; value: string; target?: boolean; children?: ReactNode; testId?: string }) {
  return (
    <div
      className={cn("rounded-md border px-3 py-2", target ? "border-primary/40 bg-primary/5" : "bg-muted/40")}
      data-testid={testId}
    >
      <p className="text-[11px] text-muted-foreground">{label}</p>
      <p className="font-mono text-[12px] break-all">{value}</p>
      {children}
    </div>
  );
}

/** One step down the pipeline: the arrow, and what happens on the way when something does. */
function Step({ text, index }: { text?: string; index?: number }) {
  return (
    <div className="flex items-center gap-2 py-1 pl-3">
      <ArrowDown className="size-3.5 shrink-0 text-muted-foreground" />
      {text !== undefined && (
        <span className="rounded border bg-background px-1.5 py-0.5 font-mono text-[11px]">
          {index !== undefined && <span className="mr-1.5 text-muted-foreground">{index}</span>}
          {text}
        </span>
      )}
    </div>
  );
}

/**
 * One entry as the pipeline that fills its property: the value's origin, the record it is found by, the modifiers in the
 * order they run, and the property they land on. A lookup's modifiers change the value it compares rather than the
 * record's field, so they are drawn inside the lookup, where they act.
 *
 * `target` is left out where the view already says which property this fills, such as beside a tree of them.
 */
export function EntryDetail({ row, target = true }: { row: PropertyRow; target?: boolean }) {
  const lookupSteps = row.input === "Cache" || row.input === "Search";
  const steps = !lookupSteps && row.modifiers.length > 0
    ? (
      <div data-testid="delivery-mapping-property-detail-modifiers">
        {row.modifiers.map((text, index) => <Step key={`${index}-${text}`} text={text} index={index + 1} />)}
      </div>
    )
    : target ? <Step /> : null;

  return (
    <div className="flex flex-col" data-testid="delivery-mapping-entry-detail">
      {row.condition !== null && (
        <p
          className="mb-3 flex items-start gap-2 rounded-md border border-warning/40 bg-warning/5 px-3 py-2 text-[12px]"
          data-testid="delivery-mapping-property-detail-condition"
        >
          <Filter className="mt-0.5 size-3.5 shrink-0 text-warning" />
          <span>
            Only when <span className="font-mono">{row.condition}</span>. A record the condition does not hold
            for is delivered without this property.
          </span>
        </p>
      )}

      <Node
        label={row.input === "List" && row.objects ? "Every object these items give, in order, each once" : SOURCE_LABEL[row.input]}
        value={row.input === "Coalesce"
          ? `${row.alternatives.length} alternatives, tried in order`
          : row.input === "List" ? `${row.items.length} item${row.items.length === 1 ? "" : "s"}` : row.sourceValue}
        testId="delivery-mapping-property-detail-source"
      >
        {(row.input === "Coalesce" || row.input === "List") && (
          <ol
            className="mt-2 flex flex-col gap-0.5 border-t pt-2"
            data-testid={row.input === "List" ? "delivery-mapping-property-detail-items" : "delivery-mapping-property-detail-alternatives"}
          >
            {(row.input === "List" ? row.items : row.alternatives).map((text, index) => (
              <li key={`${index}-${text}`} className="font-mono text-[12px] break-all">
                <span className="mr-1.5 text-muted-foreground">{index + 1}</span>
                {text}
              </li>
            ))}
          </ol>
        )}
        {lookupSteps && (
          <div className="mt-2 border-t pt-2" data-testid="delivery-mapping-property-detail-lookup">
            <p className="text-[11px] text-muted-foreground">
              {row.input === "Search"
                ? "found by searching, in order, until exactly one record on the platform matches"
                : row.findsAll
                  ? "read from every cached row that holds the key, and nothing under the fields to be empty"
                  : "found by, in order, until a cached record matches"}
            </p>
            <ul className="mt-1 flex flex-col gap-0.5">
              {row.lookupDetail.map((line, index) => (
                <li key={`${index}-${line}`} className="font-mono text-[12px] break-all">{line}</li>
              ))}
            </ul>
            {row.ignoreSeparators && (
              <p className="mt-1 text-[11px] text-muted-foreground" data-testid="delivery-mapping-property-detail-ignore-separators">
                and once more with punctuation and spacing folded away
              </p>
            )}
            {row.modifiers.length > 0 && (
              <div className="mt-2" data-testid="delivery-mapping-property-detail-modifiers">
                <p className="text-[11px] text-muted-foreground">on the value the lookup compares, first</p>
                <ul className="mt-1 flex flex-wrap gap-1">
                  {row.modifiers.map((text, index) => (
                    <li key={`${index}-${text}`} className="rounded border bg-background px-1.5 py-0.5 font-mono text-[11px]">
                      <span className="mr-1.5 text-muted-foreground">{index + 1}</span>
                      {text}
                    </li>
                  ))}
                </ul>
              </div>
            )}
          </div>
        )}
      </Node>

      {steps}
      {row.unverified && (
        <p
          className="my-1 rounded-md border border-warning/40 bg-warning/5 px-3 py-2 text-[12px]"
          data-testid="delivery-mapping-property-detail-unverified"
        >
          The id is written even when the cache holds no such record, and recorded as unverified until a refresh finds it.
        </p>
      )}
      {target && <Node label="Written to" value={row.target} target />}
      {row.assertions.length > 0 && <AssertionList row={row} />}
    </div>
  );
}

/**
 * The rules the property's value is held to (osdu/docs/reference/flow/mapping-assertions.md), one line each: what a
 * failure does, the property when it is one inside the entry's items, what the rule asserts, its name, and which value
 * it judges when that is not the one the record carries.
 */
function AssertionList({ row }: { row: PropertyRow }) {
  return (
    <section className="mt-3 flex flex-col gap-1.5" data-testid="delivery-mapping-property-assertion-list">
      <h3 className="flex items-center gap-1.5 text-[12px] font-medium">
        Assertions
        <span className="font-mono font-normal tabular-nums text-muted-foreground">{row.assertions.length}</span>
        <RichTooltip
          title="Assertions"
          body="Rules the mapping holds the value to beside the template's own. A failure that holds keeps the record from being sent until the source or the mapping changes or a release sends it; a reported one is sent and recorded; an omitted one is sent without the value."
        >
          <Info className="size-3.5 text-muted-foreground" aria-label="What an assertion does" />
        </RichTooltip>
      </h3>
      {row.assertions.map(({ target, assertion }, index) => (
        <div key={`${target}:${index}`} className="flex min-w-0 flex-wrap items-center gap-x-2 gap-y-0.5 text-[12px]" data-testid="delivery-mapping-property-assertion">
          <AssertionActionBadge action={assertion.onFail} className="text-[11px]" />
          {target !== row.target && <span className="font-mono text-[11px] text-muted-foreground">{target}</span>}
          <span className="min-w-0 break-words font-mono text-[11px]">{assertionText(assertion)}</span>
          {assertion.name !== null && assertion.name.trim() !== "" && <span className="text-muted-foreground">{assertion.name}</span>}
          {assertion.stage === "incoming" && <span className="text-muted-foreground">on the value the row gives</span>}
          {assertion.anyValue && <span className="text-muted-foreground">one of its values</span>}
          {assertion.description !== null && assertion.description.trim() !== "" && <GlyphRef icon={Info} title="Description" body={assertion.description} />}
        </div>
      ))}
    </section>
  );
}
