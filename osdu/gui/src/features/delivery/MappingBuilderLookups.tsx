import { useId, useMemo, useState } from "react";
import { OctagonAlert, Pencil, Plus, Search, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Sheet, SheetContent, SheetDescription, SheetFooter, SheetHeader, SheetTitle } from "@/components/ui/sheet";
import { Switch } from "@/components/ui/switch";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import type { DeliveryCachedType, MappingDraft, MappingDraftIssue, MappingDraftLookup, MappingDraftModifier } from "../../api/delivery";
import { isLookupEntityType } from "./cacheFormat";
import { ChoiceOrText, FindByLinesEditor, IconAction, ModifierListEditor, Section, type ChoiceOption } from "./MappingEditorParts";
import {
  COLUMN_NAME, emptyEntry, findLinesOf, findsOf, ID_MODIFIER_KINDS, knownColumns, LOOKUP_NAME, LOOKUP_TARGET_PREFIX, lookupReaders, lookupText,
  MODIFIER_KINDS, modifierText, newFindLine, type FindLine,
} from "./mappingDraft";

/** The lookup the editor edits: the lookup as the draft holds it (null for a new one), and a number per opening. */
export interface LookupEditing {
  lookup: MappingDraftLookup | null;
  session: number;
}

/** How a lookup finds its record, in one phrase: `FacilityName/NameAliases.AliasName = dataset.wellbore_uwi`. */
function findText(lookup: MappingDraftLookup): string {
  return lookupText({ ...emptyEntry("", "Cache"), findBy: lookup.findBy });
}

