// How the entry editor's form finds what a cache entry or a search reads: the lines one record is found by, tried in order,
// or, for a cache entry reading every matching row, the field the rows are found by and the fields that narrow them.

import { Plus, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Switch } from "@/components/ui/switch";
import type { DeliveryCachedType, MappingDraft, MappingDraftLookup } from "../../../api/delivery";
import { ChoiceOrText, FindByLinesEditor, IconAction, Section, type ChoiceOption } from "../MappingEditorParts";
import type { FindLine } from "../mappingDraft";
import { fieldOptionsOf, lookupFieldsOf, lookupOptionsOf, typeFieldsOf, type FindAllMode } from "./entryFormModel";
import type { FindAllState } from "./useFindAll";

/** The lines a cache entry or a search finds its one record by, and for a cache entry whether names are also compared folded. */
export function EntryFindBySection({
  choice, draft, repoTypes, cacheType, findBy, onFindBy, columnsList, columnPlaceholder, ignoreSeparators, onIgnoreSeparators,
}: {
  choice: "Cache" | "Search";
  draft: MappingDraft;
  repoTypes: DeliveryCachedType[];
  /** The cached type a cache entry reads, or the search a search entry looks in. */
  cacheType: string;
  findBy: FindLine[];
  onFindBy: (lines: FindLine[]) => void;
  columnsList: string;
  columnPlaceholder: string;
  ignoreSeparators: boolean;
  onIgnoreSeparators: (on: boolean) => void;
}) {
  const typeFields = typeFieldsOf(repoTypes, cacheType);
  // The properties other entries already compare in the same search, which is where a new line most often looks.
  const searchedFields: ChoiceOption[] = [...new Set(draft.entries
    .filter((candidate) => candidate.input === "Search" && candidate.cacheType === cacheType.trim())
    .flatMap((candidate) => candidate.findBy.map((find) => find.field))
    .filter((field) => field !== ""))]
    .map((field) => ({ value: field, group: "Compared by this search's entries" }));

  return (
    <FindByLinesEditor
      lines={findBy}
      onChange={onFindBy}
      title="Find the record by"
      hint={choice === "Search"
        ? "Tried in order. Each line asks the platform for the one record whose property is exactly the value, and the first that finds one wins. Several matching records, or a value that cannot be asked for, hold the record."
        : "Tried in order. The first line that finds a record wins, and several matching records hold the record. A line may compare several fields, each added with +: the record has to meet them all."}
      fieldOptions={choice === "Search" ? searchedFields : fieldOptionsOf(repoTypes, cacheType)}
      fieldPlaceholder={choice === "Search" ? "data.FacilityName" : "Cached field"}
      newField={choice === "Search" ? "" : typeFields[0] ?? "id"}
      columnsList={columnsList}
      columnPlaceholder={columnPlaceholder}
      emptyText={choice === "Search" ? "Add at least one line, or no record can be searched for." : "Add at least one line, or no cached record can be found."}
      allowAnd={choice === "Cache"}
      testId="mapping-builder-entry-findby"
    >
      {choice === "Cache" && (
        <Label className="flex items-center gap-2 text-[13px] font-normal">
          <Switch checked={ignoreSeparators} onCheckedChange={onIgnoreSeparators} data-testid="mapping-builder-entry-ignore-separators" />
          Also try with punctuation and spacing folded away (for names, never for codes)
        </Label>
      )}
    </FindByLinesEditor>
  );
}

