import { useId, useMemo, useState } from "react";
import { ArrowDown, ArrowUp, CircleAlert, OctagonAlert, Pencil, Plus, TriangleAlert, X } from "lucide-react";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Sheet, SheetContent, SheetDescription, SheetFooter, SheetHeader, SheetTitle } from "@/components/ui/sheet";
import { Switch } from "@/components/ui/switch";
import { Textarea } from "@/components/ui/textarea";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import { cn } from "@/lib/utils";
import type {
  DeliveryCachedType, DeliveryTemplateVariable, MappingDraft, MappingDraftAssertion, MappingDraftEntry, MappingDraftFindAll, MappingDraftInput,
  MappingDraftIssue, MappingDraftModifier,
} from "../../api/delivery";
import { MappingAssertionsSection } from "./MappingAssertionEditor";
import { ChoiceOrText, FindByLinesEditor, IconAction, ModifierListEditor, Section, type ChoiceOption } from "./MappingEditorParts";
import { assertionsOf, noAssertionsReason, recordFieldOf, takesAssertions, type AssertionPart } from "./mappingAssertions";
import {
  ACCESS_TARGETS, alternativeText, bareColumn, emptyEntry, entryText, findLinesOf, findsOf, ID_MODIFIER_KINDS, inputsFor, KEY_NAME, knownColumns,
  MODIFIER_KINDS, moveItem, parseJson, putEntry, repeaterOf, staticModeFor, withinItem, type FindLine, type StaticMode,
} from "./mappingDraft";
import { shapeText } from "./templateFormat";

/** What the entry editor edits: the variable, its entry, and the holder when the target is a new key of an object with free keys. */
export interface EntryEditorTarget {
  variable: DeliveryTemplateVariable;
  entry: MappingDraftEntry | null;
  /** Set for a new key under an object with free keys (tags): the editor asks for the key's name. */
  keyHolder: DeliveryTemplateVariable | null;
  /** Why no mapping may fill the target, when the template does not let one. */
  outside: string | null;
  /** A number the page gives each opening, so every opening starts from the draft as it is. */
  session: number;
}

type Choice = "None" | MappingDraftInput;

const CHOICE_LABELS: Record<Choice, string> = {
  None: "Not filled",
  Dataset: "Dataset column",
  Repeat: "Repeat child rows",
  Cache: "Cache",
  Lookup: "Lookup",
  Static: "Static value",
  Search: "Platform search",
  Expression: "Expression",
  Coalesce: "First value of",
  List: "List of values",
  Group: "Object",
  Parent: "Parent record",
  Child: "Child record",
};

/** The inputs one item of a list of values may read: one value each, or with a find all many. */
const ITEM_INPUTS: MappingDraftInput[] = ["Static", "Dataset", "Expression", "Cache", "Lookup", "Search", "Child"];

/** The inputs one item of a list of objects may be: an object whose properties are each filled on their own, or a fixed object. */
const OBJECT_ITEM_INPUTS: MappingDraftInput[] = ["Group", "Static"];

/** How the input choice names an input for a variable: a list filling a list of objects is a list of objects. */
function choiceLabel(input: Choice, variable: Pick<DeliveryTemplateVariable, "shape">): string {
  return input === "List" && variable.shape === "GroupList" ? "List of objects" : CHOICE_LABELS[input];
}

/** What a find all compares its field with: a dataset column, a fixed text, or a field of a lookup's record. */
type FindAllMode = "column" | "text" | "lookup";

/** The static value in every editor at once, so switching between them keeps what was typed. */
interface StaticState {
  list: string[];
  text: string;
  number: string;
  boolean: "true" | "false";
  json: string;
}

function defaultJson(variable: DeliveryTemplateVariable): string {
  if (variable.shape === "GroupList" || variable.shape === "ValueList" || variable.type === "array") {
    return "[]";
  }

  return variable.shape === "Group" || variable.shape === "Whole" ? "{}" : "\"\"";
}

function seedStatic(variable: DeliveryTemplateVariable, json: string | null): StaticState {
  const parsed = parseJson(json);
  const value = parsed.ok ? parsed.value : undefined;
  return {
    list: Array.isArray(value) ? value.map((item: unknown) => (item === null || item === undefined ? "" : String(item))) : [],
    text: typeof value === "string" ? value : "",
    number: typeof value === "number" ? String(value) : "",
    boolean: value === true ? "true" : "false",
    json: json !== null && json.trim() !== "" ? json : defaultJson(variable),
  };
}

/** The static value as JSON text from the editor in use, or why it cannot be; an empty number is no value yet. */
function staticJsonOf(mode: StaticMode, state: StaticState, variable: DeliveryTemplateVariable): { json: string | null; error: string | null } {
  switch (mode) {
    case "list":
      return { json: JSON.stringify(state.list.map((item) => item.trim()).filter((item) => item !== "")), error: null };
    case "text":
      return { json: JSON.stringify(state.text), error: null };
    case "number": {
      const text = state.number.trim();
      if (text === "") {
        return { json: null, error: null };
      }

      const number = Number(text);
      if (!Number.isFinite(number)) {
        return { json: null, error: `'${text}' is not a number.` };
      }

      if (variable.type === "integer" && !Number.isInteger(number)) {
        return { json: null, error: `'${text}' is not a whole number, and the template takes an integer.` };
      }

      return { json: JSON.stringify(number), error: null };
    }
    case "boolean":
      return { json: state.boolean, error: null };
    case "json": {
      const parsed = parseJson(state.json);
      if (!parsed.ok) {
        return { json: null, error: `The static value is not valid JSON: ${parsed.error}` };
      }

      return { json: parsed.value === undefined ? null : state.json.trim(), error: null };
    }
  }
}

/**
 * How a nested form edits one part of an entry: an alternative of a coalesce, an item of a list, or a property of an item
 * of a list of objects.
 */
interface NestedRole {
  kind: "alternative" | "item" | "property";
  /** How the form's title names the part: "alternative 2", "item 3". */
  label: string;
}

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

/**
 * The part the nested editor edits: which list, its place there (the end for a new one), for a property the variable
 * inside the list's items it fills, and a number per opening.
 */
interface EditedPart {
  list: "alternatives" | "items" | "properties";
  index: number;
  entry: MappingDraftEntry | null;
  variable: DeliveryTemplateVariable | null;
  session: number;
}

