// The pieces the mapping builder's editors share: a section, an icon action, a pick-or-type select, the findBy lines a
// cached record is found by, and the modifiers a dataset value goes through. The entry editor and the lookup editor both
// edit those, so both edit them here.

import { useState, type ReactNode } from "react";
import { ArrowDown, ArrowUp, Plus, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import {
  Select, SelectContent, SelectGroup, SelectItem, SelectLabel, SelectTrigger, SelectValue,
} from "@/components/ui/select";
import { Switch } from "@/components/ui/switch";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { cn } from "@/lib/utils";
import type { DeliveryCachedType, MappingDraftModifier, MappingDraftModifierKind, MappingDraftOtherwiseKind } from "../../api/delivery";
import { isLookupEntityType } from "./cacheFormat";
import {
  cachedReplaceFields, DECIMAL_SEPARATORS, GROUP_SEPARATORS, ID_TEMPLATE_EXAMPLE, isCachedReplace, moveItem, newFindLine, newModifier, NO_GROUP,
  type FindLine, type FindPart,
} from "./mappingDraft";

export function IconAction({
  label, onClick, disabled = false, testId, children,
}: { label: string; onClick: () => void; disabled?: boolean; testId: string; children: ReactNode }) {
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <Button variant="ghost" size="icon-xs" aria-label={label} onClick={onClick} disabled={disabled} data-testid={testId}>
          {children}
        </Button>
      </TooltipTrigger>
      <TooltipContent>{label}</TooltipContent>
    </Tooltip>
  );
}

export function Section({
  title, hint, action, testId, children,
}: { title: string; hint?: ReactNode; action?: ReactNode; testId?: string; children?: ReactNode }) {
  return (
    <section className="flex flex-col gap-2" data-testid={testId}>
      <div className="flex min-h-7 items-center gap-2">
        <h3 className="text-[13px] font-medium">{title}</h3>
        {action !== undefined && <div className="ml-auto">{action}</div>}
      </div>
      {hint !== undefined && <p className="text-xs text-muted-foreground">{hint}</p>}
      {children}
    </section>
  );
}

const OTHER = "__other__";

export interface ChoiceOption {
  value: string;
  hint?: string;
  group: string;
}

/** A select of the names the repository knows, with a way to type one it does not list. */
export function ChoiceOrText({
  value, options, onChange, placeholder, testId, className,
}: { value: string; options: ChoiceOption[]; onChange: (value: string) => void; placeholder: string; testId: string; className?: string }) {
  const known = options.some((option) => option.value === value);
  const [typing, setTyping] = useState(value !== "" && !known);
  const showInput = typing || (value !== "" && !known);
  const groups = [...new Set(options.map((option) => option.group))];

  return (
    <div className={cn("flex flex-wrap items-center gap-1", className)}>
      <Select
        value={showInput ? OTHER : value}
        onValueChange={(next) => {
          if (next === OTHER) {
            setTyping(true);
            onChange("");
          } else {
            setTyping(false);
            onChange(next);
          }
        }}
      >
        <SelectTrigger size="sm" className="h-8 min-w-40" data-testid={testId}>
          <SelectValue placeholder={placeholder} />
        </SelectTrigger>
        <SelectContent>
          {groups.map((group) => (
            <SelectGroup key={group}>
              <SelectLabel>{group}</SelectLabel>
              {options.filter((option) => option.group === group).map((option) => (
                <SelectItem key={option.value} value={option.value}>
                  {option.value}
                  {option.hint !== undefined && <span className="font-mono text-[11px] text-muted-foreground">{option.hint}</span>}
                </SelectItem>
              ))}
            </SelectGroup>
          ))}
          <SelectItem value={OTHER}>Type a name</SelectItem>
        </SelectContent>
      </Select>
      {showInput && (
        <Input
          className="h-8 w-44 font-mono text-[12px]"
          placeholder={placeholder}
          value={value}
          onChange={(event) => onChange(event.target.value)}
          data-testid={`${testId}-text`}
        />
      )}
    </div>
  );
}

const DEFAULT_FIELD = "__default__";

/**
 * A field of a cached table, picked from those it holds or typed, with the field the table settles on its own offered
 * first when there is one: a lookup table's key to match on, and the one field it holds beside its key to replace by.
 */
