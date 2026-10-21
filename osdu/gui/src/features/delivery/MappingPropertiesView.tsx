import { useMemo, useState, type ReactNode } from "react";
import { Info, OctagonAlert, TriangleAlert } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Label } from "@/components/ui/label";
import { Switch } from "@/components/ui/switch";
import { cn } from "@/lib/utils";
import { DataTable, type Column } from "@/components/DataTable";
import { FilterBar } from "@/components/FilterBar";
import { GlyphRef } from "@/components/GlyphRef";
import { RichTooltip } from "@/components/RichTooltip";
import { SearchInput } from "@/components/SearchInput";
import { useClipped } from "@/components/useClipped";
import { assertionLine } from "./mappingAssertionText";
import { EntryDetail } from "./MappingEntryDetail";
import { propertyRow, type PropertyRow } from "./mappingDraft";
import { useParsedMapping } from "./useParsedMapping";
import { splitPath } from "./templateFormat";
import { ProblemView } from "./TemplateSheet";

/** A formula on one line, clipped to the width its column leaves, with the whole of it on hover once anything is hidden. */
function FormulaLine({ text, title, children }: { text: string; title: string; children: ReactNode }) {
  const [ref, clipped] = useClipped(text);
  const line = (
    <span ref={ref} className="block max-w-full overflow-hidden text-ellipsis whitespace-nowrap font-mono text-[12px]">
      {children}
    </span>
  );

  return clipped ? <RichTooltip body={text} title={title} mono>{line}</RichTooltip> : line;
}

/** How many assertions a property states (osdu/docs/reference/flow/mapping-assertions.md), as a quiet chip whose tooltip lists them. */
function AssertionChip({ row }: { row: PropertyRow }) {
  if (row.assertions.length === 0) {
    return null;
  }

  const count = row.assertions.length;
  return (
    <RichTooltip title={`${count} assertion${count === 1 ? "" : "s"}`} body={row.assertions.map((a) => assertionLine(a.assertion, a.target, row.target)).join("\n")}>
      <Badge variant="outline" className="shrink-0 text-[10px] font-normal" data-testid="delivery-mapping-property-assertions">
        {`${count} assertion${count === 1 ? "" : "s"}`}
      </Badge>
    </RichTooltip>
  );
}

const columns: Column<PropertyRow>[] = [
  {
    id: "mapping",
    header: "From, to the property it fills",
    fill: true,
    floor: 320,
    /*
     * One line per entry, read the way the renderer reads it: the value's origin, the cached record it is found by,
     * what is done to it, then the property it lands on. The origin is muted and the property's own name carries the
     * weight, so the eye still runs down the properties although the line before each is as long as it is.
     */
    render: (row) => {
      const { parent, leaf } = splitPath(row.target);
      return (
        <span className="flex min-w-0 items-center gap-1.5" data-testid={`delivery-mapping-property-${row.target}`}>
          <FormulaLine text={row.detail} title="Mapping">
            <span className="text-muted-foreground">{row.source}</span>
            {row.lookupPhrase !== "" && <span className="text-muted-foreground">{` ${row.lookupPhrase}`}</span>}
            {row.modifierKinds !== "" && <span className="text-muted-foreground">{` | ${row.modifierKinds}`}</span>}
            <span className="px-2 text-muted-foreground">&rarr;</span>
            <span className="text-muted-foreground">{parent}</span>
            <span className="font-medium">{leaf}</span>
            {row.condition !== null && <span className="text-muted-foreground">{` when ${row.condition}`}</span>}
          </FormulaLine>
          {!row.required && <Badge variant="outline" className="shrink-0 text-[10px]">optional</Badge>}
          <AssertionChip row={row} />
          {row.description !== null && row.description !== "" && <GlyphRef icon={Info} title="Description" body={row.description} />}
        </span>
      );
    },
  },
];