function LookupForm({
  editing, draft, cacheTypes, issues, onSave, onClose,
}: {
  editing: LookupEditing;
  draft: MappingDraft;
  cacheTypes: DeliveryCachedType[];
  issues: MappingDraftIssue[];
  onSave: (previous: string | null, lookup: MappingDraftLookup) => void;
  onClose: () => void;
}) {
  const ids = useId();
  const original = editing.lookup;
  const [name, setName] = useState(original?.name ?? "");
  const [cacheType, setCacheType] = useState(original?.cacheType ?? cacheTypes.find((type) => !isLookupEntityType(type.entityType))?.name ?? "");
  const [findBy, setFindBy] = useState<FindLine[]>(() => (original === null
    ? [newFindLine(cacheTypes.find((type) => type.name === cacheType)?.fields[0] ?? "")]
    : findLinesOf(original.findBy)));
  const [modifiers, setModifiers] = useState<MappingDraftModifier[]>(original?.modifiers ?? []);
  const [ignoreSeparators, setIgnoreSeparators] = useState(original?.ignoreSeparators ?? false);
  const [description, setDescription] = useState(original?.description ?? "");

  // A lookup reads the dataset's own row wherever it is read, so the columns it offers are that row's.
  const columns = useMemo(() => knownColumns(draft).columns.filter((column) => COLUMN_NAME.test(column)), [draft]);
  const fields = cacheTypes.find((type) => type.name === cacheType.trim())?.fields ?? [];
  const typeOptions: ChoiceOption[] = [
    ...cacheTypes.filter((type) => !isLookupEntityType(type.entityType)).map((type) => ({ value: type.name, hint: type.entityType, group: "OSDU types" })),
    ...cacheTypes.filter((type) => isLookupEntityType(type.entityType)).map((type) => ({ value: type.name, hint: type.entityType, group: "Lookup tables" })),
  ];
  const trimmed = name.trim();
  const readers = original === null ? [] : lookupReaders(draft.entries, original.name);
  const nameError = trimmed === ""
    ? "Name the lookup."
    : !LOOKUP_NAME.test(trimmed)
      ? "A lookup's name holds letters, digits, underscores and hyphens."
      : trimmed !== original?.name && draft.lookups.some((lookup) => lookup.name === trimmed) ? `The mapping already has a lookup named ${trimmed}.` : null;
  const columnsList = `${ids}-columns`;

  const save = () => {
    if (nameError !== null) {
      return;
    }

    onSave(original?.name ?? null, {
      name: trimmed,
      cacheType: cacheType.trim(),
      findBy: findsOf(findBy),
      modifiers: modifiers.map((modifier) => (modifier.kind === "date" && (modifier.text ?? "").trim() === "" ? { ...modifier, text: null } : modifier)),
      ignoreSeparators,
      description: description.trim() === "" ? null : description.trim(),
    });
  };

  return (
    <>
      <SheetHeader>
        <SheetTitle className="break-all pr-6 font-mono text-[14px]" data-testid="mapping-builder-lookup-title">
          {original === null ? "New lookup" : `lookups.${original.name}`}
        </SheetTitle>
        <SheetDescription>
          A record found once for a row, in the partition's cache, and read wherever the record needs one of its fields.
        </SheetDescription>
      </SheetHeader>
      <div className="flex flex-1 flex-col gap-5 overflow-y-auto px-4 pb-4">
        <datalist id={columnsList}>
          {columns.map((column) => <option key={column} value={column} />)}
        </datalist>
        {issues.length > 0 && (
          <div className="flex flex-col gap-1 rounded-md border border-border bg-muted/30 p-2" data-testid="mapping-builder-lookup-issues">
            {issues.map((issue, index) => (
              <p key={`${index}-${issue.message}`} className="flex items-start gap-1.5 text-xs text-destructive">
                <OctagonAlert className="mt-px size-3.5 shrink-0" />
                {issue.message}
              </p>
            ))}
            <p className="text-[11px] text-muted-foreground">From the last check. It runs again when you save.</p>
          </div>
        )}

        <Section
          title="Name"
          hint={readers.length > 0
            ? `Entries read it as lookup.${original?.name ?? ""}.<field>. Renaming it renames it in the ${readers.length} ${readers.length === 1 ? "entry" : "entries"} reading it.`
            : "Entries read it as lookup.<name>.<field>, such as wellbore.id."}
        >
          <Input
            className="h-8 w-60 font-mono"
            placeholder="wellbore"
            value={name}
            onChange={(event) => setName(event.target.value)}
            aria-invalid={nameError !== null && name !== ""}
            data-testid="mapping-builder-lookup-name"
          />
        </Section>

        <Section
          title="Cached type"
          hint={cacheTypes.length === 0
            ? "No cache is picked for this mapping, so the type cannot be checked here; the check against the partition's cache does it."
            : "The type the record is found in. A lookup suits a type the cache holds whole, such as wellbores with the few paths a capture keeps; a set no capture can keep is searched for instead."}
        >
          <ChoiceOrText
            value={cacheType}
            options={typeOptions}
            onChange={setCacheType}
            placeholder="Wellbore"
            testId="mapping-builder-lookup-type"
          />
        </Section>

        <FindByLinesEditor
          lines={findBy}
          onChange={setFindBy}
          title="Find the record by"
          hint="Tried in order, each comparing a field of the cached record with a column of the dataset's own row or a fixed text; the first line that finds exactly one record wins. A line may compare several fields, each added with +, and the record has to meet them all. A lookup that finds nothing holds a required entry reading it and gives nothing to one that is not; several matching records hold the record."
          fieldOptions={[...new Set([...fields, "id"])].map((field) => ({ value: field, group: "Cached fields" }))}
          fieldPlaceholder="FacilityName"
          newField={fields[0] ?? ""}
          columnsList={columnsList}
          columnPlaceholder="wellbore_uwi"
          emptyText="Add at least one line, or no record can be found."
          allowAnd
          testId="mapping-builder-lookup-findby"
        >
          <Label className="flex items-center gap-2 text-[13px] font-normal">
            <Switch checked={ignoreSeparators} onCheckedChange={setIgnoreSeparators} data-testid="mapping-builder-lookup-ignore-separators" />
            Also try with punctuation and spacing folded away (for names, never for codes)
          </Label>
        </FindByLinesEditor>

        <ModifierListEditor
          modifiers={modifiers}
          onChange={setModifiers}
          kinds={MODIFIER_KINDS.filter((kind) => !ID_MODIFIER_KINDS.includes(kind))}
          cacheTypes={cacheTypes}
          hint="Change the dataset value each line compares, top to bottom. Cached values are never modified, and the record's id is read as lookup.<name>.id, so no modifier builds one here."
          testId="mapping-builder-lookup-modifier"
          sectionTestId="mapping-builder-lookup-modifiers"
        />

        <Section title="Description">
          <Input
            className="h-8"
            placeholder="Which record this is, and how it is found (optional)"
            value={description}
            onChange={(event) => setDescription(event.target.value)}
            data-testid="mapping-builder-lookup-description"
          />
        </Section>
      </div>
      <SheetFooter className="flex-row flex-wrap items-center justify-end gap-2 border-t border-border">
        {nameError !== null && <p className="mr-auto text-xs font-medium text-destructive" data-testid="mapping-builder-lookup-error">{nameError}</p>}
        <Button variant="ghost" size="sm" onClick={onClose} data-testid="mapping-builder-lookup-cancel">Cancel</Button>
        <Button size="sm" onClick={save} disabled={nameError !== null} data-testid="mapping-builder-lookup-save">Save lookup</Button>
      </SheetFooter>
    </>
  );
}

/**
 * The mapping's lookups: each record the mapping finds once for a row and reads wherever it needs one of its fields, with
 * how it is found and which entries read it. A lookup is added and edited in a sheet; one that entries read is removed
 * only once they read something else, and a renamed one keeps its readers.
 */