function CachedFieldSelect({
  value, settled, settledLabel, options, onChange, placeholder, testId,
}: {
  value: string | null;
  settled: string | null;
  settledLabel: string;
  options: string[];
  onChange: (value: string | null) => void;
  placeholder: string;
  testId: string;
}) {
  const named = value !== null && value.trim() !== "";
  const known = named && options.includes(value);
  const [typing, setTyping] = useState(named && !known);
  const showInput = typing || (named && !known);
  const selected = showInput ? OTHER : named ? value : settled !== null ? DEFAULT_FIELD : "";

  return (
    <div className="flex flex-wrap items-center gap-1">
      <Select
        value={selected}
        onValueChange={(next) => {
          if (next === OTHER) {
            setTyping(true);
            onChange("");
          } else {
            setTyping(false);
            onChange(next === DEFAULT_FIELD ? null : next);
          }
        }}
      >
        <SelectTrigger size="sm" className="h-7 min-w-40" data-testid={testId}>
          <SelectValue placeholder={placeholder} />
        </SelectTrigger>
        <SelectContent>
          {settled !== null && (
            <SelectItem value={DEFAULT_FIELD}>
              {settledLabel}
              <span className="font-mono text-[11px] text-muted-foreground">{settled}</span>
            </SelectItem>
          )}
          {options.length > 0 && (
            <SelectGroup>
              <SelectLabel>Cached fields</SelectLabel>
              {options.map((option) => <SelectItem key={option} value={option}>{option}</SelectItem>)}
            </SelectGroup>
          )}
          <SelectItem value={OTHER}>Type a name</SelectItem>
        </SelectContent>
      </Select>
      {showInput && (
        <Input
          className="h-7 w-44 font-mono text-[12px]"
          placeholder={placeholder}
          value={value ?? ""}
          onChange={(event) => onChange(event.target.value)}
          data-testid={`${testId}-text`}
        />
      )}
    </div>
  );
}

/**
 * A replace: the pairs written in the mapping, or a table read from the partition's cache (a dictionary, an ingestion
 * table, or OSDU reference data), and what a value the table does not list becomes.
 */
