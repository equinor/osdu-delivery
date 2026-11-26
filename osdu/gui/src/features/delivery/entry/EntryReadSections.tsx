// Where the entry editor's form reads one value from, a section per input: a dataset column, an expression, a child dataset's
// rows, a lookup's record, a cached record, a section of the parent record, the link to a child record, and a search.

import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Switch } from "@/components/ui/switch";
import { Textarea } from "@/components/ui/textarea";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import type { DeliveryCachedType, DeliveryTemplateVariable, MappingDraftLookup, MappingDraftSearch } from "../../../api/delivery";
import { ChoiceOrText, Section, type ChoiceOption } from "../MappingEditorParts";
import { itemSteps } from "../recordPath";
import { lookupFieldsOf, lookupOptionsOf } from "./entryFormModel";

/** Where a value read inside a repeater's items is read: its repeater's target, and the child dataset its rows come from. */
interface RepeaterPlace {
  /** The target of the repeat entry whose items the entry fills; null outside one. */
  repeater: string | null;
  /** The child dataset the repeater's rows come from. */
  repeaterChild: string | null;
}

export function DatasetSection({
  repeater, repeaterChild, columnsList, column, onColumn,
}: RepeaterPlace & { columnsList: string; column: string; onColumn: (column: string) => void }) {
  return (
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
        onChange={(event) => onColumn(event.target.value)}
        data-testid="mapping-builder-entry-column"
      />
    </Section>
  );
}

export function ExpressionSection({
  repeater, repeaterChild, expression, onExpression,
}: RepeaterPlace & { expression: string; onExpression: (expression: string) => void }) {
  return (
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
        onChange={(event) => onExpression(event.target.value)}
        data-testid="mapping-builder-entry-expression"
      />
    </Section>
  );
}

export function RepeatSection({
  variable, childrenList, child, onChild, whereOn, onWhereOn, where, onWhere,
}: {
  variable: DeliveryTemplateVariable;
  childrenList: string;
  child: string;
  onChild: (child: string) => void;
  whereOn: boolean;
  onWhereOn: (on: boolean) => void;
  where: string;
  onWhere: (where: string) => void;
}) {
  return (
    <Section
      title="Child dataset"
      hint={`One item of ${variable.path} per row of this child dataset. Fill the items' properties with their own entries, such as ${variable.path}[].Name.`}
    >
      <Input
        list={childrenList}
        className="h-8 font-mono"
        placeholder="curves"
        value={child}
        onChange={(event) => onChild(event.target.value)}
        data-testid="mapping-builder-entry-child"
      />
      <Label className="flex items-center gap-2 text-[13px] font-normal">
        <Switch checked={whereOn} onCheckedChange={onWhereOn} data-testid="mapping-builder-entry-where-on" />
        Only the rows a condition holds for
      </Label>
      {whereOn && (
        <Input
          className="h-8 font-mono"
          placeholder={'curve_id != "DEPT"'}
          value={where}
          onChange={(event) => onWhere(event.target.value)}
          data-testid="mapping-builder-entry-where"
        />
      )}
    </Section>
  );
}

export function LookupSection({
  lookups, repoTypes, lookupName, onLookupName, lookupField, onLookupField,
}: {
  lookups: MappingDraftLookup[];
  repoTypes: DeliveryCachedType[];
  lookupName: string;
  onLookupName: (name: string) => void;
  lookupField: string;
  onLookupField: (field: string) => void;
}) {
  return (
    <Section
      title="Lookup record"
      hint="The record the lookup finds for the row, read by one of its fields; id reads its OSDU id in the form relationships use. How the record is found belongs to the lookup, under Lookups on the page, so every entry reading it reads the same record."
      testId="mapping-builder-entry-lookup"
    >
      <div className="flex flex-wrap items-center gap-2">
        <ChoiceOrText
          value={lookupName}
          options={lookupOptionsOf(lookups)}
          onChange={onLookupName}
          placeholder="Lookup"
          testId="mapping-builder-entry-lookup-name"
        />
        <span className="font-mono text-[12px] text-muted-foreground">.</span>
        <ChoiceOrText
          value={lookupField}
          options={lookupFieldsOf(lookups, repoTypes, lookupName)}
          onChange={onLookupField}
          placeholder="id"
          testId="mapping-builder-entry-lookup-field"
        />
      </div>
    </Section>
  );
}