/** The rows a cache entry reading every matching row reads: the field compared, with what, and the fields that narrow them. */
export function FindAllSection({
  findAll, lookups, repoTypes, cacheType, columnsList, columnPlaceholder,
}: {
  findAll: FindAllState;
  lookups: MappingDraftLookup[];
  repoTypes: DeliveryCachedType[];
  /** The cached type whose rows are read. */
  cacheType: string;
  columnsList: string;
  columnPlaceholder: string;
}) {
  const fieldOptions = fieldOptionsOf(repoTypes, cacheType);
  const lookupOptions = lookupOptionsOf(lookups);

  return (
    <Section
      title="Rows to read"
      hint="Every row whose field holds the value is read, ignoring case; a field holding a list is read when any of its values is the value, and a value a lookup's record holds several of keys each. A reference finds the rows naming the record with or without the separator before its version. The rows are then narrowed to those holding nothing under each field listed below, and to those whose text under one field holds their text under another, compared by letters and digits alone and ignoring case, so United Kingdom is found in unitedkingdom."
      testId="mapping-builder-entry-findall"
    >
      <div className="flex flex-wrap items-center gap-1.5 rounded-md border border-border p-2">
        <ChoiceOrText
          value={findAll.field}
          options={fieldOptions}
          onChange={findAll.setField}
          placeholder="FieldIDList"
          testId="mapping-builder-entry-findall-field"
        />
        <span className="font-mono text-[12px] text-muted-foreground">=</span>
        <Select value={findAll.mode} onValueChange={(next) => findAll.setMode(next as FindAllMode)}>
          <SelectTrigger size="sm" className="h-8 w-44" data-testid="mapping-builder-entry-findall-mode">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="column">dataset column</SelectItem>
            <SelectItem value="text">fixed text</SelectItem>
            {(lookups.length > 0 || findAll.mode === "lookup") && <SelectItem value="lookup">lookup record field</SelectItem>}
          </SelectContent>
        </Select>
        {findAll.mode === "lookup" ? (
          <>
            <ChoiceOrText
              value={findAll.lookup}
              options={lookupOptions}
              onChange={findAll.setLookup}
              placeholder="wellbore"
              testId="mapping-builder-entry-findall-lookup"
            />
            <span className="font-mono text-[12px] text-muted-foreground">.</span>
            <ChoiceOrText
              value={findAll.path}
              options={lookupFieldsOf(lookups, repoTypes, findAll.lookup).filter((option) => option.value !== "id")}
              onChange={findAll.setPath}
              placeholder="GeoContexts.FieldID"
              testId="mapping-builder-entry-findall-path"
            />
          </>
        ) : (
          <Input
            list={findAll.mode === "column" ? columnsList : undefined}
            className="h-8 min-w-40 flex-1 font-mono text-[12px]"
            placeholder={findAll.mode === "column" ? columnPlaceholder : "United States"}
            value={findAll.value}
            onChange={(event) => findAll.setValue(event.target.value)}
            data-testid="mapping-builder-entry-findall-value"
          />
        )}
      </div>
      {findAll.empty.map((field, index) => (
        <div key={index} className="flex flex-wrap items-center gap-1.5" data-testid={`mapping-builder-entry-findall-empty-${index}`}>
          <span className="shrink-0 whitespace-nowrap text-xs text-muted-foreground">and nothing under</span>
          <ChoiceOrText
            value={field}
            options={fieldOptions.filter((option) => option.value !== "id")}
            onChange={(value) => findAll.setEmpty((current) => current.map((old, i) => (i === index ? value : old)))}
            placeholder="FieldList"
            testId={`mapping-builder-entry-findall-empty-field-${index}`}
          />
          <IconAction
            label="Remove this field"
            onClick={() => findAll.setEmpty((current) => current.filter((_, i) => i !== index))}
            testId={`mapping-builder-entry-findall-empty-remove-${index}`}
          >
            <X />
          </IconAction>
        </div>
      ))}
      <Button
        variant="ghost"
        size="xs"
        className="self-start"
        onClick={() => findAll.setEmpty((current) => [...current, ""])}
        data-testid="mapping-builder-entry-findall-empty-add"
      >
        <Plus />
        Add a field that must be empty
      </Button>
      {findAll.contains.map((line, index) => (
        <div key={index} className="flex flex-wrap items-center gap-1.5" data-testid={`mapping-builder-entry-findall-contains-${index}`}>
          <span className="shrink-0 whitespace-nowrap text-xs text-muted-foreground">and whose</span>
          <ChoiceOrText
            value={line.field}
            options={fieldOptions.filter((option) => option.value !== "id")}
            onChange={(value) => findAll.setContains((current) => current.map((old, i) => (i === index ? { ...old, field: value } : old)))}
            placeholder="GroupName"
            testId={`mapping-builder-entry-findall-contains-field-${index}`}
          />
          <span className="shrink-0 whitespace-nowrap text-xs text-muted-foreground">contains its</span>
          <ChoiceOrText
            value={line.part}
            options={fieldOptions.filter((option) => option.value !== "id")}
            onChange={(value) => findAll.setContains((current) => current.map((old, i) => (i === index ? { ...old, part: value } : old)))}
            placeholder="CountryName"
            testId={`mapping-builder-entry-findall-contains-part-${index}`}
          />
          <IconAction
            label="Remove this comparison"
            onClick={() => findAll.setContains((current) => current.filter((_, i) => i !== index))}
            testId={`mapping-builder-entry-findall-contains-remove-${index}`}
          >
            <X />
          </IconAction>
        </div>
      ))}
      <Button
        variant="ghost"
        size="xs"
        className="self-start"
        onClick={() => findAll.setContains((current) => [...current, { field: "", part: "" }])}
        data-testid="mapping-builder-entry-findall-contains-add"
      >
        <Plus />
        Add a field that must contain another
      </Button>
    </Section>
  );
}