function ReplaceEditor({
  modifier, index, cacheTypes, onChange, testId,
}: {
  modifier: MappingDraftModifier;
  index: number;
  cacheTypes: DeliveryCachedType[];
  onChange: (patch: Partial<MappingDraftModifier>) => void;
  testId: string;
}) {
  const cached = isCachedReplace(modifier) || modifier.table === "";
  const tableName = (modifier.table ?? "").trim();
  const type = cacheTypes.find((candidate) => candidate.name === tableName);
  const lookupTables = cacheTypes.filter((candidate) => isLookupEntityType(candidate.entityType));
  const osduTypes = cacheTypes.filter((candidate) => !isLookupEntityType(candidate.entityType));
  const tableOptions: ChoiceOption[] = [
    ...lookupTables.map((candidate) => ({
      value: candidate.name,
      hint: candidate.key === null ? undefined : `key ${candidate.key}`,
      group: "Lookup tables (dictionaries and ingestion tables)",
    })),
    ...osduTypes.map((candidate) => ({ value: candidate.name, hint: candidate.entityType, group: "OSDU types" })),
  ];
  const fields = cachedReplaceFields(modifier, type);
  const key = type?.key ?? null;
  const lookup = type !== undefined && key !== null;
  const matchOptions = type === undefined ? [] : key !== null ? [...new Set([key, ...type.fields])] : [...new Set([...type.fields, "id"])];
  const fieldOptions = type === undefined ? [] : key !== null ? type.fields.filter((name) => name !== key) : [...new Set([...type.fields, "id"])];

  const switchTo = (next: string) => {
    if (next === "cache" && !cached) {
      onChange({ replacements: null, table: lookupTables[0]?.name ?? cacheTypes[0]?.name ?? "", match: null, field: null });
    } else if (next === "pairs" && cached) {
      onChange({ replacements: [{ from: "", to: "" }], table: null, match: null, field: null });
    }
  };

  return (
    <div className="flex flex-col gap-2">
      <ToggleGroup
        type="single"
        variant="outline"
        size="sm"
        value={cached ? "cache" : "pairs"}
        onValueChange={(next) => { if (next !== "") { switchTo(next); } }}
        className="self-start"
        data-testid={`${testId}-source-${index}`}
      >
        <ToggleGroupItem value="pairs" data-testid={`${testId}-source-pairs-${index}`}>Values listed here</ToggleGroupItem>
        <ToggleGroupItem value="cache" data-testid={`${testId}-source-cache-${index}`}>A table in the cache</ToggleGroupItem>
      </ToggleGroup>

      {cached ? (
        <div className="flex flex-col gap-2 text-xs text-muted-foreground">
          <div className="flex flex-wrap items-center gap-2">
            <span className="w-28">Table</span>
            <ChoiceOrText
              value={modifier.table ?? ""}
              options={tableOptions}
              onChange={(value) => onChange({ table: value, match: null, field: null })}
              placeholder="the table"
              testId={`${testId}-table-${index}`}
            />
          </div>
          {tableName !== "" && cacheTypes.length > 0 && type === undefined && (
            <p className="text-warning" data-testid={`${testId}-table-missing-${index}`}>
              The cache picked for this mapping holds no type {tableName}. A cache flow declares it, as a dictionary, an ingestion table or an OSDU kind.
            </p>
          )}
          {tableName !== "" && cacheTypes.length === 0 && (
            <p>No cache is picked for this mapping, so the table cannot be checked here; the check against the partition's cache does it.</p>
          )}
          <div className="flex flex-wrap items-center gap-2">
            <span className="w-28">Match the value on</span>
            <CachedFieldSelect
              value={modifier.match ?? null}
              settled={key}
              settledLabel="the table's key"
              options={matchOptions}
              onChange={(value) => onChange({ match: value })}
              placeholder={key !== null ? `the key, ${key}` : "Code"}
              testId={`${testId}-match-${index}`}
            />
          </div>
          <div className="flex flex-wrap items-center gap-2">
            <span className="w-28">Replace it by</span>
            <CachedFieldSelect
              value={modifier.field ?? null}
              settled={fields.fieldSettled ? fields.field : null}
              settledLabel={fieldOptions.length === 0 ? "the dictionary's value" : "the table's only field"}
              options={fieldOptions}
              onChange={(value) => onChange({ field: value })}
              placeholder={lookup ? "the field" : "id"}
              testId={`${testId}-field-${index}`}
            />
          </div>
          {type !== undefined && (fields.match === null || fields.field === null) && (
            <p className="text-warning" data-testid={`${testId}-fields-missing-${index}`}>
              {fields.match === null
                ? `${type.name} holds OSDU records, which have no key: choose the field a value is compared with, and the field that replaces it.`
                : `${type.name} holds ${fieldOptions.length} fields beside its key: choose the one that replaces the value.`}
            </p>
          )}
          <p>
            Each value is looked up in the version of the cache a render reads: an exact match first, then the one row that
            matches ignoring case. A row with nothing in that field gives no value. The rows a record used are recorded, so a
            later change to the table flags the records built from it.
          </p>
        </div>
      ) : (
        <div className="flex flex-col gap-1">
          {(modifier.replacements ?? []).map((pair, pairIndex) => (
            <div key={pairIndex} className="flex items-center gap-1">
              <Input
                className="h-7 font-mono text-[12px]"
                placeholder="incoming value"
                value={pair.from}
                onChange={(event) => onChange({
                  replacements: (modifier.replacements ?? []).map((old, i) => (i === pairIndex ? { ...old, from: event.target.value } : old)),
                })}
                data-testid={`${testId}-from-${index}-${pairIndex}`}
              />
              <span className="text-xs text-muted-foreground">becomes</span>
              <Input
                className="h-7 font-mono text-[12px]"
                placeholder={pair.to === null ? "no value" : "what it becomes"}
                value={pair.to ?? ""}
                disabled={pair.to === null}
                onChange={(event) => onChange({
                  replacements: (modifier.replacements ?? []).map((old, i) => (i === pairIndex ? { ...old, to: event.target.value } : old)),
                })}
                data-testid={`${testId}-to-${index}-${pairIndex}`}
              />
              <Tooltip>
                <TooltipTrigger asChild>
                  <span className="flex items-center gap-1 text-xs text-muted-foreground">
                    <Switch
                      checked={pair.to === null}
                      onCheckedChange={(none) => onChange({
                        replacements: (modifier.replacements ?? []).map((old, i) => (i === pairIndex ? { ...old, to: none ? null : "" } : old)),
                      })}
                      aria-label="Replace with no value"
                      data-testid={`${testId}-none-${index}-${pairIndex}`}
                    />
                    none
                  </span>
                </TooltipTrigger>
                <TooltipContent>No value: the entry's required flag decides what an empty value does.</TooltipContent>
              </Tooltip>
              <IconAction
                label="Remove this pair"
                onClick={() => onChange({ replacements: (modifier.replacements ?? []).filter((_, i) => i !== pairIndex) })}
                testId={`${testId}-pair-remove-${index}-${pairIndex}`}
              >
                <X />
              </IconAction>
            </div>
          ))}
          <Button
            variant="ghost"
            size="xs"
            className="self-start"
            onClick={() => onChange({ replacements: [...(modifier.replacements ?? []), { from: "", to: "" }] })}
            data-testid={`${testId}-pair-add-${index}`}
          >
            <Plus />
            Add a pair
          </Button>
          <p className="text-xs text-muted-foreground">
            Values are matched trimmed: an exact key first, then the one key that matches ignoring case. A list kept in one
            place for many mappings belongs in a dictionary, read here as a table in the cache.
          </p>
        </div>
      )}

      <div className="flex flex-wrap items-center gap-2 text-xs text-muted-foreground">
        <span>{cached ? "A value the table does not list" : "A value the pairs do not list"}</span>
        <Select
          value={modifier.otherwiseKind ?? "keep"}
          onValueChange={(value) => onChange({
            otherwiseKind: value as MappingDraftOtherwiseKind,
            otherwiseText: value === "text" ? (modifier.otherwiseText ?? "") : null,
          })}
        >
          <SelectTrigger size="sm" className="h-7 w-40" data-testid={`${testId}-otherwise-${index}`}>
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="keep">is left as it is</SelectItem>
            <SelectItem value="empty">gives no value</SelectItem>
            <SelectItem value="text">becomes</SelectItem>
          </SelectContent>
        </Select>
        {modifier.otherwiseKind === "text" && (
          <Input
            className="h-7 w-48 font-mono text-[12px]"
            placeholder="what it becomes"
            value={modifier.otherwiseText ?? ""}
            onChange={(event) => onChange({ otherwiseText: event.target.value })}
            data-testid={`${testId}-otherwise-text-${index}`}
          />
        )}
      </div>
    </div>
  );
}

