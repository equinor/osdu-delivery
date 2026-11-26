// The top of the entry editor's form: the target it fills and what the template says of it, the notes on it (its
// description, why no mapping may fill it, what the last check found), the name of a new key, and where the value comes from.

import { CircleAlert, OctagonAlert, TriangleAlert } from "lucide-react";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Input } from "@/components/ui/input";
import { SheetDescription, SheetHeader, SheetTitle } from "@/components/ui/sheet";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import { cn } from "@/lib/utils";
import type { DeliveryTemplateVariable, MappingDraftInput, MappingDraftIssue } from "../../../api/delivery";
import { Section } from "../MappingEditorParts";
import { isPlainName } from "../recordPath";
import { shapeText } from "../templateFormat";
import { CHOICE_LABELS, choiceLabel, type Choice, type NestedRole } from "./entryFormModel";

/** The sheet's heading: the target, or the part of it a nested form edits, and the facts the template gives about it. */
export function EntryTitle({
  variable, valueVariable, keyHolder, nested,
}: {
  variable: DeliveryTemplateVariable;
  /** The variable as the form edits its value: one item for an item of a list. */
  valueVariable: DeliveryTemplateVariable;
  keyHolder: DeliveryTemplateVariable | null;
  nested: NestedRole | undefined;
}) {
  const facts = [
    shapeText(valueVariable),
    variable.format !== null ? `format ${variable.format}` : null,
    variable.required && nested === undefined ? "required by the template" : null,
    variable.relationships.length > 0 ? `points to ${variable.relationships.join(", ")}` : null,
    variable.unitContext !== null ? `unit context ${variable.unitContext}` : null,
  ].filter((fact): fact is string => fact !== null);

  return (
    <SheetHeader>
      <SheetTitle className="break-all pr-6 font-mono text-[14px]" data-testid="mapping-builder-entry-target">
        {keyHolder !== null ? `${keyHolder.path}.<key>` : nested !== undefined ? `${variable.path}, ${nested.label}` : variable.path}
      </SheetTitle>
      <SheetDescription>{facts.join(", ")}.</SheetDescription>
    </SheetHeader>
  );
}

/** What is known about the target before it is filled: its description, why no mapping may fill it, and the last check's issues. */
export function EntryNotes({
  variable, nested, outside, issues,
}: {
  variable: DeliveryTemplateVariable;
  nested: NestedRole | undefined;
  outside: string | null;
  issues: MappingDraftIssue[];
}) {
  return (
    <>
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
    </>
  );
}

/** The name of a new key under an object with free keys, and the target it makes when it is no plain name. */
export function EntryKeySection({
  keyHolder, keyName, onKeyName, newTarget,
}: {
  keyHolder: DeliveryTemplateVariable;
  keyName: string;
  onKeyName: (name: string) => void;
  newTarget: string;
}) {
  return (
    <Section
      title="Key"
      hint={`A key under ${keyHolder.path}, which takes any key with a ${keyHolder.keyValueType ?? "string"} value. A name holding a dot, such as lra.securityClassification, is one key.`}
    >
      <div className="flex items-center gap-1">
        <span className="font-mono text-[12px] text-muted-foreground">{keyHolder.path}.</span>
        <Input
          className="h-8 w-60 font-mono"
          placeholder="DeliveredBy"
          value={keyName}
          onChange={(event) => onKeyName(event.target.value)}
          data-testid="mapping-builder-entry-key"
        />
      </div>
      {keyName.trim() !== "" && !isPlainName(keyName.trim()) && (
        <p className="text-[11px] text-muted-foreground" data-testid="mapping-builder-entry-key-target">
          One key, which the template names <span className="font-mono">{newTarget}</span>.
        </p>
      )}
    </Section>
  );
}

/** Where the value comes from: the inputs the target takes, and Not filled where leaving it out is a choice. */
export function EntryInputSection({
  choice, onChoose, allowed, variable, offersNone, isProperty,
}: {
  choice: Choice;
  onChoose: (next: Choice) => void;
  allowed: MappingDraftInput[];
  variable: DeliveryTemplateVariable;
  /** Whether Not filled is offered: never for a new key, an alternative or an item. */
  offersNone: boolean;
  isProperty: boolean;
}) {
  return (
    <Section title="Where the value comes from">
      <ToggleGroup
        type="single"
        variant="outline"
        size="sm"
        value={choice}
        onValueChange={(next) => { if (next !== "") { onChoose(next as Choice); } }}
        className="flex-wrap"
        data-testid="mapping-builder-entry-input"
      >
        {offersNone && (
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
  );
}
