// The entry editor's form: one variable's entry, or one part of an entry in a nested form over it. It holds what its sections
// share (the input chosen, what each input reads, the settings the entry carries), builds the entry Save writes, carries what
// was read over when the input changes, and lays out the sections drawn beside it.

import { useId, useMemo, useState } from "react";
import { Button } from "@/components/ui/button";
import { Sheet, SheetContent, SheetFooter } from "@/components/ui/sheet";
import type {
  DeliveryCachedType, DeliveryTemplateVariable, MappingDraft, MappingDraftAssertion, MappingDraftEntry, MappingDraftInput, MappingDraftIssue,
  MappingDraftModifier,
} from "../../../api/delivery";
import type { EntryEditorTarget } from "../MappingEntryEditor";
import { assertionsOf, takesAssertions, type AssertionPart } from "../mappingAssertions";
import { bareColumn, emptyEntry, findLinesOf, findsOf, ID_MODIFIER_KINDS, knownColumns, newFindLine, parseJson, repeaterOf, type FindLine } from "../mappingDraft";
import { ACCESS_TARGETS, inputsOffered } from "../entryInputs";
import { appendName, itemSteps } from "../recordPath";
import { EntryFindBySection, FindAllSection } from "./EntryFindSections";
import { EntryInputSection, EntryKeySection, EntryNotes, EntryTitle } from "./EntryHeading";
import { EntryPartsSection, EntryPropertiesSection } from "./EntryParts";
import {
  CacheSection, ChildSection, DatasetSection, ExpressionSection, LookupSection, ParentSection, RepeatSection, SearchSection,
} from "./EntryReadSections";
import { DescriptionSection, EntryAssertions, EntryModifiersSection, RequiredSection, WhenSection } from "./EntrySettings";
import { StaticValueSection } from "./StaticValueSection";
import type { Choice, NestedRole } from "./entryFormModel";
import { useEntryParts } from "./useEntryParts";
import { useFindAll } from "./useFindAll";
import { useStaticValue } from "./useStaticValue";

interface EntryFormProps {
  target: EntryEditorTarget;
  draft: MappingDraft;
  /** The template's variables, of which an item of a list of objects fills those inside the list's items. */
  variables: DeliveryTemplateVariable[];
  /** The types of the cache the mapping reads, offered to a cache entry; empty when no cache is picked. */
  cacheTypes: DeliveryCachedType[];
  /** The kind the template is linked to as its parent, whose sections a parent input takes; null for a template linked to none. */
  parent: string | null;
  /** The kinds of the templates linked to this one, whose records a child input links; empty when none is. */
  childKinds: string[];
  issues: MappingDraftIssue[];
  onSave: (target: string, entry: MappingDraftEntry | null) => void;
  onClose: () => void;
  /**
   * Set when the form edits one part of an entry. An alternative of a coalesce reads one value of its own input, without
   * what only the whole entry decides (its condition, whether it is required, its description). An item of a list of
   * values reads one value, or many with a find all, and has a condition, a required flag and a description of its own.
   * An item of a list of objects is a group of properties or a fixed object, with no settings of its own. A property of
   * such an item is an entry of its own for the variable inside the list's items it fills.
   */
  nested?: NestedRole;
}