/** One comparison of a findBy line: the field compared, `=`, and a dataset column or a fixed text it must equal. */
function FindPartControls({
  part, onChange, fieldOptions, fieldPlaceholder, columnsList, columnPlaceholder, testId,
}: {
  part: FindPart;
  onChange: (patch: Partial<FindPart>) => void;
  fieldOptions: ChoiceOption[];
  fieldPlaceholder: string;
  columnsList: string;
  columnPlaceholder: string;
  /** Prefixes each control: `<testId>-field`, `-mode` and `-value` with the place of the comparison appended. */
  testId: { prefix: string; at: string };
}) {
  return (
    <>
      <ChoiceOrText
        value={part.field}
        options={fieldOptions}
        onChange={(field) => onChange({ field })}
        placeholder={fieldPlaceholder}
        testId={`${testId.prefix}-field-${testId.at}`}
      />
      <span className="font-mono text-[12px] text-muted-foreground">=</span>
      <Select value={part.mode} onValueChange={(next) => onChange({ mode: next === "text" ? "text" : "column" })}>
        <SelectTrigger size="sm" className="h-8 w-40" data-testid={`${testId.prefix}-mode-${testId.at}`}>
          <SelectValue />
        </SelectTrigger>
        <SelectContent>
          <SelectItem value="column">dataset column</SelectItem>
          <SelectItem value="text">fixed text</SelectItem>
        </SelectContent>
      </Select>
      <Input
        list={part.mode === "column" ? columnsList : undefined}
        className="h-8 min-w-40 flex-1 font-mono text-[12px]"
        placeholder={part.mode === "column" ? columnPlaceholder : "KellyBushing"}
        value={part.value}
        onChange={(event) => onChange({ value: event.target.value })}
        data-testid={`${testId.prefix}-value-${testId.at}`}
      />
    </>
  );
}