function EntryForm({ target, draft, variables, cacheTypes, parent, childKinds, issues, onSave, onClose, nested }: EntryFormProps) {
  const ids = useId();
  const { variable, entry, keyHolder, outside } = target;
  const known = useMemo(() => knownColumns(draft), [draft]);
  const variableOrder = useMemo(() => new Map(variables.map((candidate, index) => [candidate.path, index])), [variables]);
  const isAlternative = nested?.kind === "alternative";
  const isItem = nested?.kind === "item";
  const isProperty = nested?.kind === "property";
  const part: AssertionPart = nested?.kind ?? "entry";
  // The record paths a condition of an assertion may read: those the template has that a mapping or the engine writes.
  const recordFields = useMemo(
    () => [...new Set(variables.filter((candidate) => candidate.role !== "Osdu").map((candidate) => recordFieldOf(candidate.path)))],
    [variables],
  );
  // An item of a list of objects is one object: a group of properties each filled on its own, or a fixed object.
  const objectItem = isItem && variable.shape === "GroupList";
  // An item of a list of values is one value of the list's items, so a fixed item is edited as one value of that type.
  const valueVariable: DeliveryTemplateVariable = objectItem
    ? { ...variable, shape: "Group", type: "object" }
    : isItem ? { ...variable, shape: "Value", type: variable.itemType ?? "string" } : variable;

  const searches = draft.searches;
  const lookups = draft.lookups;
  // A property of an item of a list of objects is filled as its variable is anywhere, except that the item repeats no rows
  // and a list of objects inside it is written whole.
  const offered: MappingDraftInput[] = outside !== null
    ? ["Dataset", "Expression", "Repeat", "Cache", "Lookup", "Search", "Static"]
    : objectItem
      ? OBJECT_ITEM_INPUTS
      : isItem
        ? ITEM_INPUTS
        : isProperty
          ? inputsFor(variable).filter((input) => input !== "Repeat" && !(input === "List" && variable.shape === "GroupList"))
          : inputsFor(variable);
  // A search is offered once the mapping declares one to look in, a lookup once it declares one to read, the parent record
  // once the template is linked to a parent, and a child record once a template is linked to this one, both outside the
  // items a child dataset's rows fill, which have neither. A child link is never an alternative: nothing stands in for it.
  // An alternative or an item reads values of its own, so it is neither a repeat, a coalesce nor a list.
  const repeatedHolder = repeaterOf(keyHolder !== null ? keyHolder.path : variable.path);
  const inRepeater = repeatedHolder !== null
    && draft.entries.some((candidate) => candidate.target === repeatedHolder && candidate.input === "Repeat");
  const allowed = offered.filter((input) => (input !== "Search" || searches.length > 0)
    && (input !== "Lookup" || lookups.length > 0)
    && (input !== "Parent" || (parent !== null && !inRepeater && !isItem))
    && (input !== "Child" || (childKinds.length > 0 && !inRepeater && !isAlternative))
    && (nested === undefined || isProperty || (input !== "Repeat" && input !== "Coalesce" && input !== "List")));
  if (entry !== null && !allowed.includes(entry.input)) {
    allowed.push(entry.input);
  }

  const typedMode = staticModeFor(valueVariable, null);
  const initialStatic = entry?.input === "Static" ? entry.static : null;
  const initialAll = entry?.findAll ?? null;
  const initialAllLookup = (initialAll?.lookup ?? "").trim();

  const [choice, setChoice] = useState<Choice>(entry?.input ?? (keyHolder !== null || (nested !== undefined && !isProperty) ? allowed[0] : "None"));
  const [keyName, setKeyName] = useState("");
  const [column, setColumn] = useState(entry?.column ?? "");
  const [child, setChild] = useState(entry?.child ?? "");
  const [cacheType, setCacheType] = useState(entry?.cacheType ?? variable.cacheTypes[0] ?? "");
  const [cacheField, setCacheField] = useState(entry?.input === "Lookup" ? "id" : entry?.cacheField ?? "id");
  const [findBy, setFindBy] = useState<FindLine[]>(() => findLinesOf(entry?.findBy ?? []));
  const [findMode, setFindMode] = useState<"one" | "all">(initialAll === null ? "one" : "all");
  const [allField, setAllField] = useState(initialAll?.field ?? "");
  const [allMode, setAllMode] = useState<FindAllMode>(initialAll === null
    ? (lookups.length > 0 ? "lookup" : "column")
    : initialAllLookup !== "" ? "lookup" : (initialAll.literal ?? "") !== "" ? "text" : "column");
  const [allValue, setAllValue] = useState(initialAll === null ? "" : (initialAll.literal ?? "") !== "" ? initialAll.literal ?? "" : initialAll.column ?? "");
  const [allLookup, setAllLookup] = useState(initialAllLookup === "" ? lookups[0]?.name ?? "" : initialAllLookup.split(".")[0]);
  const [allPath, setAllPath] = useState(initialAllLookup.includes(".") ? initialAllLookup.slice(initialAllLookup.indexOf(".") + 1) : "");
  const [allEmpty, setAllEmpty] = useState<string[]>(initialAll?.empty ?? []);
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
  const [description, setDescription] = useState(entry?.description ?? "");
  const [unverified, setUnverified] = useState(entry?.unverified ?? false);
  const [alternatives, setAlternatives] = useState<MappingDraftEntry[]>(entry?.alternatives ?? []);
  const [items, setItems] = useState<MappingDraftEntry[]>(entry?.items ?? []);
  const [properties, setProperties] = useState<MappingDraftEntry[]>(entry?.properties ?? []);
  const [assertions, setAssertions] = useState<MappingDraftAssertion[]>(() => assertionsOf(entry));
  const [editing, setEditing] = useState<EditedPart | null>(null);
  const [openings, setOpenings] = useState(0);
  const [jsonMode, setJsonMode] = useState(() => typedMode === "json" || staticModeFor(valueVariable, initialStatic) === "json");
  const [staticState, setStaticState] = useState<StaticState>(() => seedStatic(valueVariable, initialStatic));

  const mode: StaticMode = jsonMode ? "json" : typedMode;
  const staticResult = choice === "Static" ? staticJsonOf(mode, staticState, valueVariable) : { json: null, error: null };
  const newTarget = keyHolder !== null ? `${keyHolder.path}.${keyName.trim()}` : variable.path;
  // Inside the items of an array a child dataset's rows fill, a column reads the item's row; the items of a list of objects
  // read the dataset row, as the record's own properties do.
  const repeatedIn = repeaterOf(newTarget);
  const repeat = repeatedIn === null ? undefined : draft.entries.find((candidate) => candidate.target === repeatedIn && candidate.input === "Repeat");
  const listed = repeatedIn !== null && repeat === undefined
    && draft.entries.some((candidate) => candidate.target === repeatedIn && candidate.input === "List");
  const repeater = isProperty || listed ? null : repeatedIn;
  const repeaterChild = repeat?.child ?? null;
  const repoTypes = cacheTypes;
  const typeFields = repoTypes.find((type) => type.name === cacheType.trim())?.fields ?? [];
  const fieldOptions: ChoiceOption[] = [...new Set([...typeFields, "id"])].map((field) => ({ value: field, group: "Cached fields" }));
  const typeOptions: ChoiceOption[] = [
    ...variable.cacheTypes.map((name) => ({
      value: name,
      hint: repoTypes.find((type) => type.name === name)?.entityType,
      group: "Types this variable can be read from",
    })),
    ...repoTypes.filter((type) => !variable.cacheTypes.includes(type.name)).map((type) => ({
      value: type.name,
      hint: type.entityType,
      group: "Other cached types",
    })),
  ];
  const searchOptions: ChoiceOption[] = searches.map((search) => ({ value: search.name, hint: search.kind, group: "The mapping's searches" }));
  const lookupOptions: ChoiceOption[] = lookups.map((lookup) => ({ value: lookup.name, hint: lookup.cacheType, group: "The mapping's lookups" }));
  // The fields of the record a lookup finds: those its cached type captures, and its id.
  const lookupFields = (name: string): ChoiceOption[] => {
    const type = lookups.find((lookup) => lookup.name === name.trim())?.cacheType ?? "";
    const fields = repoTypes.find((candidate) => candidate.name === type)?.fields ?? [];
    return [...new Set(["id", ...fields])].map((field) => ({ value: field, group: `Fields of ${type === "" ? "its record" : type}` }));
  };
  // The properties other entries already compare in the same search, which is where a new line most often looks.
  const searchedFields: ChoiceOption[] = [...new Set(draft.entries
    .filter((candidate) => candidate.input === "Search" && candidate.cacheType === cacheType.trim())
    .flatMap((candidate) => candidate.findBy.map((find) => find.field))
    .filter((field) => field !== ""))]
    .map((field) => ({ value: field, group: "Compared by this search's entries" }));
  const resolves = choice === "Cache" || choice === "Search";
  const findsAll = choice === "Cache" && findMode === "all";
  // A section of the parent is taken whole, so modifiers change only a single value it holds.
  const parentValue = valueVariable.shape === "Value";
  // A find all's modifiers change the column it compares, so one comparing a fixed text or a lookup's field takes none.
  const modified = choice === "Dataset" || choice === "Expression" || (resolves && !(findsAll && allMode !== "column"))
    || (choice === "Parent" && parentValue);
  // The paths of the template, which a custom template copied from its parent shares, offered as the section to take.
  const parentOptions: ChoiceOption[] = variables
    .filter((candidate) => candidate.role === "Mapping" && candidate.path.startsWith("osdu.data.") && !candidate.path.includes("[]"))
    .map((candidate) => ({ value: candidate.path.slice("osdu.".length), group: "Paths of the template, as its parent's copy holds them" }));
  const childOptions: ChoiceOption[] = childKinds.map((kind) => ({ value: kind, group: "Templates linked to this one" }));
  const buildsId = modifiers.some((modifier) => ID_MODIFIER_KINDS.includes(modifier.kind));
  const access = ACCESS_TARGETS.includes(newTarget);
  const objectList = choice === "List" && variable.shape === "GroupList";

  // The variables an item of this list of objects holds, in template order, each a property the item may fill. A list of
  // objects inside the items is left out, since an array inside the items of another is not supported.
  const itemPrefix = `${variable.path}[].`;
  const itemVariables = objectItem
    ? variables.filter((candidate) => candidate.path.startsWith(itemPrefix) && candidate.role === "Mapping" && !candidate.nested
      && !candidate.path.slice(itemPrefix.length).includes("[]"))
    : [];
  const strayProperties = properties.filter((property) => !itemVariables.some((candidate) => candidate.path === property.target));

  let keyError: string | null = null;
  if (keyHolder !== null) {
    const name = keyName.trim();
    if (name === "") {
      keyError = "Name the key.";
    } else if (!KEY_NAME.test(name)) {
      keyError = "A key name holds no dots, brackets or spaces.";
    } else if (draft.entries.some((candidate) => candidate.target === newTarget)) {
      keyError = `${newTarget} already has an entry.`;
    }
  }

  const groupError = choice === "Group" && properties.length === 0 ? "Fill at least one property of the item." : null;
  const error = keyError ?? groupError ?? staticResult.error;
  const columnsList = `${ids}-columns`;
  const childrenList = `${ids}-children`;
  const columnPlaceholder = repeater === null ? "column" : `${repeaterChild ?? "child"}.column`;

  const findAllOf = (): MappingDraftFindAll => ({
    field: allField.trim(),
    column: allMode === "column" ? bareColumn(allValue) : null,
    literal: allMode === "text" ? allValue : null,
    lookup: allMode === "lookup" ? `${allLookup.trim()}.${allPath.trim()}` : null,
    empty: allEmpty.map((field) => field.trim()).filter((field) => field !== ""),
  });

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
    const takesModifiers = kind === "Dataset" || kind === "Expression" || (looks && !(all && allMode !== "column"))
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
      findAll: all ? findAllOf() : null,
      modifiers: takesModifiers
        ? modifiers.map((modifier) => (modifier.kind === "date" && (modifier.text ?? "").trim() === "" ? { ...modifier, text: null } : modifier))
        : [],
      expression: kind === "Expression" ? expression.trim() : null,
      when: own && conditionOn && condition.trim() !== "" ? condition.trim() : null,
      where: kind === "Repeat" && whereOn && where.trim() !== "" ? where.trim() : null,
      // A child link never holds its record, so it is never required.
      required: kind === "Child" ? false : kind === "Static" || kind === "List" || kind === "Group" || isAlternative || objectItem ? true : required,
      ignoreSeparators: kind === "Cache" && !all && ignoreSeparators,
      unverified: (kind === "Dataset" || kind === "Expression") && buildsId && unverified,
      alternatives: kind === "Coalesce" ? alternatives.map((alternative) => ({ ...alternative, target: newTarget, assertions: [] })) : [],
      items: kind === "List" ? items.map((item) => ({ ...item, target: newTarget, assertions: [] })) : [],
      properties: kind === "Group" ? properties : [],
      static: kind === "Static" ? staticResult.json : null,
      description: own && description.trim() !== "" ? description.trim() : null,
      assertions: takesAssertions(kind, part) ? assertions : [],
      prefilled: false,
    };
  };

  const openPart = (list: "alternatives" | "items", index: number, part: MappingDraftEntry | null) => {
    const session = openings + 1;
    setOpenings(session);
    setEditing({ list, index, entry: part, variable: null, session });
  };

  // A property of the item is edited as an entry of its own for the variable inside the list's items it fills.
  const openProperty = (property: DeliveryTemplateVariable, part: MappingDraftEntry | null) => {
    const session = openings + 1;
    setOpenings(session);
    setEditing({ list: "properties", index: -1, entry: part, variable: property, session });
  };

  const choose = (next: Choice) => {
    const previous = choice;
    const reads = previous !== "None" && previous !== "Repeat" && previous !== "Coalesce" && previous !== "List" && previous !== "Group";
    setChoice(next);
    if (next === "Coalesce") {
      // What the form already reads becomes the first alternative, so turning an entry into a coalesce keeps it; its
      // assertions stay the entry's, which judge whichever alternative gives the value.
      if (alternatives.length === 0 && reads) {
        setAlternatives([{ ...build(previous), when: null, required: true, description: null, assertions: [] }]);
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
      if (parentPath.trim() === "" && newTarget.startsWith("osdu.") && !newTarget.includes("[]")) {
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
        setFindBy([{ field: "", mode: "column", value: "" }]);
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
      setFindBy([{ field: fields[0] ?? "id", mode: "column", value: "" }]);
    }
  };

  const toggleJson = (on: boolean) => {
    if (on) {
      const typed = staticJsonOf(typedMode, staticState, valueVariable);
      setStaticState((current) => ({ ...current, json: typed.json ?? current.json }));
      setJsonMode(true);
      return;
    }

    // Back to the typed editor only when it can show what the JSON holds.
    if (staticModeFor(valueVariable, staticState.json) !== "json") {
      setStaticState(seedStatic(valueVariable, staticState.json));
      setJsonMode(false);
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

  const facts = [
    shapeText(valueVariable),
    variable.format !== null ? `format ${variable.format}` : null,
    variable.required && nested === undefined ? "required by the template" : null,
    variable.relationships.length > 0 ? `points to ${variable.relationships.join(", ")}` : null,
    variable.unitContext !== null ? `unit context ${variable.unitContext}` : null,
  ].filter((fact): fact is string => fact !== null);

  const parts = choice === "Coalesce" ? alternatives : items;
  const setParts = choice === "Coalesce" ? setAlternatives : setItems;
  const partName = choice === "Coalesce" ? "alternative" : "item";

  return (
    <>
      <SheetHeader>
        <SheetTitle className="break-all pr-6 font-mono text-[14px]" data-testid="mapping-builder-entry-target">
          {keyHolder !== null ? `${keyHolder.path}.<key>` : nested !== undefined ? `${variable.path}, ${nested.label}` : variable.path}
        </SheetTitle>
        <SheetDescription>{facts.join(", ")}.</SheetDescription>
      </SheetHeader>
      <div className="flex flex-1 flex-col gap-5 overflow-y-auto px-4 pb-4">
        <datalist id={columnsList}>
          {known.columns.map((name) => <option key={name} value={name} />)}
        </datalist>
        <datalist id={childrenList}>
          {known.children.map((name) => <option key={name} value={name} />)}
        </datalist>
        {variable.description !== null && nested === undefined && <p className="text-xs text-muted-foreground">{variable.description}</p>}
        {outside !== null && (
          <Alert data-testid="mapping-builder-entry-outside">
            <CircleAlert />
            <AlertDescription><p>{outside} Remove the entry, or keep it and let the check report it.</p></AlertDescription>
          </Alert>
        )}
        {issues.length > 0 && (
          <div className="flex flex-col gap-1 rounded-md border border-border bg-muted/30 p-2" data-testid="mapping-builder-entry-issues">
            {issues.map((issue, index) => (
              <p
                key={`${issue.severity}-${index}`}
                className={cn("flex items-start gap-1.5 text-xs", issue.severity === "error" ? "text-destructive" : "text-warning")}
              >
                {issue.severity === "error" ? <OctagonAlert className="mt-px size-3.5 shrink-0" /> : <TriangleAlert className="mt-px size-3.5 shrink-0" />}
                {issue.message}
              </p>
            ))}
            <p className="text-[11px] text-muted-foreground">From the last check. It runs again when you save.</p>
          </div>
        )}

        {keyHolder !== null && (
          <Section title="Key" hint={`A key under ${keyHolder.path}, which takes any key with a ${keyHolder.keyValueType ?? "string"} value.`}>
            <div className="flex items-center gap-1">
              <span className="font-mono text-[12px] text-muted-foreground">{keyHolder.path}.</span>
              <Input
                className="h-8 w-60 font-mono"
                placeholder="DeliveredBy"
                value={keyName}
                onChange={(event) => setKeyName(event.target.value)}
                data-testid="mapping-builder-entry-key"
              />
            </div>
          </Section>
        )}

        <Section title="Where the value comes from">
          <ToggleGroup
            type="single"
            variant="outline"
            size="sm"
            value={choice}
            onValueChange={(next) => { if (next !== "") { choose(next as Choice); } }}
            className="flex-wrap"
            data-testid="mapping-builder-entry-input"
          >
            {keyHolder === null && (nested === undefined || isProperty) && (
              <ToggleGroupItem value="None" data-testid="mapping-builder-entry-input-none">{CHOICE_LABELS.None}</ToggleGroupItem>
            )}
            {allowed.map((input) => (
              <ToggleGroupItem key={input} value={input} data-testid={`mapping-builder-entry-input-${input.toLowerCase()}`}>
                {choiceLabel(input, variable)}
              </ToggleGroupItem>
            ))}
          </ToggleGroup>
          {choice === "None" && (
            <p className="text-xs text-muted-foreground">
              {isProperty ? "A property the item does not fill is left out of it." : "A variable without an entry is left out of the record."}
              {variable.required && !isProperty && " The template requires this one, so the check reports it until it is filled."}
            </p>
          )}
        </Section>

        {(choice === "Coalesce" || choice === "List") && (
          <Section
            title={choice === "Coalesce" ? "Alternatives" : objectList ? "Items" : "Values"}
            hint={choice === "Coalesce"
              ? "Tried in order, and the first that gives a value is written: one that gives nothing (an empty column, no cached record, an id the partition holds no record under) passes to the next, and one that meets a mistake, such as a date that is not a date, holds the record. A static value can only be the last, the value when none of the others gives one."
              : objectList
                ? "Every object the items give, in the order they are written, an object given twice written once. An item is an object whose properties are each filled on their own from the dataset row, or a fixed object. An item none of whose properties gives a value adds nothing, and a property that holds holds the record, as it would on its own."
                : `Every value the items give, in the order they are written, a value given twice (whatever its case) written once. An item that gives nothing adds nothing, and one that holds holds the record, as it would on its own; an item reading every matching row with a find all adds each of them.${access ? " Every record carries the fixed values without a condition, so list at least one." : ""}`}
            action={(
              <Button
                variant="outline"
                size="xs"
                onClick={() => openPart(choice === "Coalesce" ? "alternatives" : "items", parts.length, null)}
                data-testid={`mapping-builder-entry-${partName}-add`}
              >
                <Plus />
                {choice === "Coalesce" ? "Add an alternative" : objectList ? "Add an item" : "Add a value"}
              </Button>
            )}
            testId={choice === "Coalesce" ? "mapping-builder-entry-alternatives" : "mapping-builder-entry-items"}
          >
            {choice === "Coalesce" && alternatives.length < 2 && (
              <p className="text-xs text-destructive">Add at least two alternatives; one alternative is an input of its own.</p>
            )}
            {choice === "List" && items.length === 0 && (
              <p className="text-xs text-destructive">{objectList ? "Add at least one item." : "Add at least one value."}</p>
            )}
            {choice === "List" && access && items.length > 0 && !items.some((item) => item.input === "Static" && item.when === null) && (
              <p className="text-xs text-destructive" data-testid="mapping-builder-entry-items-fixed">
                Every record carries {newTarget}, so list at least one fixed value without a condition.
              </p>
            )}
            {parts.map((part, index) => (
              <div
                key={index}
                className="flex items-center gap-1 rounded-md border border-border p-2"
                data-testid={`mapping-builder-entry-${partName}-${index}`}
              >
                <span className="w-5 shrink-0 text-xs text-muted-foreground">{index + 1}</span>
                <span className="min-w-0 flex-1 break-all font-mono text-[12px]">{alternativeText(part)}</span>
                <IconAction
                  label={`Edit this ${partName}`}
                  onClick={() => openPart(choice === "Coalesce" ? "alternatives" : "items", index, part)}
                  testId={`mapping-builder-entry-${partName}-edit-${index}`}
                >
                  <Pencil />
                </IconAction>
                <IconAction label="Move up" onClick={() => setParts((current) => moveItem(current, index, -1))} disabled={index === 0} testId={`mapping-builder-entry-${partName}-up-${index}`}>
                  <ArrowUp />
                </IconAction>
                <IconAction label="Move down" onClick={() => setParts((current) => moveItem(current, index, 1))} disabled={index === parts.length - 1} testId={`mapping-builder-entry-${partName}-down-${index}`}>
                  <ArrowDown />
                </IconAction>
                <IconAction label={`Remove this ${partName}`} onClick={() => setParts((current) => current.filter((_, i) => i !== index))} testId={`mapping-builder-entry-${partName}-remove-${index}`}>
                  <X />
                </IconAction>
              </div>
            ))}
          </Section>
        )}

        {choice === "Group" && (
          <Section
            title="Properties"
            hint={`The item is the object its properties give, each filled as the variable is anywhere, from the dataset row the record is rendered from. A property left unfilled is left out of the item, and an item none of whose properties gives a value adds nothing to ${variable.path}.`}
            testId="mapping-builder-entry-properties"
          >
            {properties.length === 0 && <p className="text-xs text-destructive">Fill at least one property.</p>}
            {itemVariables.length === 0 && (
              <p className="text-xs text-muted-foreground">The template names no property of the items of {variable.path}.</p>
            )}
            {itemVariables.map((property) => {
              const name = withinItem(variable.path, property.path);
              const filled = properties.find((candidate) => candidate.target === property.path) ?? null;
              return (
                <div
                  key={property.path}
                  className="flex items-center gap-2 rounded-md border border-border p-2"
                  data-testid={`mapping-builder-entry-property-${name}`}
                >
                  <span className="w-44 shrink-0 break-all font-mono text-[12px]" style={{ paddingLeft: (name.split(".").length - 1) * 12 }}>
                    {name}
                  </span>
                  <span className="w-24 shrink-0 text-[11px] text-muted-foreground">{shapeText(property)}</span>
                  <span className={cn("min-w-0 flex-1 break-all font-mono text-[12px]", filled === null && "text-muted-foreground")}>
                    {filled === null ? "Not filled" : entryText(filled)}
                  </span>
                  {assertionsOf(filled).length > 0 && (
                    <span className="shrink-0 text-[11px] text-muted-foreground" data-testid={`mapping-builder-entry-property-assertions-${name}`}>
                      {assertionsOf(filled).length === 1 ? "1 assertion" : `${assertionsOf(filled).length} assertions`}
                    </span>
                  )}
                  <IconAction
                    label={filled === null ? "Fill this property" : "Edit this property"}
                    onClick={() => openProperty(property, filled)}
                    testId={`mapping-builder-entry-property-edit-${name}`}
                  >
                    {filled === null ? <Plus /> : <Pencil />}
                  </IconAction>
                  {filled !== null && (
                    <IconAction
                      label="Leave this property out"
                      onClick={() => setProperties((current) => current.filter((candidate) => candidate.target !== property.path))}
                      testId={`mapping-builder-entry-property-remove-${name}`}
                    >
                      <X />
                    </IconAction>
                  )}
                </div>
              );
            })}
            {strayProperties.map((property, index) => (
              <div
                key={`${property.target}-${index}`}
                className="flex items-center gap-2 rounded-md border border-destructive/40 p-2"
                data-testid={`mapping-builder-entry-property-stray-${index}`}
              >
                <span className="min-w-0 flex-1 break-all font-mono text-[12px] text-destructive">
                  {property.target} is not a variable of the items of {variable.path}
                </span>
                <IconAction
                  label="Remove this property"
                  onClick={() => setProperties((current) => current.filter((candidate) => candidate !== property))}
                  testId={`mapping-builder-entry-property-stray-remove-${index}`}
                >
                  <X />
                </IconAction>
              </div>
            ))}
          </Section>
        )}

        {choice === "Dataset" && (
          <Section
            title="Dataset column"
            hint={repeater === null
              ? "A column of the dataset row, written without dataset."
              : `Inside ${repeater}, a column of its child rows is written ${repeaterChild ?? "child"}.column; a plain column reads the parent row.`}
          >
            <Input
              list={columnsList}
              className="h-8 font-mono"
              placeholder={repeater === null ? "log_name" : `${repeaterChild ?? "child"}.column`}
              value={column}
              onChange={(event) => setColumn(event.target.value)}
              data-testid="mapping-builder-entry-column"
            />
          </Section>
        )}

        {choice === "Expression" && (
          <Section
            title="Expression"
            hint={repeater === null
              ? "Computed from the dataset row: a column by its name, a parameter as $param.<name>, text in quotes. Such as coalesce(name, alias), upper(trim(unit)) or iif(depth > 1000, \"deep\", \"shallow\"). Work heavier than this belongs in the ingestion SQL."
              : `Inside ${repeater}, a name reads the item's row of ${repeaterChild ?? "the child dataset"}, and $dataset.<column> the dataset row. Such as coalesce(unit, $dataset.default_unit).`}
          >
            <Textarea
              className="min-h-16 font-mono text-[12px]"
              placeholder={repeater === null ? "coalesce(name, alias)" : "coalesce(unit, $dataset.default_unit)"}
              value={expression}
              onChange={(event) => setExpression(event.target.value)}
              data-testid="mapping-builder-entry-expression"
            />
          </Section>
        )}

        {choice === "Repeat" && (
          <Section
            title="Child dataset"
            hint={`One item of ${variable.path} per row of this child dataset. Fill the items' properties with their own entries, such as ${variable.path}[].Name.`}
          >
            <Input
              list={childrenList}
              className="h-8 font-mono"
              placeholder="curves"
              value={child}
              onChange={(event) => setChild(event.target.value)}
              data-testid="mapping-builder-entry-child"
            />
            <Label className="flex items-center gap-2 text-[13px] font-normal">
              <Switch checked={whereOn} onCheckedChange={setWhereOn} data-testid="mapping-builder-entry-where-on" />
              Only the rows a condition holds for
            </Label>
            {whereOn && (
              <Input
                className="h-8 font-mono"
                placeholder={'curve_id != "DEPT"'}
                value={where}
                onChange={(event) => setWhere(event.target.value)}
                data-testid="mapping-builder-entry-where"
              />
            )}
          </Section>
        )}

        {choice === "Lookup" && (
          <Section
            title="Lookup record"
            hint="The record the lookup finds for the row, read by one of its fields; id reads its OSDU id in the form relationships use. How the record is found belongs to the lookup, under Lookups on the page, so every entry reading it reads the same record."
            testId="mapping-builder-entry-lookup"
          >
            <div className="flex flex-wrap items-center gap-2">
              <ChoiceOrText
                value={lookupName}
                options={lookupOptions}
                onChange={setLookupName}
                placeholder="Lookup"
                testId="mapping-builder-entry-lookup-name"
              />
              <span className="font-mono text-[12px] text-muted-foreground">.</span>
              <ChoiceOrText
                value={lookupField}
                options={lookupFields(lookupName)}
                onChange={setLookupField}
                placeholder="id"
                testId="mapping-builder-entry-lookup-field"
              />
            </div>
          </Section>
        )}

        {choice === "Cache" && (
          <Section
            title="Cached record"
            hint={repoTypes.length === 0
              ? "The repository's cache holds no types, so the check cannot confirm the type or its fields."
              : findsAll
                ? "The cached type whose rows are read, and the field read from each of them. The values of every row found make a list, so the variable takes a list, or this is an item of one."
                : "The cached type to read, and the field to read from it. id reads the record's OSDU id in the form relationships use."}
          >
            <div className="flex flex-wrap items-center gap-2">
              <ChoiceOrText
                value={cacheType}
                options={typeOptions}
                onChange={setCacheType}
                placeholder="Cached type"
                testId="mapping-builder-entry-cache-type"
              />
              <span className="font-mono text-[12px] text-muted-foreground">.</span>
              <Input
                className="h-8 w-56 font-mono"
                placeholder="id"
                value={cacheField}
                onChange={(event) => setCacheField(event.target.value)}
                data-testid="mapping-builder-entry-cache-field"
              />
            </div>
            <ToggleGroup
              type="single"
              variant="outline"
              size="sm"
              value={findMode}
              onValueChange={(next) => { if (next === "one" || next === "all") { setFindMode(next); } }}
              className="self-start"
              data-testid="mapping-builder-entry-find-mode"
            >
              <ToggleGroupItem value="one" data-testid="mapping-builder-entry-find-one">Find one record</ToggleGroupItem>
              <ToggleGroupItem value="all" data-testid="mapping-builder-entry-find-all">Read every matching row</ToggleGroupItem>
            </ToggleGroup>
          </Section>
        )}

        {choice === "Parent" && (
          <Section
            title="Section of the parent record"
            hint={`The record of ${parent ?? "the kind the template is linked to"} with this record's minted id, read from OSDU as the record renders. The section is taken whole, an object or a list as the parent holds it. A parent OSDU does not hold holds the record until it lands; a parent without the section gives no value, which Required decides.`}
            testId="mapping-builder-entry-parent"
          >
            <ChoiceOrText
              value={parentPath}
              options={parentOptions}
              onChange={setParentPath}
              placeholder="data.DDMSDatasets"
              testId="mapping-builder-entry-parent-path"
            />
          </Section>
        )}

        {choice === "Child" && (
          <Section
            title="Child record"
            hint="The record of this kind with this record's minted id, looked for in OSDU as the record renders. Once OSDU holds it, the record links it by its id, with no version, so the link names its latest version; until then the record goes out without the link, never held for it, and the child landing plans it again so its next run adds the link. The link goes where the template has room for an id: a property of ExtensionProperties or a tag, on a canonical template."
            testId="mapping-builder-entry-child"
          >
            <ChoiceOrText
              value={childKind}
              options={childOptions}
              onChange={setChildKind}
              placeholder="welldb:wks:work-product-component--WellLogDetail:1.0.0"
              testId="mapping-builder-entry-child-kind"
            />
          </Section>
        )}

        {choice === "Search" && (
          <Section
            title="Searched record"
            hint="The search to look in, as the mapping declares it. A search reads the OSDU id of the one record it finds, in the form relationships use."
          >
            <div className="flex flex-wrap items-center gap-2">
              <ChoiceOrText
                value={cacheType}
                options={searchOptions}
                onChange={setCacheType}
                placeholder="Search"
                testId="mapping-builder-entry-search"
              />
              <span className="font-mono text-[12px] text-muted-foreground">.id</span>
            </div>
          </Section>
        )}

        {resolves && !findsAll && (
          <FindByLinesEditor
            lines={findBy}
            onChange={setFindBy}
            title="Find the record by"
            hint={choice === "Search"
              ? "Tried in order. Each line asks the platform for the one record whose property is exactly the value, and the first that finds one wins. Several matching records, or a value that cannot be asked for, hold the record."
              : "Tried in order. The first line that finds a record wins, and several matching records hold the record."}
            fieldOptions={choice === "Search" ? searchedFields : fieldOptions}
            fieldPlaceholder={choice === "Search" ? "data.FacilityName" : "Cached field"}
            newField={choice === "Search" ? "" : typeFields[0] ?? "id"}
            columnsList={columnsList}
            columnPlaceholder={columnPlaceholder}
            emptyText={choice === "Search" ? "Add at least one line, or no record can be searched for." : "Add at least one line, or no cached record can be found."}
            testId="mapping-builder-entry-findby"
          >
            {choice === "Cache" && (
              <Label className="flex items-center gap-2 text-[13px] font-normal">
                <Switch checked={ignoreSeparators} onCheckedChange={setIgnoreSeparators} data-testid="mapping-builder-entry-ignore-separators" />
                Also try with punctuation and spacing folded away (for names, never for codes)
              </Label>
            )}
          </FindByLinesEditor>
        )}

        {findsAll && (
          <Section
            title="Rows to read"
            hint="Every row whose field holds the value is read, ignoring case; a field holding a list is read when any of its values is the value, and a value a lookup's record holds several of keys each. A reference finds the rows naming the record with or without the separator before its version. The rows are then narrowed to those holding nothing under each field listed below."
            testId="mapping-builder-entry-findall"
          >
            <div className="flex flex-wrap items-center gap-1.5 rounded-md border border-border p-2">
              <ChoiceOrText
                value={allField}
                options={fieldOptions}
                onChange={setAllField}
                placeholder="FieldIDList"
                testId="mapping-builder-entry-findall-field"
              />
              <span className="font-mono text-[12px] text-muted-foreground">=</span>
              <Select value={allMode} onValueChange={(next) => setAllMode(next as FindAllMode)}>
                <SelectTrigger size="sm" className="h-8 w-44" data-testid="mapping-builder-entry-findall-mode">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="column">dataset column</SelectItem>
                  <SelectItem value="text">fixed text</SelectItem>
                  {(lookups.length > 0 || allMode === "lookup") && <SelectItem value="lookup">lookup record field</SelectItem>}
                </SelectContent>
              </Select>
              {allMode === "lookup" ? (
                <>
                  <ChoiceOrText
                    value={allLookup}
                    options={lookupOptions}
                    onChange={setAllLookup}
                    placeholder="wellbore"
                    testId="mapping-builder-entry-findall-lookup"
                  />
                  <span className="font-mono text-[12px] text-muted-foreground">.</span>
                  <ChoiceOrText
                    value={allPath}
                    options={lookupFields(allLookup).filter((option) => option.value !== "id")}
                    onChange={setAllPath}
                    placeholder="GeoContexts.FieldID"
                    testId="mapping-builder-entry-findall-path"
                  />
                </>
              ) : (
                <Input
                  list={allMode === "column" ? columnsList : undefined}
                  className="h-8 min-w-40 flex-1 font-mono text-[12px]"
                  placeholder={allMode === "column" ? columnPlaceholder : "United States"}
                  value={allValue}
                  onChange={(event) => setAllValue(event.target.value)}
                  data-testid="mapping-builder-entry-findall-value"
                />
              )}
            </div>
            {allEmpty.map((field, index) => (
              <div key={index} className="flex flex-wrap items-center gap-1.5" data-testid={`mapping-builder-entry-findall-empty-${index}`}>
                <span className="shrink-0 whitespace-nowrap text-xs text-muted-foreground">and nothing under</span>
                <ChoiceOrText
                  value={field}
                  options={fieldOptions.filter((option) => option.value !== "id")}
                  onChange={(value) => setAllEmpty((current) => current.map((old, i) => (i === index ? value : old)))}
                  placeholder="FieldList"
                  testId={`mapping-builder-entry-findall-empty-field-${index}`}
                />
                <IconAction
                  label="Remove this field"
                  onClick={() => setAllEmpty((current) => current.filter((_, i) => i !== index))}
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
              onClick={() => setAllEmpty((current) => [...current, ""])}
              data-testid="mapping-builder-entry-findall-empty-add"
            >
              <Plus />
              Add a field that must be empty
            </Button>
          </Section>
        )}

        {choice === "Static" && (
          <Section
            title="Static value"
            hint="{$param.name} tokens in text are replaced with the flow's parameter values."
            action={typedMode !== "json" ? (
              <Label className="flex items-center gap-2 text-xs font-normal">
                <Switch
                  size="sm"
                  checked={jsonMode}
                  onCheckedChange={toggleJson}
                  disabled={jsonMode && staticModeFor(valueVariable, staticState.json) === "json"}
                  data-testid="mapping-builder-entry-static-json-mode"
                />
                Write as JSON
              </Label>
            ) : undefined}
          >
            {mode === "list" && (
              <div className="flex flex-col gap-1.5" data-testid="mapping-builder-entry-static-list">
                {staticState.list.length === 0 && <p className="text-xs text-muted-foreground">No values yet.</p>}
                {staticState.list.map((item, index) => (
                  <div key={index} className="flex items-center gap-1">
                    <Input
                      className="h-8 font-mono"
                      value={item}
                      onChange={(event) => {
                        const value = event.target.value;
                        setStaticState((current) => ({ ...current, list: current.list.map((old, i) => (i === index ? value : old)) }));
                      }}
                      data-testid={`mapping-builder-entry-static-item-${index}`}
                    />
                    <IconAction
                      label="Remove this value"
                      onClick={() => setStaticState((current) => ({ ...current, list: current.list.filter((_, i) => i !== index) }))}
                      testId={`mapping-builder-entry-static-remove-${index}`}
                    >
                      <X />
                    </IconAction>
                  </div>
                ))}
                <Button
                  variant="outline"
                  size="xs"
                  className="self-start"
                  onClick={() => setStaticState((current) => ({ ...current, list: [...current.list, ""] }))}
                  data-testid="mapping-builder-entry-static-add"
                >
                  <Plus />
                  Add a value
                </Button>
              </div>
            )}
            {mode === "text" && (
              <Input
                className="h-8 font-mono"
                value={staticState.text}
                onChange={(event) => { const value = event.target.value; setStaticState((current) => ({ ...current, text: value })); }}
                data-testid="mapping-builder-entry-static"
              />
            )}
            {mode === "number" && (
              <Input
                className="h-8 w-48 font-mono"
                inputMode="decimal"
                placeholder={valueVariable.type === "integer" ? "a whole number" : "a number"}
                value={staticState.number}
                onChange={(event) => { const value = event.target.value; setStaticState((current) => ({ ...current, number: value })); }}
                data-testid="mapping-builder-entry-static"
              />
            )}
            {mode === "boolean" && (
              <Select
                value={staticState.boolean}
                onValueChange={(next) => setStaticState((current) => ({ ...current, boolean: next === "true" ? "true" : "false" }))}
              >
                <SelectTrigger size="sm" className="h-8 w-32" data-testid="mapping-builder-entry-static"><SelectValue /></SelectTrigger>
                <SelectContent>
                  <SelectItem value="true">true</SelectItem>
                  <SelectItem value="false">false</SelectItem>
                </SelectContent>
              </Select>
            )}
            {mode === "json" && (
              <Textarea
                className="field-sizing-fixed h-40 resize-y font-mono text-[12px]"
                spellCheck={false}
                value={staticState.json}
                onChange={(event) => { const value = event.target.value; setStaticState((current) => ({ ...current, json: value })); }}
                aria-invalid={staticResult.error !== null}
                data-testid="mapping-builder-entry-static"
              />
            )}
          </Section>
        )}

        {modified && (
          <ModifierListEditor
            modifiers={modifiers}
            onChange={setModifiers}
            kinds={MODIFIER_KINDS.filter((kind) => !ID_MODIFIER_KINDS.includes(kind) || choice === "Dataset" || choice === "Expression")}
            cacheTypes={repoTypes}
            hint={choice === "Cache"
              ? findsAll
                ? "On a find all, modifiers change the dataset value the rows are found by. Cached values are never modified."
                : "On a cache input, modifiers change the dataset value before it is compared. Cached values are never modified."
              : choice === "Search"
                ? "On a search input, modifiers change the dataset value before it is searched for."
                : choice === "Parent"
                  ? "Applied to the value the parent holds, top to bottom."
                  : "Applied to the dataset value, top to bottom."}
            refHint={{ path: variable.path, relationships: variable.relationships }}
            testId="mapping-builder-entry-modifier"
            sectionTestId="mapping-builder-entry-modifiers"
          >
            {(choice === "Dataset" || choice === "Expression") && buildsId && (
              <Label className="flex items-center gap-2 text-[13px] font-normal">
                <Switch checked={unverified} onCheckedChange={setUnverified} data-testid="mapping-builder-entry-unverified" />
                Write the id even when the cache holds no such record, recorded as unverified
              </Label>
            )}
          </ModifierListEditor>
        )}

        {choice !== "None" && choice !== "List" && choice !== "Group" && !isAlternative && !objectItem && (
          <Section
            title="When it applies"
            hint={choice === "Repeat"
              ? "A condition on the dataset row that decides for the whole array. When it is false, the array is written empty. A value it cannot test, such as text where it compares a number, holds the record with the reason."
              : isItem
                ? "A condition such as not empty(wellbore_uwi). When it is false, the item adds nothing to the list for that row. A value it cannot test holds the record with the reason."
                : "A condition such as depth_coding = \"REGULAR\", not empty(unit), or depth > 0 and status in [\"A\", \"B\"]. When it is false, the variable is left out for that row. A value it cannot test, such as text where it compares a number, holds the record with the reason."}
          >
            <Label className="flex items-center gap-2 text-[13px] font-normal">
              <Switch checked={conditionOn} onCheckedChange={setConditionOn} data-testid="mapping-builder-entry-applies" />
              Only when a condition holds
            </Label>
            {conditionOn && (
              <Input
                className="h-8 font-mono"
                placeholder={'depth_coding = "REGULAR"'}
                value={condition}
                onChange={(event) => setCondition(event.target.value)}
                data-testid="mapping-builder-entry-applies-condition"
              />
            )}
          </Section>
        )}

        {choice !== "None" && choice !== "Static" && choice !== "List" && choice !== "Group" && choice !== "Child" && !isAlternative && !objectItem && (
          <Section
            title="Required"
            hint={variable.required && !isItem
              ? "The template requires this variable, so its entry has to stay required."
              : choice === "Coalesce"
                ? "Off leaves the variable out when no alternative gives a value, instead of holding the record."
                : isItem
                  ? "Off adds nothing to the list when the item gives no value (an empty column, no record found, no row matching), instead of holding the record."
                  : "Off leaves the variable out when the value is empty or the cache has no match, instead of holding the record."}
          >
            <Label className="flex items-center gap-2 text-[13px] font-normal">
              <Switch checked={required} onCheckedChange={setRequired} data-testid="mapping-builder-entry-required" />
              Hold the record when there is no value
            </Label>
          </Section>
        )}

        {choice !== "None" && takesAssertions(choice, part) && (
          <MappingAssertionsSection
            assertions={assertions}
            onChange={setAssertions}
            node={{
              target: newTarget,
              input: choice,
              required,
              templateRequired: keyHolder === null && variable.required,
              severalValues: variable.shape === "ValueList" || variable.shape === "GroupList" || newTarget.includes("[]") || findsAll,
            }}
            columns={known.columns}
            columnPlaceholder={columnPlaceholder}
            fields={recordFields}
          />
        )}
        {choice !== "None" && !takesAssertions(choice, part) && assertions.length > 0 && (
          <p className="flex items-start gap-1.5 text-xs text-muted-foreground" data-testid="mapping-builder-entry-assertions-dropped">
            <TriangleAlert className="mt-px size-3.5 shrink-0 text-warning" aria-hidden />
            {`${assertions.length === 1 ? "Its assertion is" : `Its ${assertions.length} assertions are`} left out when it is saved: ${noAssertionsReason(choice, part)}.`}
          </p>
        )}

        {choice !== "None" && choice !== "List" && choice !== "Group" && !isAlternative && !objectItem && (
          <Section title="Description">
            <Input
              className="h-8"
              placeholder="Why the value comes from here (optional)"
              value={description}
              onChange={(event) => setDescription(event.target.value)}
              data-testid="mapping-builder-entry-description"
            />
          </Section>
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
      <Sheet open={editing !== null} onOpenChange={(open) => { if (!open) { setEditing(null); } }}>
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
              onSave={(path, value) => {
                if (editing.list === "properties") {
                  // A property is kept in template order, and one left unfilled is taken out of the item.
                  setProperties((current) => (value === null
                    ? current.filter((candidate) => candidate.target !== path)
                    : putEntry(current, value, variableOrder)));
                } else if (value !== null) {
                  const at = editing.index;
                  const put = (current: MappingDraftEntry[]) => (at < current.length ? current.map((old, i) => (i === at ? value : old)) : [...current, value]);
                  if (editing.list === "items") {
                    setItems(put);
                  } else {
                    setAlternatives(put);
                  }
                }

                setEditing(null);
              }}
              onClose={() => setEditing(null)}
            />
          )}
        </SheetContent>
      </Sheet>
    </>
  );
}

interface MappingEntryEditorProps {
  /** What to edit; null closes the editor. */
  target: EntryEditorTarget | null;
  draft: MappingDraft;
  /** The template's variables, of which an item of a list of objects fills those inside the list's items. */
  variables: DeliveryTemplateVariable[];
  /** The types of the cache the mapping reads, offered to a cache entry; empty when no cache is picked. */
  cacheTypes: DeliveryCachedType[];
  /** The kind the template is linked to as its parent, whose sections a parent input takes; null for a template linked to none. */
  parent: string | null;
  /** The kinds of the templates linked to this one, whose records a child input links; empty when none is. */
  childKinds: string[];
  /** Every issue of the last check; the editor shows the ones about its target. */
  issues: MappingDraftIssue[];
  /** Puts the entry for a target into the draft, or removes the target's entry when the entry is null. */
  onSave: (target: string, entry: MappingDraftEntry | null) => void;
  onClose: () => void;
}

/**
 * The entry editor: where one variable's value comes from (a dataset column, a child dataset's rows, a cached record, a
 * lookup's record, a search, a section of the record's parent, the link to one of its children, a static value, the first of
 * several alternatives, a list of values or a list of objects), with its modifiers,
 * condition, requiredness, assertions and description. It edits a copy, and Save puts the entry into the draft, so Cancel
 * leaves the draft as it was.
 */
export function MappingEntryEditor({ target, draft, variables, cacheTypes, parent, childKinds, issues, onSave, onClose }: MappingEntryEditorProps) {
  // The sheet slides out showing what it showed, so the last target stays rendered while it closes.
  const [shown, setShown] = useState<EntryEditorTarget | null>(target);
  if (target !== null && target !== shown) {
    setShown(target);
  }

  return (
    <Sheet open={target !== null} onOpenChange={(open) => { if (!open) { onClose(); } }}>
      <SheetContent className="w-full gap-0 sm:max-w-2xl" data-testid="mapping-builder-entry-editor">
        {shown !== null && (
          <EntryForm
            key={shown.session}
            target={shown}
            draft={draft}
            variables={variables}
            cacheTypes={cacheTypes}
            parent={parent}
            childKinds={childKinds}
            issues={shown.keyHolder === null ? issues.filter((issue) => issue.target === shown.variable.path) : []}
            onSave={onSave}
            onClose={onClose}
          />
        )}
      </SheetContent>
    </Sheet>
  );
}