export function EntryForm({ target, draft, variables, cacheTypes, parent, childKinds, issues, onSave, onClose, nested }: EntryFormProps) {
  const ids = useId();
  const { variable, entry, keyHolder, outside } = target;
  const known = useMemo(() => knownColumns(draft), [draft]);
  const isAlternative = nested?.kind === "alternative";
  const isItem = nested?.kind === "item";
  const isProperty = nested?.kind === "property";
  const part: AssertionPart = nested?.kind ?? "entry";
  // An item of a list of objects is one object: a group of properties each filled on its own, or a fixed object.
  const objectItem = isItem && variable.shape === "GroupList";
  // Whether a node of this input can restrict the record when it gives no value ($restrictWhenMissing): one that reads a
  // value, as the entry itself or an item of a list of values; never a literal, a list or an object whole, a child link, a
  // repeat, an alternative of a coalesce (whose node decides for all of them) or an object item.
  const restrictable = (kind: Choice) => !isAlternative && !objectItem
    && kind !== "None" && kind !== "Static" && kind !== "List" && kind !== "Group" && kind !== "Child" && kind !== "Repeat";
  // An item of a list of values is one value of the list's items, so a fixed item is edited as one value of that type.
  const valueVariable: DeliveryTemplateVariable = objectItem
    ? { ...variable, shape: "Group", type: "object" }
    : isItem ? { ...variable, shape: "Value", type: variable.itemType ?? "string" } : variable;

  const searches = draft.searches;
  const lookups = draft.lookups;
  // What a repeater fills is its rows, so an entry inside its items reads no relative of the record.
  const repeatedHolder = repeaterOf(keyHolder !== null ? keyHolder.path : variable.path);
  const inRepeater = repeatedHolder !== null
    && draft.entries.some((candidate) => candidate.target === repeatedHolder && candidate.input === "Repeat");
  const allowed: MappingDraftInput[] = inputsOffered(
    variable,
    { nested: nested?.kind ?? null, outside: outside !== null, inRepeater },
    { searches: searches.length, lookups: lookups.length, parent: parent !== null, children: childKinds.length > 0 },
    entry?.input ?? null,
  );

  const initialStatic = entry?.input === "Static" ? entry.static : null;
  const initialAll = entry?.findAll ?? null;

  const [choice, setChoice] = useState<Choice>(entry?.input ?? (keyHolder !== null || (nested !== undefined && !isProperty) ? allowed[0] : "None"));
  const [keyName, setKeyName] = useState("");
  const [column, setColumn] = useState(entry?.column ?? "");
  const [child, setChild] = useState(entry?.child ?? "");
  const [cacheType, setCacheType] = useState(entry?.cacheType ?? variable.cacheTypes[0] ?? "");
  const [cacheField, setCacheField] = useState(entry?.input === "Lookup" ? "id" : entry?.cacheField ?? "id");
  const [findBy, setFindBy] = useState<FindLine[]>(() => findLinesOf(entry?.findBy ?? []));
  const [findMode, setFindMode] = useState<"one" | "all">(initialAll === null ? "one" : "all");
  const findAll = useFindAll(initialAll, lookups);
  const [lookupName, setLookupName] = useState(entry?.input === "Lookup" ? entry.lookup ?? "" : lookups[0]?.name ?? "");
  const [lookupField, setLookupField] = useState(entry?.input === "Lookup" ? entry.cacheField ?? "id" : "id");
  const [parentPath, setParentPath] = useState(entry?.parentPath ?? "");
  const [childKind, setChildKind] = useState(entry?.childKind ?? childKinds[0] ?? "");
  const [ignoreSeparators, setIgnoreSeparators] = useState(entry?.ignoreSeparators ?? false);
  const [modifiers, setModifiers] = useState<MappingDraftModifier[]>(entry?.modifiers ?? []);
  const [expression, setExpression] = useState(entry?.expression ?? "");
  const [conditionOn, setConditionOn] = useState(entry !== null && entry.when !== null);
  const [condition, setCondition] = useState(entry?.when ?? "");
  const [whereOn, setWhereOn] = useState(entry !== null && entry.where !== null);
  const [where, setWhere] = useState(entry?.where ?? "");
  const [required, setRequired] = useState(entry?.required ?? true);
  // $restrictWhenMissing: a missing value sends the record restricted under the mapping's restrictedAccess instead of holding it.
  const [restrict, setRestrict] = useState(entry?.restrictWhenMissing ?? false);
  const [description, setDescription] = useState(entry?.description ?? "");
  const [unverified, setUnverified] = useState(entry?.unverified ?? false);
  const {
    alternatives, setAlternatives, items, setItems, properties, setProperties, editing, openPart, openProperty, savePart, closePart,
  } = useEntryParts(entry, variables);
  const [assertions, setAssertions] = useState<MappingDraftAssertion[]>(() => assertionsOf(entry));
  const staticValue = useStaticValue(valueVariable, initialStatic, choice);

  const staticResult = staticValue.result;
  const newTarget = keyHolder !== null ? appendName(keyHolder.path, keyName.trim()) : variable.path;
  // Inside the items of an array a child dataset's rows fill, a column reads the item's row; the items of a list of objects
  // read the dataset row, as the record's own properties do.
  const repeatedIn = repeaterOf(newTarget);
  const repeat = repeatedIn === null ? undefined : draft.entries.find((candidate) => candidate.target === repeatedIn && candidate.input === "Repeat");
  const listed = repeatedIn !== null && repeat === undefined
    && draft.entries.some((candidate) => candidate.target === repeatedIn && candidate.input === "List");
  const repeater = isProperty || listed ? null : repeatedIn;
  const repeaterChild = repeat?.child ?? null;
  const repoTypes = cacheTypes;
  const resolves = choice === "Cache" || choice === "Search";
  const findsAll = choice === "Cache" && findMode === "all";
  // A section of the parent is taken whole, so modifiers change only a single value it holds.
  const parentValue = valueVariable.shape === "Value";
  // A find all's modifiers change the column it compares, so one comparing a fixed text or a lookup's field takes none.
  const modified = choice === "Dataset" || choice === "Expression" || (resolves && !(findsAll && findAll.mode !== "column"))
    || (choice === "Parent" && parentValue);
  const buildsId = modifiers.some((modifier) => ID_MODIFIER_KINDS.includes(modifier.kind));
  const access = ACCESS_TARGETS.includes(newTarget);
  const objectList = choice === "List" && variable.shape === "GroupList";

  let keyError: string | null = null;
  if (keyHolder !== null) {
    const name = keyName.trim();
    if (name === "") {
      keyError = "Name the key.";
    } else if (draft.entries.some((candidate) => candidate.target === newTarget)) {
      keyError = `${newTarget} already has an entry.`;
    }
  }

  const groupError = choice === "Group" && properties.length === 0 ? "Fill at least one property of the item." : null;
  const error = keyError ?? groupError ?? staticResult.error;
  const columnsList = `${ids}-columns`;
  const childrenList = `${ids}-children`;
  const columnPlaceholder = repeater === null ? "column" : `${repeaterChild ?? "child"}.column`;

  /**
   * The entry the form holds for an input, as Save writes it. An alternative keeps nothing only the whole entry decides, a
   * coalesce entry carries its alternatives and a list its items under its own target, a group its properties under
   * theirs, and a list, a group and an item of a list of objects have no settings of their own, since each is written as
   * what it holds alone. Assertions go only on a node with a value of its own to judge: never on a fixed value, a list, a
   * group, an alternative or an item, so the alternatives and items it carries hold none.
   */
  const build = (kind: MappingDraftInput): MappingDraftEntry => {
    const looks = kind === "Cache" || kind === "Search";
    const all = kind === "Cache" && findMode === "all";
    const takesModifiers = kind === "Dataset" || kind === "Expression" || (looks && !(all && findAll.mode !== "column"))
      || (kind === "Parent" && parentValue);
    const own = !isAlternative && !objectItem && kind !== "List" && kind !== "Group";
    return {
      ...(entry ?? emptyEntry(newTarget, kind)),
      target: newTarget,
      input: kind,
      column: kind === "Dataset" ? bareColumn(column) : null,
      child: kind === "Repeat" ? bareColumn(child) : null,
      cacheType: looks ? cacheType.trim() : null,
      cacheField: kind === "Cache" ? cacheField.trim() : kind === "Search" ? "id" : kind === "Lookup" ? lookupField.trim() : null,
      lookup: kind === "Lookup" ? lookupName.trim() : null,
      parentPath: kind === "Parent" ? parentPath.trim() : null,
      childKind: kind === "Child" ? childKind.trim() : null,
      findBy: looks && !all ? findsOf(findBy) : [],
      findAll: all ? findAll.build() : null,
      modifiers: takesModifiers
        ? modifiers.map((modifier) => (modifier.kind === "date" && (modifier.text ?? "").trim() === "" ? { ...modifier, text: null } : modifier))
        : [],
      expression: kind === "Expression" ? expression.trim() : null,
      when: own && conditionOn && condition.trim() !== "" ? condition.trim() : null,
      where: kind === "Repeat" && whereOn && where.trim() !== "" ? where.trim() : null,
      // A child link never holds its record, so it is never required.
      required: kind === "Child" ? false : kind === "Static" || kind === "List" || kind === "Group" || isAlternative || objectItem ? true : required,
      // Only a required node that reads a value restricts the record when it has none: never a literal, a list or an object as a
      // whole, a child link, a repeat, an alternative (the coalesce decides for all of them) or an object item.
      restrictWhenMissing: restrictable(kind) && required && restrict,
      ignoreSeparators: kind === "Cache" && !all && ignoreSeparators,
      unverified: (kind === "Dataset" || kind === "Expression") && buildsId && unverified,
      alternatives: kind === "Coalesce" ? alternatives.map((alternative) => ({ ...alternative, target: newTarget, restrictWhenMissing: false, assertions: [] })) : [],
      items: kind === "List" ? items.map((item) => ({ ...item, target: newTarget, assertions: [] })) : [],
      properties: kind === "Group" ? properties : [],
      static: kind === "Static" ? staticResult.json : null,
      description: own && description.trim() !== "" ? description.trim() : null,
      assertions: takesAssertions(kind, part) ? assertions : [],
      prefilled: false,
    };
  };

  const choose = (next: Choice) => {
    const previous = choice;
    const reads = previous !== "None" && previous !== "Repeat" && previous !== "Coalesce" && previous !== "List" && previous !== "Group";
    setChoice(next);
    if (next === "Coalesce") {
      // What the form already reads becomes the first alternative, so turning an entry into a coalesce keeps it; its
      // assertions stay the entry's, which judge whichever alternative gives the value.
      if (alternatives.length === 0 && reads) {
        setAlternatives([{ ...build(previous), when: null, required: true, restrictWhenMissing: false, description: null, assertions: [] }]);
      }

      return;
    }

    if (next === "List") {
      // What the form already holds becomes the list's first items: each value of a static list an item of its own, and
      // any other value one item, so turning an entry into a list of values keeps it. An item takes no assertions, since
      // it gives one value of the list.
      if (items.length === 0 && reads) {
        const parsed = previous === "Static" ? parseJson(staticResult.json) : null;
        if (parsed !== null && parsed.ok && Array.isArray(parsed.value)) {
          setItems(parsed.value.map((value: unknown) => ({ ...emptyEntry(newTarget, "Static"), static: JSON.stringify(value) })));
        } else {
          setItems([{ ...build(previous), assertions: [] }]);
        }
      }

      return;
    }

    if (next === "Parent") {
      // A custom template is a copy of its parent, so the section most often taken is the one at the variable's own path.
      // A $parent path names plain properties, so a target holding a name in brackets offers none.
      if (parentPath.trim() === "" && newTarget.startsWith("osdu.") && itemSteps(newTarget) === 0 && !newTarget.includes("[")) {
        setParentPath(newTarget.slice("osdu.".length));
      }

      return;
    }

    if (next === "Child") {
      if (childKind.trim() === "") {
        setChildKind(childKinds[0] ?? "");
      }

      return;
    }

    if (next === "Lookup") {
      if (!lookups.some((lookup) => lookup.name === lookupName.trim())) {
        setLookupName(lookups[0]?.name ?? "");
      }

      return;
    }

    if (next === "Search") {
      // A search reads the id of the record it finds, in one of the searches the mapping declares. The lines of a cache
      // lookup name cached fields, not the properties a search compares, so they do not carry over.
      if (!searches.some((search) => search.name === cacheType.trim())) {
        setCacheType(searches[0]?.name ?? "");
      }

      setCacheField("id");
      if (findBy.length === 0 || previous === "Cache") {
        setFindBy([newFindLine("")]);
      }

      return;
    }

    if (next !== "Cache") {
      return;
    }

    const type = cacheType.trim() === "" || previous === "Search" ? variable.cacheTypes[0] ?? "" : cacheType;
    if (type !== cacheType) {
      setCacheType(type);
    }

    if (findBy.length === 0 || previous === "Search") {
      const fields = repoTypes.find((candidate) => candidate.name === type)?.fields ?? [];
      setFindBy([newFindLine(fields[0] ?? "id")]);
    }
  };

  const save = () => {
    if (error !== null) {
      return;
    }

    if (choice === "None") {
      onSave(variable.path, null);
      return;
    }

    onSave(newTarget, build(choice));
  };

  return (
    <>
      <EntryTitle variable={variable} valueVariable={valueVariable} keyHolder={keyHolder} nested={nested} />
      <div className="flex flex-1 flex-col gap-5 overflow-y-auto px-4 pb-4">
        <datalist id={columnsList}>
          {known.columns.map((name) => <option key={name} value={name} />)}
        </datalist>
        <datalist id={childrenList}>
          {known.children.map((name) => <option key={name} value={name} />)}
        </datalist>
        <EntryNotes variable={variable} nested={nested} outside={outside} issues={issues} />

        {keyHolder !== null && <EntryKeySection keyHolder={keyHolder} keyName={keyName} onKeyName={setKeyName} newTarget={newTarget} />}

        <EntryInputSection
          choice={choice}
          onChoose={choose}
          allowed={allowed}
          variable={variable}
          offersNone={keyHolder === null && (nested === undefined || isProperty)}
          isProperty={isProperty}
        />

        {(choice === "Coalesce" || choice === "List") && (
          <EntryPartsSection
            choice={choice}
            alternatives={alternatives}
            setAlternatives={setAlternatives}
            items={items}
            setItems={setItems}
            objectList={objectList}
            access={access}
            newTarget={newTarget}
            onOpen={openPart}
          />
        )}

        {choice === "Group" && (
          <EntryPropertiesSection
            variable={variable}
            variables={variables}
            objectItem={objectItem}
            properties={properties}
            setProperties={setProperties}
            onOpen={openProperty}
          />
        )}

        {choice === "Dataset" && (
          <DatasetSection repeater={repeater} repeaterChild={repeaterChild} columnsList={columnsList} column={column} onColumn={setColumn} />
        )}

        {choice === "Expression" && (
          <ExpressionSection repeater={repeater} repeaterChild={repeaterChild} expression={expression} onExpression={setExpression} />
        )}

        {choice === "Repeat" && (
          <RepeatSection
            variable={variable}
            childrenList={childrenList}
            child={child}
            onChild={setChild}
            whereOn={whereOn}
            onWhereOn={setWhereOn}
            where={where}
            onWhere={setWhere}
          />
        )}

        {choice === "Lookup" && (
          <LookupSection
            lookups={lookups}
            repoTypes={repoTypes}
            lookupName={lookupName}
            onLookupName={setLookupName}
            lookupField={lookupField}
            onLookupField={setLookupField}
          />
        )}

        {choice === "Cache" && (
          <CacheSection
            variable={variable}
            repoTypes={repoTypes}
            cacheType={cacheType}
            onCacheType={setCacheType}
            cacheField={cacheField}
            onCacheField={setCacheField}
            findMode={findMode}
            onFindMode={setFindMode}
            findsAll={findsAll}
          />
        )}

        {choice === "Parent" && <ParentSection parent={parent} variables={variables} parentPath={parentPath} onParentPath={setParentPath} />}

        {choice === "Child" && <ChildSection childKinds={childKinds} childKind={childKind} onChildKind={setChildKind} />}

        {choice === "Search" && <SearchSection searches={searches} cacheType={cacheType} onCacheType={setCacheType} />}

        {resolves && !findsAll && (
          <EntryFindBySection
            choice={choice}
            draft={draft}
            repoTypes={repoTypes}
            cacheType={cacheType}
            findBy={findBy}
            onFindBy={setFindBy}
            columnsList={columnsList}
            columnPlaceholder={columnPlaceholder}
            ignoreSeparators={ignoreSeparators}
            onIgnoreSeparators={setIgnoreSeparators}
          />
        )}

        {findsAll && (
          <FindAllSection
            findAll={findAll}
            lookups={lookups}
            repoTypes={repoTypes}
            cacheType={cacheType}
            columnsList={columnsList}
            columnPlaceholder={columnPlaceholder}
          />
        )}

        {choice === "Static" && <StaticValueSection staticValue={staticValue} valueVariable={valueVariable} />}

        {modified && (
          <EntryModifiersSection
            choice={choice}
            findsAll={findsAll}
            modifiers={modifiers}
            onModifiers={setModifiers}
            repoTypes={repoTypes}
            variable={variable}
            buildsId={buildsId}
            unverified={unverified}
            onUnverified={setUnverified}
          />
        )}

        {choice !== "None" && choice !== "List" && choice !== "Group" && !isAlternative && !objectItem && (
          <WhenSection
            choice={choice}
            isItem={isItem}
            conditionOn={conditionOn}
            onConditionOn={setConditionOn}
            condition={condition}
            onCondition={setCondition}
          />
        )}

        {choice !== "None" && choice !== "Static" && choice !== "List" && choice !== "Group" && choice !== "Child" && !isAlternative && !objectItem && (
          <RequiredSection
            choice={choice}
            variable={variable}
            isItem={isItem}
            required={required}
            onRequired={setRequired}
            restrictable={restrictable(choice)}
            restrict={restrict}
            onRestrict={setRestrict}
          />
        )}

        <EntryAssertions
          choice={choice}
          part={part}
          assertions={assertions}
          onAssertions={setAssertions}
          newTarget={newTarget}
          required={required}
          variable={variable}
          keyHolder={keyHolder}
          findsAll={findsAll}
          variables={variables}
          columns={known.columns}
          columnPlaceholder={columnPlaceholder}
        />

        {choice !== "None" && choice !== "List" && choice !== "Group" && !isAlternative && !objectItem && (
          <DescriptionSection description={description} onDescription={setDescription} />
        )}
      </div>
      <SheetFooter className="flex-row flex-wrap items-center justify-end gap-2 border-t border-border">
        {error !== null && (
          <p className="mr-auto text-xs font-medium text-destructive" data-testid="mapping-builder-entry-error">{error}</p>
        )}
        <Button variant="ghost" size="sm" onClick={onClose} data-testid="mapping-builder-entry-cancel">Cancel</Button>
        <Button size="sm" onClick={save} disabled={error !== null} data-testid="mapping-builder-entry-save">
          {isAlternative
            ? "Save alternative"
            : objectItem
              ? "Save item"
              : isItem
                ? "Save value"
                : isProperty
                  ? choice === "None" ? "Leave the property out" : "Save property"
                  : choice === "None" && entry !== null ? "Remove entry" : "Save entry"}
        </Button>
      </SheetFooter>
      {/*
        One alternative of the coalesce, one item of the list, or one property of the item, edited as an input of its own
        over the entry's form.
      */}
      <Sheet open={editing !== null} onOpenChange={(open) => { if (!open) { closePart(); } }}>
        <SheetContent
          className="w-full gap-0 sm:max-w-2xl"
          data-testid={editing?.list === "items"
            ? "mapping-builder-item-editor"
            : editing?.list === "properties" ? "mapping-builder-property-editor" : "mapping-builder-alternative-editor"}
        >
          {editing !== null && (
            <EntryForm
              key={editing.session}
              target={{ variable: editing.variable ?? variable, entry: editing.entry, keyHolder: null, outside: null, session: editing.session }}
              draft={draft}
              variables={variables}
              cacheTypes={cacheTypes}
              parent={parent}
              childKinds={childKinds}
              issues={[]}
              nested={editing.list === "items"
                ? { kind: "item", label: `item ${editing.index + 1}` }
                : editing.list === "properties"
                  ? { kind: "property", label: nested?.label ?? "the item" }
                  : { kind: "alternative", label: `alternative ${editing.index + 1}` }}
              onSave={(path, value) => savePart(editing, path, value)}
              onClose={closePart}
            />
          )}
        </SheetContent>
      </Sheet>
    </>
  );
}
