// What the entry editor's form says of the value once it is read: the modifiers it goes through, the condition it applies
// under, whether it is required (and restricts the record instead while missing), its assertions, and its description.

import { useMemo } from "react";
import { TriangleAlert } from "lucide-react";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Switch } from "@/components/ui/switch";
import type { DeliveryCachedType, DeliveryTemplateVariable, MappingDraftAssertion, MappingDraftModifier } from "../../../api/delivery";
import { MappingAssertionsSection } from "../MappingAssertionEditor";
import { ModifierListEditor, Section } from "../MappingEditorParts";
import { noAssertionsReason, recordFieldOf, takesAssertions, type AssertionPart } from "../mappingAssertions";
import { ID_MODIFIER_KINDS, MODIFIER_KINDS } from "../mappingDraft";
import { itemSteps } from "../recordPath";
import type { Choice } from "./entryFormModel";

/** The modifiers the value read goes through, and for a dataset value building an id whether an id the cache lacks is written. */
export function EntryModifiersSection({
  choice, findsAll, modifiers, onModifiers, repoTypes, variable, buildsId, unverified, onUnverified,
}: {
  choice: Choice;
  findsAll: boolean;
  modifiers: MappingDraftModifier[];
  onModifiers: (modifiers: MappingDraftModifier[]) => void;
  repoTypes: DeliveryCachedType[];
  variable: DeliveryTemplateVariable;
  /** Whether a modifier builds an id. */
  buildsId: boolean;
  unverified: boolean;
  onUnverified: (on: boolean) => void;
}) {
  return (
    <ModifierListEditor
      modifiers={modifiers}
      onChange={onModifiers}
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
          <Switch checked={unverified} onCheckedChange={onUnverified} data-testid="mapping-builder-entry-unverified" />
          Write the id even when the cache holds no such record, recorded as unverified
        </Label>
      )}
    </ModifierListEditor>
  );
}

/** The condition on the dataset row the entry applies under. */
export function WhenSection({
  choice, isItem, conditionOn, onConditionOn, condition, onCondition,
}: {
  choice: Choice;
  isItem: boolean;
  conditionOn: boolean;
  onConditionOn: (on: boolean) => void;
  condition: string;
  onCondition: (condition: string) => void;
}) {
  return (
    <Section
      title="When it applies"
      hint={choice === "Repeat"
        ? "A condition on the dataset row that decides for the whole array. When it is false, the array is written empty. A value it cannot test, such as text where it compares a number, holds the record with the reason."
        : isItem
          ? "A condition such as not empty(wellbore_uwi). When it is false, the item adds nothing to the list for that row. A value it cannot test holds the record with the reason."
          : "A condition such as depth_coding = \"REGULAR\", not empty(unit), or depth > 0 and status in [\"A\", \"B\"]. When it is false, the variable is left out for that row. A value it cannot test, such as text where it compares a number, holds the record with the reason."}
    >
      <Label className="flex items-center gap-2 text-[13px] font-normal">
        <Switch checked={conditionOn} onCheckedChange={onConditionOn} data-testid="mapping-builder-entry-applies" />
        Only when a condition holds
      </Label>
      {conditionOn && (
        <Input
          className="h-8 font-mono"
          placeholder={'depth_coding = "REGULAR"'}
          value={condition}
          onChange={(event) => onCondition(event.target.value)}
          data-testid="mapping-builder-entry-applies-condition"
        />
      )}
    </Section>
  );
}

/** Whether a missing value holds the record, and whether it restricts the record instead while the value is missing. */
export function RequiredSection({
  choice, variable, isItem, required, onRequired, restrictable, restrict, onRestrict,
}: {
  choice: Choice;
  variable: DeliveryTemplateVariable;
  isItem: boolean;
  required: boolean;
  onRequired: (on: boolean) => void;
  /** Whether the input chosen can restrict the record when it gives no value. */
  restrictable: boolean;
  restrict: boolean;
  onRestrict: (on: boolean) => void;
}) {
  return (
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
        <Switch checked={required} onCheckedChange={onRequired} data-testid="mapping-builder-entry-required" />
        Hold the record when there is no value
      </Label>
      {restrictable && (
        <Label
          className="mt-1 flex items-center gap-2 text-[13px] font-normal"
          title={variable.required && !isItem
            ? "The template requires this variable, so a record without it cannot be sent: it stays held when the value is missing."
            : "$restrictWhenMissing: when the value is missing, the property is left out and the record goes out under the mapping's restricted access instead of being held, and it gets its own access back once a delivery finds the value. The mapping names that access under restrictedAccess."}
        >
          <Switch
            checked={required && restrict}
            disabled={!required || (variable.required && !isItem)}
            onCheckedChange={onRestrict}
            data-testid="mapping-builder-entry-restrict-when-missing"
          />
          Restrict the record instead while the value is missing
        </Label>
      )}
    </Section>
  );
}

/** The entry's assertions where the input chosen takes them, or a note that Save leaves out those it holds. */
export function EntryAssertions({
  choice, part, assertions, onAssertions, newTarget, required, variable, keyHolder, findsAll, variables, columns, columnPlaceholder,
}: {
  choice: Choice;
  part: AssertionPart;
  assertions: MappingDraftAssertion[];
  onAssertions: (assertions: MappingDraftAssertion[]) => void;
  newTarget: string;
  required: boolean;
  variable: DeliveryTemplateVariable;
  keyHolder: DeliveryTemplateVariable | null;
  findsAll: boolean;
  variables: DeliveryTemplateVariable[];
  /** The dataset's columns the mapping knows. */
  columns: string[];
  columnPlaceholder: string;
}) {
  // The record paths a condition of an assertion may read: those the template has that a mapping or the engine writes.
  const recordFields = useMemo(
    () => [...new Set(variables.filter((candidate) => candidate.role !== "Osdu").map((candidate) => recordFieldOf(candidate.path)))],
    [variables],
  );

  return (
    <>
      {choice !== "None" && takesAssertions(choice, part) && (
        <MappingAssertionsSection
          assertions={assertions}
          onChange={onAssertions}
          node={{
            target: newTarget,
            input: choice,
            required,
            templateRequired: keyHolder === null && variable.required,
            severalValues: variable.shape === "ValueList" || variable.shape === "GroupList" || itemSteps(newTarget) > 0 || findsAll,
          }}
          columns={columns}
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
    </>
  );
}

/** Why the value comes from where it does, in the entry's own words. */
export function DescriptionSection({ description, onDescription }: { description: string; onDescription: (description: string) => void }) {
  return (
    <Section title="Description">
      <Input
        className="h-8"
        placeholder="Why the value comes from here (optional)"
        value={description}
        onChange={(event) => onDescription(event.target.value)}
        data-testid="mapping-builder-entry-description"
      />
    </Section>
  );
}