/**
 * The findBy lines a record is found by, tried in order: each the field compared and a dataset column or a fixed text it
 * must equal, and, where `allowAnd` says a line may, further comparisons joined by "and" that a record must meet together
 * with the first. A line written with "and" elsewhere is shown whole wherever it is edited, so nothing of it is lost; the
 * server says why a search line may not keep it. `testId` prefixes every control, so the entry editor and the lookup
 * editor keep their own; a line's further comparisons are numbered `<line>-and-<n>`.
 */
export function FindByLinesEditor({
  lines, onChange, title, hint, fieldOptions, fieldPlaceholder, newField, columnsList, columnPlaceholder, emptyText, allowAnd, testId, children,
}: {
  lines: FindLine[];
  onChange: (lines: FindLine[]) => void;
  title: string;
  hint: string;
  fieldOptions: ChoiceOption[];
  fieldPlaceholder: string;
  /** The field a new line starts with. */
  newField: string;
  /** The id of the datalist the column inputs suggest from. */
  columnsList: string;
  columnPlaceholder: string;
  /** What is said while there is no line. */
  emptyText: string;
  /** Whether a line may compare several fields: a cache lookup's may, a search's may not. */
  allowAnd: boolean;
  testId: string;
  children?: ReactNode;
}) {
  const update = (index: number, patch: Partial<FindLine>) => onChange(lines.map((line, i) => (i === index ? { ...line, ...patch } : line)));
  const updateAnd = (index: number, at: number, patch: Partial<FindPart>) =>
    update(index, { and: lines[index].and.map((part, i) => (i === at ? { ...part, ...patch } : part)) });
  return (
    <Section
      title={title}
      hint={hint}
      action={(
        <Button variant="outline" size="xs" onClick={() => onChange([...lines, newFindLine(newField)])} data-testid={`${testId}-add`}>
          <Plus />
          Add a line
        </Button>
      )}
      testId={testId}
    >
      {lines.length === 0 && <p className="text-xs text-destructive">{emptyText}</p>}
      {lines.map((line, index) => (
        <div key={index} className="flex flex-col gap-1.5 rounded-md border border-border p-2" data-testid={`${testId}-${index}`}>
          <div className="flex flex-wrap items-center gap-1.5">
            <FindPartControls
              part={line}
              onChange={(patch) => update(index, patch)}
              fieldOptions={fieldOptions}
              fieldPlaceholder={fieldPlaceholder}
              columnsList={columnsList}
              columnPlaceholder={columnPlaceholder}
              testId={{ prefix: testId, at: String(index) }}
            />
            {allowAnd && (
              <IconAction
                label="Compare another field on this line: a record must meet both"
                onClick={() => update(index, { and: [...line.and, { field: "", mode: "column", value: "" }] })}
                testId={`${testId}-and-add-${index}`}
              >
                <Plus />
              </IconAction>
            )}
            <IconAction label="Move up" onClick={() => onChange(moveItem(lines, index, -1))} disabled={index === 0} testId={`${testId}-up-${index}`}>
              <ArrowUp />
            </IconAction>
            <IconAction label="Move down" onClick={() => onChange(moveItem(lines, index, 1))} disabled={index === lines.length - 1} testId={`${testId}-down-${index}`}>
              <ArrowDown />
            </IconAction>
            <IconAction label="Remove this line" onClick={() => onChange(lines.filter((_, i) => i !== index))} testId={`${testId}-remove-${index}`}>
              <X />
            </IconAction>
          </div>
          {line.and.map((part, at) => (
            <div key={at} className="flex flex-wrap items-center gap-1.5 pl-3" data-testid={`${testId}-${index}-and-${at}`}>
              <span className="w-7 font-mono text-[12px] text-muted-foreground">and</span>
              <FindPartControls
                part={part}
                onChange={(patch) => updateAnd(index, at, patch)}
                fieldOptions={fieldOptions}
                fieldPlaceholder={fieldPlaceholder}
                columnsList={columnsList}
                columnPlaceholder={columnPlaceholder}
                testId={{ prefix: testId, at: `${index}-and-${at}` }}
              />
              <IconAction
                label="Remove this comparison"
                onClick={() => update(index, { and: line.and.filter((_, i) => i !== at) })}
                testId={`${testId}-remove-${index}-and-${at}`}
              >
                <X />
              </IconAction>
            </div>
          ))}
        </div>
      ))}
      {children}
    </Section>
  );
}

