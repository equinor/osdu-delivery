// The entry editor's static value: the editor the variable's type takes (a list of values, a text, a number or a boolean),
// or the value written as JSON.

import { Plus, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Switch } from "@/components/ui/switch";
import { Textarea } from "@/components/ui/textarea";
import type { DeliveryTemplateVariable } from "../../../api/delivery";
import { IconAction, Section } from "../MappingEditorParts";
import { staticModeFor } from "../mappingDraft";
import type { StaticValue } from "./useStaticValue";

export function StaticValueSection({ staticValue, valueVariable }: { staticValue: StaticValue; valueVariable: DeliveryTemplateVariable }) {
  const { typedMode, jsonMode, mode, state: staticState, setState: setStaticState, result: staticResult, toggleJson } = staticValue;

  return (
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
  );
}
