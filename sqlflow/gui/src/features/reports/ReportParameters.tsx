// The row of controls above a report's tiles: one per parameter the report declares, each saying what it is, what it
// is set to and which tiles it narrows. A control only reports a value; the page keeps the values in its address.

import { useState } from "react";
import { FilterX, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { activeFilterClass, FilterBar } from "@/components/FilterBar";
import { FilterCombobox, type FilterOption } from "@/components/FilterCombobox";
import { cn } from "@/lib/utils";
import type { Report, ReportParameter } from "../../api/types";
import {
  choicesFrom, effectiveValue, isSet, parameterLabel, queryTitle, type GivenValues,
} from "./reportModel";
import type { QueryState } from "./useReportQueries";

interface ReportParametersProps {
  report: Report;
  given: GivenValues;
  states: ReadonlyMap<string, QueryState>;
  onChange: (parameter: ReportParameter, value: string | null) => void;
  onReset: () => void;
}

export function ReportParameters({ report, given, states, onChange, onReset }: ReportParametersProps) {
  if (report.parameters.length === 0) {
    return null;
  }

  const anySet = report.parameters.some((parameter) => isSet(parameter, given));
  return (
    <FilterBar>
      {report.parameters.map((parameter) => (
        <ParameterField
          key={parameter.name}
          report={report}
          parameter={parameter}
          value={effectiveValue(parameter, given)}
          changed={isSet(parameter, given)}
          feeder={parameter.choicesFrom === null ? null : states.get(parameter.choicesFrom) ?? null}
          onChange={(value) => onChange(parameter, value)}
        />
      ))}
      {anySet && (
        <Button variant="ghost" size="sm" onClick={onReset} data-testid="report-parameters-reset">
          <FilterX />
          Reset
        </Button>
      )}
    </FilterBar>
  );
}

/** What a parameter narrows, in a sentence: the tiles whose queries bind it. */
function impact(report: Report, parameter: ReportParameter): string {
  const narrowed = report.queries
    .filter((query) => query.views.length > 0 && parameter.usedBy.includes(query.name))
    .map(queryTitle);
  return narrowed.length === 0
    ? "It sets the choices of another filter."
    : `Narrows: ${narrowed.join(", ")}.`;
}

function ParameterField({ report, parameter, value, changed, feeder, onChange }: {
  report: Report;
  parameter: ReportParameter;
  value: string | null;
  changed: boolean;
  /** The query whose rows are this parameter's choices, when the document reads them from one. */
  feeder: QueryState | null;
  onChange: (value: string | null) => void;
}) {
  const label = parameterLabel(parameter);
  const testId = `report-parameter-${parameter.name}`;
  const listed = parameter.choices.length > 0 || parameter.choicesFrom !== null;

  return (
    <div className="flex items-center gap-1.5">
      <Tooltip delayDuration={300} disableHoverableContent>
        <TooltipTrigger asChild>
          <span className="cursor-default text-xs text-muted-foreground">
            {label}
            {parameter.required && <span className="ml-0.5 text-destructive" aria-hidden>*</span>}
          </span>
        </TooltipTrigger>
        <TooltipContent variant="panel" side="bottom" align="start" className="pointer-events-none max-w-xs">
          {parameter.description !== null && <div>{parameter.description}</div>}
          <div className={cn("text-muted-foreground", parameter.description !== null && "mt-1")}>{impact(report, parameter)}</div>
          {parameter.required && <div className="mt-1 text-muted-foreground">Required: its tiles wait until it is set.</div>}
        </TooltipContent>
      </Tooltip>
      {listed
        ? <ChoiceControl parameter={parameter} label={label} value={value} feeder={feeder} onChange={onChange} testId={testId} />
        : parameter.type === "flag"
          ? <FlagControl label={label} value={value} changed={changed} onChange={onChange} testId={testId} />
          : <TypedControl parameter={parameter} label={label} value={value} changed={changed} onChange={onChange} testId={testId} />}
    </div>
  );
}

/** A parameter with a list of choices, written in the document or read from one of the report's queries. */
function ChoiceControl({ parameter, label, value, feeder, onChange, testId }: {
  parameter: ReportParameter;
  label: string;
  value: string | null;
  feeder: QueryState | null;
  onChange: (value: string | null) => void;
  testId: string;
}) {
  const fromDocument: FilterOption[] = parameter.choices.map((choice) => ({ value: choice.value, label: choice.label ?? choice.value }));
  const fromQuery: FilterOption[] = feeder?.answer !== null && feeder?.answer !== undefined
    ? choicesFrom(feeder.answer.rows).map((choice) => (choice.label === choice.value
      ? { value: choice.value, label: choice.label }
      : { value: choice.value, label: choice.label, hint: choice.value }))
    : [];
  const options = parameter.choicesFrom === null ? fromDocument : fromQuery;

  // A value in effect that the list does not hold (set by a link, or no longer answered) is still shown as set.
  const shown = value !== null && !options.some((option) => option.value === value)
    ? [{ value, label: value }, ...options]
    : options;
  const reading = feeder !== null && feeder.status === "running";
  const unreadable = feeder !== null && (feeder.status === "failed" || feeder.status === "blocked" || feeder.status === "cancelled");

  return (
    <FilterCombobox
      options={shown}
      value={value ?? ""}
      onChange={(next) => onChange(next === "" ? null : next)}
      placeholder={reading ? "Reading..." : unreadable ? "Choices not read" : parameter.required ? "Choose" : "Any"}
      searchPlaceholder={`Find ${label.toLowerCase()}...`}
      emptyText={unreadable ? "The query that lists the choices did not answer." : "No matches."}
      ariaLabel={label}
      testId={testId}
      className="w-44"
    />
  );
}

const ANY = "any";

/** A yes-or-no parameter, which can also be left unset. */
function FlagControl({ label, value, changed, onChange, testId }: {
  label: string;
  value: string | null;
  changed: boolean;
  onChange: (value: string | null) => void;
  testId: string;
}) {
  return (
    <Select value={value ?? ANY} onValueChange={(next) => onChange(next === ANY ? null : next)}>
      <SelectTrigger size="sm" active={changed} className="h-8 w-24 text-[13px]" aria-label={label} data-testid={testId}>
        <SelectValue />
      </SelectTrigger>
      <SelectContent>
        <SelectItem value={ANY}>Any</SelectItem>
        <SelectItem value="true">Yes</SelectItem>
        <SelectItem value="false">No</SelectItem>
      </SelectContent>
    </Select>
  );
}

const INPUT_TYPE: Record<string, string> = { integer: "number", number: "number", date: "date", datetime: "datetime-local", text: "text" };
const INPUT_WIDTH: Record<string, string> = { integer: "w-24", number: "w-28", date: "w-36", datetime: "w-52", text: "w-44" };

/** Whether a date or a date and time, as a field writes it, is whole: a field reports a year still being typed as 0002. */
function isWholeDate(text: string): boolean {
  const year = /^(\d{4,})-\d{2}-\d{2}/.exec(text);
  return year !== null && Number(year[1]) >= 1000;
}

/**
 * A parameter a person types: text, a number, a date. What is typed takes effect when the field is left or Enter is
 * pressed, so a query is not run once per keystroke; a date takes effect as it is picked, once its year is whole.
 */
function TypedControl({ parameter, label, value, changed, onChange, testId }: {
  parameter: ReportParameter;
  label: string;
  value: string | null;
  changed: boolean;
  onChange: (value: string | null) => void;
  testId: string;
}) {
  const [draft, setDraft] = useState(value ?? "");
  // A value set elsewhere (a click on a bar, a link, Reset) replaces what was typed. The field itself stays, so typing
  // is never interrupted by the value it has just reported.
  const [reported, setReported] = useState(value);
  if (value !== reported) {
    setReported(value);
    setDraft(value ?? "");
  }

  const picked = parameter.type === "date" || parameter.type === "datetime";
  const commit = (text: string) => {
    const next = text.trim() === "" ? null : text.trim();
    if (next !== value) onChange(next);
  };

  return (
    <div className="relative">
      <Input
        type={INPUT_TYPE[parameter.type] ?? "text"}
        step={parameter.type === "integer" ? 1 : parameter.type === "number" ? "any" : parameter.type === "datetime" ? 1 : undefined}
        value={draft}
        placeholder={parameter.required ? "Required" : "Any"}
        aria-label={label}
        className={cn("h-8 text-[13px]", INPUT_WIDTH[parameter.type] ?? "w-44", changed && activeFilterClass, draft !== "" && !picked && "pr-7")}
        onChange={(event) => {
          setDraft(event.target.value);
          if (picked && (event.target.value === "" || isWholeDate(event.target.value))) commit(event.target.value);
        }}
        onBlur={() => commit(draft)}
        onKeyDown={(event) => {
          if (event.key === "Enter") commit(draft);
        }}
        data-testid={testId}
      />
      {draft !== "" && !picked && (
        <button
          type="button"
          className="absolute right-1.5 top-1/2 -translate-y-1/2 rounded-sm p-0.5 text-muted-foreground hover:text-foreground focus-visible:outline-1 focus-visible:outline-ring"
          aria-label={`Clear ${label}`}
          onClick={() => {
            setDraft("");
            commit("");
          }}
          data-testid={`${testId}-clear`}
        >
          <X className="size-3.5" />
        </button>
      )}
    </div>
  );
}