/**
 * The modifiers a dataset value goes through, top to bottom, each with its settings, and the kinds that may be added.
 * `refHint` words the ref modifier's settings for the property it fills; without it a ref names its type in full.
 * `testId` prefixes each modifier's controls, and `sectionTestId` names the section.
 */
export function ModifierListEditor({
  modifiers, onChange, kinds, cacheTypes, hint, refHint, testId, sectionTestId, children,
}: {
  modifiers: MappingDraftModifier[];
  onChange: (modifiers: MappingDraftModifier[]) => void;
  kinds: readonly MappingDraftModifierKind[];
  cacheTypes: DeliveryCachedType[];
  hint: string;
  refHint?: { path: string; relationships: string[] };
  testId: string;
  sectionTestId: string;
  children?: ReactNode;
}) {
  const update = (index: number, patch: Partial<MappingDraftModifier>) =>
    onChange(modifiers.map((modifier, i) => (i === index ? { ...modifier, ...patch } : modifier)));
  const relationships = refHint?.relationships ?? [];

  return (
    <Section
      title="Modifiers"
      hint={hint}
      action={(
        <Select value="" onValueChange={(kind) => onChange([...modifiers, newModifier(kind as MappingDraftModifierKind)])}>
          <SelectTrigger size="sm" className="h-7 w-40" data-testid={`${testId}-add`}>
            <SelectValue placeholder="Add a modifier" />
          </SelectTrigger>
          <SelectContent>
            {kinds.map((kind) => <SelectItem key={kind} value={kind}>{kind}</SelectItem>)}
          </SelectContent>
        </Select>
      )}
      testId={sectionTestId}
    >
      {modifiers.length === 0 && <p className="text-xs text-muted-foreground">No modifiers.</p>}
      {modifiers.map((modifier, index) => (
        <div key={index} className="flex flex-col gap-2 rounded-md border border-border p-2" data-testid={`${testId}-${index}`}>
          <div className="flex items-center gap-1">
            <span className="font-mono text-[12px] font-medium">{modifier.kind}</span>
            <div className="ml-auto flex items-center gap-0.5">
              <IconAction label="Move up" onClick={() => onChange(moveItem(modifiers, index, -1))} disabled={index === 0} testId={`${testId}-up-${index}`}>
                <ArrowUp />
              </IconAction>
              <IconAction label="Move down" onClick={() => onChange(moveItem(modifiers, index, 1))} disabled={index === modifiers.length - 1} testId={`${testId}-down-${index}`}>
                <ArrowDown />
              </IconAction>
              <IconAction label="Remove this modifier" onClick={() => onChange(modifiers.filter((_, i) => i !== index))} testId={`${testId}-remove-${index}`}>
                <X />
              </IconAction>
            </div>
          </div>
          {modifier.kind === "split" && (
            <div className="flex flex-wrap items-center gap-2 text-xs text-muted-foreground">
              <span>separator</span>
              <Input
                className="h-7 w-20 font-mono text-[12px]"
                value={modifier.separator ?? ""}
                onChange={(event) => update(index, { separator: event.target.value })}
                data-testid={`${testId}-separator-${index}`}
              />
              <span>keep part</span>
              <Input
                className="h-7 w-16 font-mono text-[12px]"
                inputMode="numeric"
                value={modifier.part === null ? "" : String(modifier.part)}
                onChange={(event) => {
                  const part = Number.parseInt(event.target.value, 10);
                  update(index, { part: Number.isNaN(part) ? null : part });
                }}
                data-testid={`${testId}-part-${index}`}
              />
              <span>counting from one; a single space splits on any run of whitespace</span>
            </div>
          )}
          {modifier.kind === "replace" && (
            <ReplaceEditor modifier={modifier} index={index} cacheTypes={cacheTypes} onChange={(patch) => update(index, patch)} testId={testId} />
          )}
          {modifier.kind === "equals" && (
            <div className="flex flex-wrap items-center gap-2 text-xs text-muted-foreground">
              <span>true when the value is</span>
              <Input
                className="h-7 w-48 font-mono text-[12px]"
                value={modifier.text ?? ""}
                onChange={(event) => update(index, { text: event.target.value })}
                data-testid={`${testId}-text-${index}`}
              />
              <span>(trimmed, any case), false otherwise</span>
            </div>
          )}
          {modifier.kind === "date" && (
            <div className="flex flex-wrap items-center gap-2 text-xs text-muted-foreground">
              <span>format</span>
              <Input
                className="h-7 w-48 font-mono text-[12px]"
                placeholder="dd.MM.yyyy"
                value={modifier.text ?? ""}
                onChange={(event) => update(index, { text: event.target.value })}
                data-testid={`${testId}-format-${index}`}
              />
              <span>empty reads ISO 8601</span>
            </div>
          )}
          {modifier.kind === "id" && (
            <div className="flex flex-col gap-1 text-xs text-muted-foreground">
              <Input
                className="h-7 w-full font-mono text-[12px]"
                placeholder={ID_TEMPLATE_EXAMPLE}
                spellCheck={false}
                value={modifier.text ?? ""}
                onChange={(event) => update(index, { text: event.target.value })}
                data-testid={`${testId}-id-${index}`}
              />
              <span>
                {"Tokens: {$value}, {<column>} (the row the entry reads, an item's row inside a repeated array), {$dataset.<column>} (the dataset's own row), {$cache.<Type>.<field>} (a lookup table row keyed by the value) and {$param.<name>}. "
                  + "Values are percent-encoded; a token with no value gives no value. The last modifier; a reference ends with ':'."}
              </span>
            </div>
          )}
          {modifier.kind === "ref" && (
            <div className="flex flex-col gap-1 text-xs text-muted-foreground">
              <Input
                className="h-7 w-full font-mono text-[12px]"
                placeholder={relationships.length === 1 ? relationships[0] : "UnitOfMeasure"}
                spellCheck={false}
                value={modifier.text ?? ""}
                onChange={(event) => update(index, { text: event.target.value === "" ? null : event.target.value })}
                data-testid={`${testId}-ref-${index}`}
              />
              <span>
                {(relationships.length > 0
                  ? `The reference to the record whose code is the value, in the flow's partition. ${refHint?.path ?? "The property"} points to ${relationships.join(" or ")}; `
                  : "The reference to the record whose code is the value, in the flow's partition. The template names no type for this property, so name one in full, such as reference-data--UnitOfMeasure; ")
                  + "leave the type empty when the property points to one, or name the entity (UnitOfMeasure) or the type in full. The last modifier."}
              </span>
            </div>
          )}
          {modifier.kind === "number" && (
            <div className="flex flex-wrap items-center gap-2 text-xs text-muted-foreground">
              <span>decimals after</span>
              <Select value={modifier.decimalSeparator ?? "."} onValueChange={(value) => update(index, { decimalSeparator: value })}>
                <SelectTrigger size="sm" className="h-7 w-28" data-testid={`${testId}-decimal-${index}`}>
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  {DECIMAL_SEPARATORS.map((option) => <SelectItem key={option.value} value={option.value}>{option.label}</SelectItem>)}
                </SelectContent>
              </Select>
              <span>digit groups split by</span>
              <Select
                value={modifier.groupSeparator ?? NO_GROUP}
                onValueChange={(value) => update(index, { groupSeparator: value === NO_GROUP ? null : value })}
              >
                <SelectTrigger size="sm" className="h-7 w-32" data-testid={`${testId}-group-${index}`}>
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  {GROUP_SEPARATORS.map((option) => <SelectItem key={option.value} value={option.value}>{option.label}</SelectItem>)}
                </SelectContent>
              </Select>
              {modifier.groupSeparator !== null && modifier.groupSeparator === (modifier.decimalSeparator ?? ".") && (
                <span className="text-destructive">the two separators must differ</span>
              )}
            </div>
          )}
        </div>
      ))}
      {children}
    </Section>
  );
}