export function CacheSection({
  variable, repoTypes, cacheType, onCacheType, cacheField, onCacheField, findMode, onFindMode, findsAll,
}: {
  variable: DeliveryTemplateVariable;
  repoTypes: DeliveryCachedType[];
  cacheType: string;
  onCacheType: (type: string) => void;
  cacheField: string;
  onCacheField: (field: string) => void;
  findMode: "one" | "all";
  onFindMode: (mode: "one" | "all") => void;
  /** Whether the entry reads every matching row. */
  findsAll: boolean;
}) {
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

  return (
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
          onChange={onCacheType}
          placeholder="Cached type"
          testId="mapping-builder-entry-cache-type"
        />
        <span className="font-mono text-[12px] text-muted-foreground">.</span>
        <Input
          className="h-8 w-56 font-mono"
          placeholder="id"
          value={cacheField}
          onChange={(event) => onCacheField(event.target.value)}
          data-testid="mapping-builder-entry-cache-field"
        />
      </div>
      <ToggleGroup
        type="single"
        variant="outline"
        size="sm"
        value={findMode}
        onValueChange={(next) => { if (next === "one" || next === "all") { onFindMode(next); } }}
        className="self-start"
        data-testid="mapping-builder-entry-find-mode"
      >
        <ToggleGroupItem value="one" data-testid="mapping-builder-entry-find-one">Find one record</ToggleGroupItem>
        <ToggleGroupItem value="all" data-testid="mapping-builder-entry-find-all">Read every matching row</ToggleGroupItem>
      </ToggleGroup>
    </Section>
  );
}

export function ParentSection({
  parent, variables, parentPath, onParentPath,
}: {
  /** The kind the template is linked to as its parent. */
  parent: string | null;
  variables: DeliveryTemplateVariable[];
  parentPath: string;
  onParentPath: (path: string) => void;
}) {
  // The paths of the template, which a custom template copied from its parent shares, offered as the section to take.
  const parentOptions: ChoiceOption[] = variables
    .filter((candidate) => candidate.role === "Mapping" && candidate.path.startsWith("osdu.data.") && itemSteps(candidate.path) === 0)
    .map((candidate) => ({ value: candidate.path.slice("osdu.".length), group: "Paths of the template, as its parent's copy holds them" }));

  return (
    <Section
      title="Section of the parent record"
      hint={`The record of ${parent ?? "the kind the template is linked to"} with this record's minted id, read from OSDU as the record renders. The section is taken whole, an object or a list as the parent holds it. A parent OSDU does not hold holds the record until it lands; a parent without the section gives no value, which Required decides.`}
      testId="mapping-builder-entry-parent"
    >
      <ChoiceOrText
        value={parentPath}
        options={parentOptions}
        onChange={onParentPath}
        placeholder="data.DDMSDatasets"
        testId="mapping-builder-entry-parent-path"
      />
    </Section>
  );
}

export function ChildSection({
  childKinds, childKind, onChildKind,
}: {
  childKinds: string[];
  childKind: string;
  onChildKind: (kind: string) => void;
}) {
  const childOptions: ChoiceOption[] = childKinds.map((kind) => ({ value: kind, group: "Templates linked to this one" }));

  return (
    <Section
      title="Child record"
      hint="The record of this kind with this record's minted id, looked for in OSDU as the record renders. Once OSDU holds it, the record links it by its id, with no version, so the link names its latest version; until then the record goes out without the link, never held for it, and the child landing plans it again so its next run adds the link. The link goes where the template has room for an id: a property of ExtensionProperties or a tag, on a canonical template."
      testId="mapping-builder-entry-child"
    >
      <ChoiceOrText
        value={childKind}
        options={childOptions}
        onChange={onChildKind}
        placeholder="welldb:wks:work-product-component--WellLogDetail:1.0.0"
        testId="mapping-builder-entry-child-kind"
      />
    </Section>
  );
}

export function SearchSection({
  searches, cacheType, onCacheType,
}: {
  searches: MappingDraftSearch[];
  /** The search looked in, which the entry names where a cache entry names its cached type. */
  cacheType: string;
  onCacheType: (search: string) => void;
}) {
  const searchOptions: ChoiceOption[] = searches.map((search) => ({ value: search.name, hint: search.kind, group: "The mapping's searches" }));

  return (
    <Section
      title="Searched record"
      hint="The search to look in, as the mapping declares it. A search reads the OSDU id of the one record it finds, in the form relationships use."
    >
      <div className="flex flex-wrap items-center gap-2">
        <ChoiceOrText
          value={cacheType}
          options={searchOptions}
          onChange={onCacheType}
          placeholder="Search"
          testId="mapping-builder-entry-search"
        />
        <span className="font-mono text-[12px] text-muted-foreground">.id</span>
      </div>
    </Section>
  );
}
