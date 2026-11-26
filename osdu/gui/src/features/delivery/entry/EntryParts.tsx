// The parts an entry of the entry editor's form holds, each edited in a nested form over it: the alternatives of a coalesce
// or the items of a list, in order, and the properties an item of a list of objects fills, in template order.

import type { Dispatch, SetStateAction } from "react";
import { ArrowDown, ArrowUp, Pencil, Plus, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { cn } from "@/lib/utils";
import type { DeliveryTemplateVariable, MappingDraftEntry } from "../../../api/delivery";
import { IconAction, Section } from "../MappingEditorParts";
import { assertionsOf } from "../mappingAssertions";
import { alternativeText, entryText, moveItem, withinItem } from "../mappingDraft";
import { itemSteps, pathNames, repeaterOfPath } from "../recordPath";
import { shapeText } from "../templateFormat";

/** The alternatives of a coalesce, tried in order, or the items of a list of values or of objects. */
export function EntryPartsSection({
  choice, alternatives, setAlternatives, items, setItems, objectList, access, newTarget, onOpen,
}: {
  choice: "Coalesce" | "List";
  alternatives: MappingDraftEntry[];
  setAlternatives: Dispatch<SetStateAction<MappingDraftEntry[]>>;
  items: MappingDraftEntry[];
  setItems: Dispatch<SetStateAction<MappingDraftEntry[]>>;
  /** Whether the list fills a list of objects. */
  objectList: boolean;
  /** Whether the target is one of the record's access lists, which every record carries. */
  access: boolean;
  newTarget: string;
  onOpen: (list: "alternatives" | "items", index: number, part: MappingDraftEntry | null) => void;
}) {
  const parts = choice === "Coalesce" ? alternatives : items;
  const setParts = choice === "Coalesce" ? setAlternatives : setItems;
  const partName = choice === "Coalesce" ? "alternative" : "item";

  return (
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
          onClick={() => onOpen(choice === "Coalesce" ? "alternatives" : "items", parts.length, null)}
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
            onClick={() => onOpen(choice === "Coalesce" ? "alternatives" : "items", index, part)}
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
  );
}

/**
 * The properties an item of a list of objects fills, one row per variable its items hold in template order, and any the
 * entry holds that the template does not name.
 */
export function EntryPropertiesSection({
  variable, variables, objectItem, properties, setProperties, onOpen,
}: {
  variable: DeliveryTemplateVariable;
  variables: DeliveryTemplateVariable[];
  /** Whether the form edits an item of a list of objects, whose properties the template names. */
  objectItem: boolean;
  properties: MappingDraftEntry[];
  setProperties: Dispatch<SetStateAction<MappingDraftEntry[]>>;
  onOpen: (property: DeliveryTemplateVariable, part: MappingDraftEntry | null) => void;
}) {
  // The variables an item of this list of objects holds, in template order, each a property the item may fill. A list of
  // objects inside the items is left out, since an array inside the items of another is not supported.
  const itemPrefix = `${variable.path}[]`;
  const itemVariables = objectItem
    ? variables.filter((candidate) => candidate.path.length > itemPrefix.length && candidate.path.startsWith(itemPrefix) && candidate.role === "Mapping" && !candidate.nested
      && repeaterOfPath(candidate.path) === variable.path && itemSteps(candidate.path) === 1)
    : [];
  const strayProperties = properties.filter((property) => !itemVariables.some((candidate) => candidate.path === property.target));

  return (
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
            <span className="w-44 shrink-0 break-all font-mono text-[12px]" style={{ paddingLeft: ((pathNames(name) ?? name.split(".")).length - 1) * 12 }}>
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
              onClick={() => onOpen(property, filled)}
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
  );
}