export function MappingBuilderLookups({
  draft, cacheTypes, issues, editing, onOpen, onClose, onSave, onRemove,
}: {
  draft: MappingDraft;
  cacheTypes: DeliveryCachedType[];
  /** Every issue of the last check; the card and the editor show the ones about a lookup. */
  issues: MappingDraftIssue[];
  editing: LookupEditing | null;
  onOpen: (lookup: MappingDraftLookup | null) => void;
  onClose: () => void;
  onSave: (previous: string | null, lookup: MappingDraftLookup) => void;
  onRemove: (name: string) => void;
}) {
  // The sheet slides out showing what it showed, so the last lookup stays rendered while it closes.
  const [shown, setShown] = useState<LookupEditing | null>(editing);
  if (editing !== null && editing !== shown) {
    setShown(editing);
  }

  const issuesOf = (name: string) => issues.filter((issue) => issue.target === LOOKUP_TARGET_PREFIX + name);

  return (
    <Card className="gap-3 rounded-lg p-4" data-testid="mapping-builder-lookups">
      <div className="flex items-center gap-2">
        <h2 className="text-[13px] font-medium">Lookups</h2>
        <Button variant="outline" size="xs" className="ml-auto" onClick={() => onOpen(null)} data-testid="mapping-builder-lookup-add">
          <Plus />
          Add a lookup
        </Button>
      </div>
      {draft.lookups.length === 0 ? (
        <p className="text-xs text-muted-foreground">
          A lookup finds one record in the partition's cache once for a row, and entries read its fields wherever the record
          needs them, so the id a record writes and the values taken from that record always come from the same one: the
          wellbore a log belongs to, whose id the log writes and whose field and country its access groups follow.
        </p>
      ) : (
        <ul className="flex flex-col gap-2">
          {draft.lookups.map((lookup) => {
            const readers = lookupReaders(draft.entries, lookup.name);
            const found = issuesOf(lookup.name);
            return (
              <li key={lookup.name} className="flex items-start gap-2 rounded-md border border-border p-2" data-testid={`mapping-builder-lookup-${lookup.name}`}>
                <Search className="mt-0.5 size-3.5 shrink-0 text-muted-foreground" />
                <div className="flex min-w-0 flex-1 flex-col gap-0.5">
                  <div className="flex flex-wrap items-baseline gap-x-2">
                    <span className="font-mono text-[13px] font-medium">{lookup.name}</span>
                    <span className="font-mono text-[12px] text-muted-foreground">{lookup.cacheType}</span>
                    {found.length > 0 && (
                      <Tooltip>
                        <TooltipTrigger asChild>
                          <span className="inline-flex items-center gap-1 text-xs text-destructive" data-testid={`mapping-builder-lookup-issues-${lookup.name}`}>
                            <OctagonAlert className="size-3.5" />
                            {found.length}
                          </span>
                        </TooltipTrigger>
                        <TooltipContent className="max-w-sm">{found.map((issue) => issue.message).join(" ")}</TooltipContent>
                      </Tooltip>
                    )}
                  </div>
                  <span className="break-all font-mono text-[12px] text-muted-foreground">
                    by {findText(lookup)}
                    {lookup.modifiers.length > 0 && ` | ${lookup.modifiers.map(modifierText).join(" | ")}`}
                    {lookup.ignoreSeparators && ", folding punctuation and spacing"}
                  </span>
                  <span className="text-xs text-muted-foreground" data-testid={`mapping-builder-lookup-readers-${lookup.name}`}>
                    {readers.length === 0
                      ? "Read by no entry yet."
                      : `Read by ${readers.map((target) => target.replace(/^osdu\./, "")).join(", ")}.`}
                  </span>
                </div>
                <IconAction label="Edit this lookup" onClick={() => onOpen(lookup)} testId={`mapping-builder-lookup-edit-${lookup.name}`}>
                  <Pencil />
                </IconAction>
                {readers.length > 0 ? (
                  <Tooltip>
                    <TooltipTrigger asChild>
                      <span tabIndex={0} className="inline-flex">
                        <Button variant="ghost" size="icon-xs" aria-label="Remove this lookup" disabled data-testid={`mapping-builder-lookup-remove-${lookup.name}`}>
                          <X />
                        </Button>
                      </span>
                    </TooltipTrigger>
                    <TooltipContent>Entries read it; change them to read something else first.</TooltipContent>
                  </Tooltip>
                ) : (
                  <IconAction label="Remove this lookup" onClick={() => onRemove(lookup.name)} testId={`mapping-builder-lookup-remove-${lookup.name}`}>
                    <X />
                  </IconAction>
                )}
              </li>
            );
          })}
        </ul>
      )}
      <Sheet open={editing !== null} onOpenChange={(open) => { if (!open) { onClose(); } }}>
        <SheetContent className="w-full gap-0 sm:max-w-2xl" data-testid="mapping-builder-lookup-editor">
          {shown !== null && (
            <LookupForm
              key={shown.session}
              editing={shown}
              draft={draft}
              cacheTypes={cacheTypes}
              issues={shown.lookup === null ? [] : issuesOf(shown.lookup.name)}
              onSave={onSave}
              onClose={onClose}
            />
          )}
        </SheetContent>
      </Sheet>
    </Card>
  );
}