/** One property as the pipeline that fills it, in a dialog: the row's own detail, under the property it fills, with its assertions. */
function PropertyDialog({ row, onClose }: { row: PropertyRow | null; onClose: () => void }) {
  // The dialog fades out showing what it showed, so the last property stays rendered while it closes.
  const [shown, setShown] = useState<PropertyRow | null>(row);
  if (row !== null && row !== shown) {
    setShown(row);
  }

  const { parent, leaf } = splitPath(shown?.target ?? "");

  return (
    <Dialog open={row !== null} onOpenChange={(open) => { if (!open) { onClose(); } }}>
      <DialogContent className="sm:max-w-xl" data-testid="delivery-mapping-property-detail">
        {shown !== null && (
          <>
            <DialogHeader>
              <DialogTitle className="flex flex-wrap items-center gap-2 font-mono text-[13px] break-all">
                <span>
                  <span className="font-normal text-muted-foreground">{parent}</span>
                  {leaf}
                </span>
                {!shown.required && <Badge variant="outline" className="font-sans text-[10px] font-normal">optional</Badge>}
              </DialogTitle>
              {shown.description !== null && shown.description !== "" && (
                <DialogDescription>{shown.description}</DialogDescription>
              )}
            </DialogHeader>

            <EntryDetail row={shown} />
          </>
        )}
      </DialogContent>
    </Dialog>
  );
}

interface MappingPropertiesViewProps {
  /** The mapping document as the catalog holds it. */
  yaml: string;
  /** Its path in the repository, which the messages name. */
  path: string;
  /** The document's content hash, so a mapping synced with new content is read again. */
  contentHash: string;
  /** The synced repository the mapping is a document of, whose template folders settle a template it names by kind alone. */
  repoId?: string | null;
}

/**
 * What the mapping fills the template's properties with, read from the document by the same parse the mapping builder
 * opens it with: one row per property, reading from the value's origin to the target it lands on, with a lookup and
 * modifiers where there are any, how many assertions it states, and the whole of a property behind its row. Nothing is
 * rendered and no data is read; this is the document as written.
 */
export function MappingPropertiesView({ yaml, path, contentHash, repoId }: MappingPropertiesViewProps) {
  const [filter, setFilter] = useState("");
  const [onlyDerived, setOnlyDerived] = useState(false);
  const [opened, setOpened] = useState<string | null>(null);
  const parsed = useParsedMapping(yaml, path, contentHash, repoId);

  const draft = parsed.data?.draft ?? null;
  const rows = useMemo(() => (draft === null ? [] : draft.entries.map(propertyRow)), [draft]);

  const term = filter.trim().toLowerCase();
  const shown = rows.filter(
    (row) => (!onlyDerived || row.lookup !== "" || row.modifiers.length > 0) && (term === "" || row.search.includes(term)),
  );

  return (
    <div className="flex min-h-0 flex-1 flex-col gap-3" data-testid="delivery-mapping-properties">
      {parsed.isError && <ProblemView error={parsed.error} testId="delivery-mapping-properties-error" />}
      {(parsed.data?.issues ?? []).map((issue, index) => (
        <p
          key={`${index}-${issue.message}`}
          className={cn("flex items-start gap-1.5 text-[13px]", issue.severity === "error" ? "text-destructive" : "text-warning")}
          data-testid="delivery-mapping-properties-issue"
        >
          {issue.severity === "error"
            ? <OctagonAlert className="mt-0.5 size-3.5 shrink-0" />
            : <TriangleAlert className="mt-0.5 size-3.5 shrink-0" />}
          {issue.message}
        </p>
      ))}

      <FilterBar>
        <SearchInput
          value={filter}
          onChange={setFilter}
          placeholder="Property, source, lookup, modifier or assertion"
          label="Filter the properties"
          testId="delivery-mapping-properties-filter"
        />
        <Label className="flex items-center gap-2 text-[13px] font-normal">
          <Switch checked={onlyDerived} onCheckedChange={setOnlyDerived} data-testid="delivery-mapping-properties-only-derived" />
          Only properties with a lookup or a modifier
        </Label>
      </FilterBar>

      <p className="text-xs text-muted-foreground" data-testid="delivery-mapping-properties-count">
        {parsed.isSuccess && draft === null
          ? "The document does not parse, so the properties it fills cannot be listed."
          : `${shown.length} of ${rows.length} properties. A property of the template the mapping does not list is left out of the record.`}
      </p>

      <DataTable
        columns={columns}
        rows={parsed.isPending ? undefined : shown}
        rowKey={(row) => row.target}
        onRowClick={(row) => setOpened(row.target)}
        emptyMessage={rows.length === 0 ? "This mapping fills no property." : "No property matches the filter."}
        skeletonRows={8}
        data-testid="delivery-mapping-properties-table"
      />

      <PropertyDialog row={rows.find((row) => row.target === opened) ?? null} onClose={() => setOpened(null)} />
    </div>
  );
}
