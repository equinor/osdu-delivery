import { useId, useState, type ReactNode } from "react";
import { ArrowDown, ArrowUp, Info, Pencil, Plus, TriangleAlert, X } from "lucide-react";
import { RichTooltip } from "@/components/RichTooltip";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Sheet, SheetContent, SheetDescription, SheetFooter, SheetHeader, SheetTitle } from "@/components/ui/sheet";
import { Switch } from "@/components/ui/switch";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import { cn } from "@/lib/utils";
import type { MappingDraftAssertion, MappingDraftAssertionFilter } from "../../api/delivery";
import type { AssertionAction, AssertionStage } from "../../api/validation";
import { AssertionActionBadge } from "./AssertionFindingsView";
import { IconAction } from "./MappingEditorParts";
import { assertionConditionText, assertionText } from "./mappingAssertionText";
import {
  ASSERTION_OPERATORS, assertionConflicts, FLAG_OPERATORS, filterReads, ignoresCase, incomingBlocked, JSON_TYPES, MAX_ASSERTION_CONDITIONS,
  MAX_ASSERTION_NAME, MAX_ENTRY_ASSERTIONS, newAssertion, omitBlocked, OPERATOR_MEANINGS, operandExample, operandFor, operandProblem,
  recordFieldOf, takesTolerance, toleranceOf, type AssertionNode,
} from "./mappingAssertions";
import { isWithin, repeaterOfPath } from "./recordPath";
import { bareColumn, moveItem } from "./mappingDraft";
import { assertionActionWord, assertionStageWord } from "./validationModel";

/** An explanation behind an info mark, as the editor gives every setting its own. */
function InfoTip({ title, body }: { title: string; body: string }) {
  return (
    <RichTooltip title={title} body={body}>
      <Info className="size-3.5 shrink-0 text-muted-foreground" aria-label={title} />
    </RichTooltip>
  );
}

/** One setting of the assertion form: its name with what it means on hover, and its control. */
function Setting({ label, tip, children, testId }: { label: string; tip: string; children: ReactNode; testId?: string }) {
  return (
    <>
      <div className="flex h-8 items-center gap-1 text-[12px] text-muted-foreground">
        {label}
        <InfoTip title={label} body={tip} />
      </div>
      <div className="flex min-w-0 flex-col gap-1.5" data-testid={testId}>{children}</div>
    </>
  );
}

interface Choice<T extends string> {
  value: T;
  label: string;
  /** What the choice means, on hover. */
  tip: string;
  /** Why the choice is not offered here, which the hover says instead; null when it is. */
  blocked?: string | null;
}

/**
 * A choice of a few words, each with what it means on hover. A choice the entry does not allow stays visible, dimmed, and
 * says on hover why; picking it does nothing, so a value already written that way is kept until another is picked.
 */
function ChoiceButtons<T extends string>({ value, choices, onChange, testId }: {
  value: T;
  choices: Choice<T>[];
  onChange: (value: T) => void;
  testId: string;
}) {
  return (
    <ToggleGroup
      type="single"
      variant="outline"
      size="sm"
      spacing={1}
      value={value}
      onValueChange={(next) => {
        const picked = choices.find((choice) => choice.value === next);
        if (picked !== undefined && (picked.blocked ?? null) === null) {
          onChange(picked.value);
        }
      }}
      className="flex-wrap"
      data-testid={testId}
    >
      {choices.map((choice) => {
        const blocked = (choice.blocked ?? null) !== null;
        return (
          <RichTooltip key={choice.value} title={choice.label} body={choice.blocked ?? choice.tip}>
            <span className="inline-flex">
              <ToggleGroupItem
                value={choice.value}
                aria-disabled={blocked}
                className={cn("h-7 text-[12px] font-normal", blocked && "cursor-not-allowed opacity-50")}
                data-testid={`${testId}-${choice.value}`}
                data-blocked={blocked || undefined}
              >
                {choice.label}
              </ToggleGroupItem>
            </span>
          </RichTooltip>
        );
      })}
    </ToggleGroup>
  );
}

/** The operator of a condition, each offered with what a value meets it by; the picked one shows its word alone. */
function OperatorSelect({ value, onChange, className, testId }: { value: string; onChange: (operator: string) => void; className?: string; testId: string }) {
  const known = ASSERTION_OPERATORS.includes(value);
  return (
    <Select value={value} onValueChange={onChange}>
      <SelectTrigger size="sm" className={cn("font-mono text-[12px]", className)} data-testid={testId}>
        <SelectValue>{value}</SelectValue>
      </SelectTrigger>
      <SelectContent>
        {!known && value !== "" && <SelectItem value={value}><span className="font-mono text-[12px] text-destructive">{value}</span></SelectItem>}
        {ASSERTION_OPERATORS.map((operator) => (
          <SelectItem key={operator} value={operator}>
            <span className="font-mono text-[12px]">{operator}</span>
            <span className="text-[11px] text-muted-foreground">{OPERATOR_MEANINGS[operator]}</span>
          </SelectItem>
        ))}
      </SelectContent>
    </Select>
  );
}

/** What a condition compares with: true or false, a JSON type, or one line of YAML as the document writes it. */
function OperandControl({ operator, value, onChange, className, testId }: {
  operator: string;
  value: string;
  onChange: (operand: string) => void;
  className?: string;
  testId: string;
}) {
  const choices = FLAG_OPERATORS.has(operator) ? ["true", "false"] : operator === "type" ? JSON_TYPES : null;
  if (choices !== null) {
    return (
      <Select value={choices.includes(value.trim()) ? value.trim() : ""} onValueChange={onChange}>
        <SelectTrigger size="sm" className={cn("w-32 font-mono text-[12px]", className)} data-testid={testId}>
          <SelectValue placeholder={operandExample(operator)} />
        </SelectTrigger>
        <SelectContent>
          {choices.map((choice) => <SelectItem key={choice} value={choice}><span className="font-mono text-[12px]">{choice}</span></SelectItem>)}
        </SelectContent>
      </Select>
    );
  }

  return (
    <Input
      className={cn("min-w-28 flex-1 font-mono text-[12px]", className)}
      spellCheck={false}
      placeholder={operandExample(operator)}
      value={value}
      onChange={(event) => onChange(event.target.value)}
      aria-invalid={value.trim() === "" ? undefined : operandProblem(operator, value) !== null}
      data-testid={testId}
    />
  );
}

/** One condition an assertion is judged under, as the form holds it: the tolerance as typed. */
interface FilterState {
  path: string;
  operator: string;
  operand: string;
  ignoreCase: boolean;
  tolerance: string;
}

function filterStateOf(filter: MappingDraftAssertionFilter): FilterState {
  return {
    path: filter.path,
    operator: filter.operator,
    operand: filter.operand,
    ignoreCase: filter.ignoreCase,
    tolerance: filter.tolerance === null ? "" : String(filter.tolerance),
  };
}

const STAGE_TIPS: Record<AssertionStage, string> = {
  record: "The value the record carries, after the modifiers and the conversion to the property's type: what OSDU holds. A value the record does not carry is judged only by exists and empty.",
  incoming: "The value the row gives, before the modifiers: the column, or what the expression computes. Judged only when the entry applies to the row; a blank value is no value.",
};

const ACTION_TIPS: Record<AssertionAction, string> = {
  hold: "The record is not sent; its document is kept until the row or the mapping changes, or a release accepts it.",
  report: "The record is sent as it is, and the failure is recorded on the attempt.",
  omit: "The value that fails is left out of the record and the failure is recorded; an object left with nothing is left out with it. Each value is judged on its own.",
};

interface AssertionFormProps {
  /** The target the entry fills, which the title names. */
  target: string;
  /** Where the assertion is in the entry's list, from one; a new one is given the next place. */
  number: number;
  assertion: MappingDraftAssertion | null;
  /** The entry's other assertions, whose names and conditions this one may not repeat. */
  others: MappingDraftAssertion[];
  node: AssertionNode;
  /** The dataset columns the draft names, offered to a condition of the incoming stage. */
  columns: string[];
  /** How a column reads where the entry is: `column`, or `child.column` inside a repeated array. */
  columnPlaceholder: string;
  /** The fields of the record the template has, offered to a condition of the record stage. */
  fields: string[];
  onSave: (assertion: MappingDraftAssertion) => void;
  onClose: () => void;
}

/**
 * One assertion of an entry, edited as a form of its own over the entry's: the condition, the value it judges, what a
 * failure does, how several values are judged, the conditions it is judged under, its name and its description. It edits
 * a copy, so Cancel leaves the entry's assertions as they were.
 */
function AssertionForm({ target, number, assertion, others, node, columns, columnPlaceholder, fields, onSave, onClose }: AssertionFormProps) {
  const ids = useId();
  const initial = assertion ?? newAssertion();
  const [operator, setOperator] = useState(initial.operator);
  const [operand, setOperand] = useState(initial.operand);
  const [stage, setStage] = useState<AssertionStage>(initial.stage);
  const [onFail, setOnFail] = useState<AssertionAction>(initial.onFail);
  const [anyValue, setAnyValue] = useState(initial.anyValue);
  const [ignoreCase, setIgnoreCase] = useState(initial.ignoreCase);
  const [tolerance, setTolerance] = useState(initial.tolerance === null ? "" : String(initial.tolerance));
  const [filters, setFilters] = useState<FilterState[]>(() => initial.where.map(filterStateOf));
  const [name, setName] = useState(initial.name ?? "");
  const [description, setDescription] = useState(initial.description ?? "");

  const incomingReason = incomingBlocked(node);
  const omitReason = omitBlocked(node);
  const incoming = stage === "incoming";
  // Whether one of several values may meet it is said only on the record stage, of a node holding several values, or of
  // one already written to say it.
  const showValues = !incoming && (node.severalValues || anyValue);
  const caseApplies = ignoresCase(operator);
  const toleranceApplies = takesTolerance(operator);
  const toleranceRead = toleranceApplies ? toleranceOf(tolerance) : { value: null, error: null };
  const filterTolerances = filters.map((filter) => (takesTolerance(filter.operator) ? toleranceOf(filter.tolerance) : { value: null, error: null }));
  const columnsList = `${ids}-columns`;
  const fieldsList = `${ids}-fields`;
  // A condition inside a repeated array most often reads another field of the same item, so one of those is the example.
  const ownField = recordFieldOf(target);
  const repeater = repeaterOfPath(target);
  const itemHolder = repeater === null ? null : recordFieldOf(repeater);
  const fieldExample = (itemHolder === null ? undefined : fields.find((field) => field !== ownField && field !== itemHolder && isWithin(field, itemHolder))) ?? "data.Name";
  const reads = filterReads(stage);

  const trimmedName = name.trim();
  // The assertion as Save puts it into the entry: settings that mean nothing for its operator or its stage left out.
  const edited: MappingDraftAssertion = {
    operator: operator.trim(),
    operand: operand.trim(),
    stage,
    onFail,
    anyValue: !incoming && anyValue,
    ignoreCase: caseApplies && ignoreCase,
    tolerance: toleranceRead.value,
    where: filters.map((filter, index) => ({
      reads,
      path: incoming ? bareColumn(filter.path) : filter.path.trim(),
      operator: filter.operator.trim(),
      operand: filter.operand.trim(),
      ignoreCase: ignoresCase(filter.operator) && filter.ignoreCase,
      tolerance: filterTolerances[index].value,
    })),
    name: trimmedName === "" ? null : trimmedName,
    description: description.trim() === "" ? null : description.trim(),
  };
  const sameName = trimmedName !== "" && others.some((other) => (other.name ?? "").trim() === trimmedName);
  // Without a name an assertion is called by what it states (its condition, how it reads several values, its conditions),
  // and two of an entry called the same are refused.
  const statement = (candidate: MappingDraftAssertion) => `${assertionText(candidate)}${candidate.anyValue ? " for one of its values" : ""}`;
  const sameStatement = trimmedName === "" && others.some((other) => (other.name ?? "").trim() === "" && statement(other) === statement(edited));
  const filterIssues = filters.flatMap((filter, index) => {
    const operandIssue = operandProblem(filter.operator, filter.operand);
    return [
      filter.path.trim() === "" ? `Condition ${index + 1}: name the ${reads} it reads.` : null,
      operandIssue === null ? null : `Condition ${index + 1}: ${operandIssue}`,
    ];
  });
  const problems = [
    operandProblem(operator, operand),
    incoming && incomingReason !== null ? `Judge the value the record carries. ${incomingReason}` : null,
    onFail === "omit" && omitReason !== null ? `Hold or report the record. ${omitReason}` : null,
    sameName ? `Another assertion of this entry is named '${trimmedName}'; each has a name of its own, since what it finds is recorded under it.` : null,
    sameStatement
      ? `Another assertion of this entry states ${assertionConditionText(edited)} under the same conditions without a name, so both would be called the same; name one of them, or remove one.`
      : null,
    ...filterIssues,
  ].filter((problem): problem is string => problem !== null);
  // Only what the draft cannot hold stops Save; what the check would refuse is said here and reported by the check.
  const error = toleranceRead.error ?? filterTolerances.find((read) => read.error !== null)?.error ?? null;

  const pickOperator = (next: string) => {
    setOperand(operandFor(next, operator, operand));
    setOperator(next);
  };

  const updateFilter = (index: number, patch: Partial<FilterState>) =>
    setFilters((current) => current.map((filter, i) => (i === index ? { ...filter, ...patch } : filter)));

  const save = () => {
    if (error === null) {
      onSave(edited);
    }
  };

  return (
    <>
      <SheetHeader>
        <SheetTitle className="break-all pr-6 font-mono text-[14px]" data-testid="mapping-builder-assertion-target">
          {`${target}, assertion ${number}`}
        </SheetTitle>
        <SheetDescription>A rule the value must meet, judged on every record the mapping renders.</SheetDescription>
      </SheetHeader>
      <div className="flex flex-1 flex-col gap-3 overflow-y-auto px-4 pb-4">
        <datalist id={columnsList}>
          {columns.map((column) => <option key={column} value={column} />)}
        </datalist>
        <datalist id={fieldsList}>
          {fields.map((field) => <option key={field} value={field} />)}
        </datalist>
        <div className="grid grid-cols-[7.5rem_minmax(0,1fr)] gap-x-3 gap-y-3">
          <Setting
            label="Condition"
            tip={"What the value must be, in the assertion flows' own words. The operand is one line as the mapping writes it after the operator: text in quotes ('GR'), so text that looks like a number stays text ('5'); numbers and true or false as they are; a list in brackets ([GR, SP]); a range as [low, high]; a count as a number or { atLeast: 1, atMost: 16 }. Numbers compare as numbers and ISO 8601 dates as instants. A value the condition cannot judge, such as text where a number is compared, fails it."}
            testId="mapping-builder-assertion-condition"
          >
            <div className="flex flex-wrap items-center gap-1.5">
              <OperatorSelect value={operator} onChange={pickOperator} className="h-8 w-36" testId="mapping-builder-assertion-operator" />
              <OperandControl operator={operator} value={operand} onChange={setOperand} className="h-8" testId="mapping-builder-assertion-operand" />
            </div>
            {(caseApplies || toleranceApplies) && (
              <div className="flex flex-wrap items-center gap-x-4 gap-y-1.5">
                {caseApplies && (
                  <div className="flex items-center gap-1.5">
                    <Label className="flex items-center gap-1.5 text-[12px] font-normal">
                      <Switch size="sm" checked={ignoreCase} onCheckedChange={setIgnoreCase} data-testid="mapping-builder-assertion-ignore-case" />
                      Ignore case
                    </Label>
                    <InfoTip title="Ignore case" body="Text is compared ignoring case. Without it, text compares ordinally, so 'gr' does not equal 'GR'." />
                  </div>
                )}
                {toleranceApplies && (
                  <div className="flex items-center gap-1.5">
                    <Label className="flex items-center gap-1.5 text-[12px] font-normal">
                      Tolerance
                      <Input
                        className="h-7 w-24 font-mono text-[12px]"
                        inputMode="decimal"
                        placeholder="none"
                        value={tolerance}
                        onChange={(event) => setTolerance(event.target.value)}
                        aria-invalid={toleranceRead.error !== null}
                        data-testid="mapping-builder-assertion-tolerance"
                      />
                    </Label>
                    <InfoTip title="Tolerance" body="How far off a number may be and still compare as written: zero or more. It applies when what the condition compares with is a number; without it, numbers compare exactly." />
                  </div>
                )}
              </div>
            )}
          </Setting>

          <Setting
            label="Judges"
            tip={incomingReason === null
              ? `${STAGE_TIPS.record}\n\n${STAGE_TIPS.incoming}`
              : `${STAGE_TIPS.record}\n\n${incomingReason}`}
            testId="mapping-builder-assertion-stage"
          >
            {incomingReason === null || incoming
              ? (
                <ChoiceButtons
                  value={stage}
                  onChange={setStage}
                  testId="mapping-builder-assertion-stage-choice"
                  choices={[
                    { value: "record", label: "Value the record carries", tip: STAGE_TIPS.record },
                    { value: "incoming", label: "Value the row gives", tip: STAGE_TIPS.incoming, blocked: incomingReason },
                  ]}
                />
              )
              : <span className="flex h-8 items-center text-[12px]">Value the record carries</span>}
          </Setting>

          <Setting
            label="When it fails"
            tip="What a record whose value fails the assertion does. Several assertions are judged each on its own, and any failure that holds holds the record."
            testId="mapping-builder-assertion-onfail"
          >
            <ChoiceButtons
              value={onFail}
              onChange={setOnFail}
              testId="mapping-builder-assertion-onfail-choice"
              choices={[
                { value: "hold", label: "Hold the record", tip: ACTION_TIPS.hold },
                { value: "report", label: "Send and report", tip: ACTION_TIPS.report },
                {
                  value: "omit",
                  label: "Leave the value out",
                  tip: ACTION_TIPS.omit,
                  blocked: omitReason ?? (anyValue && !incoming ? "One of several values meeting it judges the values together, so no one value fails it; judge every value to leave out each that fails." : null),
                },
              ]}
            />
          </Setting>

          {showValues && (
            <Setting
              label="Values"
              tip="For a property holding several values (a list, the items of a repeated array): every value has to meet it, or one of them does. length, contains, notContains and empty judge a list as a whole."
              testId="mapping-builder-assertion-values"
            >
              <ChoiceButtons
                value={anyValue ? "any" : "all"}
                onChange={(next) => setAnyValue(next === "any")}
                testId="mapping-builder-assertion-values-choice"
                choices={[
                  { value: "all", label: "Every value", tip: "Every value the property holds has to meet it, and each that does not is a failure." },
                  {
                    value: "any",
                    label: "One of them",
                    tip: "One of the values the property holds has to meet it.",
                    blocked: onFail === "omit" ? "Leaving the value out judges each value on its own, so it cannot be judged by one of them meeting it." : null,
                  },
                ]}
              />
            </Setting>
          )}

          <Setting
            label="Only when"
            tip={incoming
              ? `Up to ${MAX_ASSERTION_CONDITIONS} conditions selecting the rows the assertion is judged on; all of them have to hold. Each reads a column of the row the entry reads, named as the entry names columns (${columnPlaceholder}${columnPlaceholder.includes(".") ? ", or a plain column for the dataset's own row" : ""}).`
              : `Up to ${MAX_ASSERTION_CONDITIONS} conditions selecting the records the assertion is judged on; all of them have to hold. Each reads a field of the record, such as data.Name; a field inside the same repeated array as the property reads the same item. A field the record does not carry selects nothing.`}
            testId="mapping-builder-assertion-where"
          >
            {filters.length === 0 && <span className="flex h-8 items-center text-[12px] text-muted-foreground">Every {incoming ? "row" : "record"}</span>}
            {filters.map((filter, index) => (
              <div key={index} className="flex flex-col gap-1.5 rounded-md border border-border p-1.5" data-testid={`mapping-builder-assertion-where-${index}`}>
                <div className="flex items-center gap-1.5">
                  <span className="w-12 shrink-0 text-[11px] text-muted-foreground">{reads}</span>
                  <Input
                    list={incoming ? columnsList : fieldsList}
                    className="h-7 min-w-0 flex-1 font-mono text-[12px]"
                    spellCheck={false}
                    placeholder={incoming ? columnPlaceholder : fieldExample}
                    value={filter.path}
                    onChange={(event) => updateFilter(index, { path: event.target.value })}
                    data-testid={`mapping-builder-assertion-where-path-${index}`}
                  />
                  <IconAction
                    label="Remove this condition"
                    onClick={() => setFilters((current) => current.filter((_, i) => i !== index))}
                    testId={`mapping-builder-assertion-where-remove-${index}`}
                  >
                    <X />
                  </IconAction>
                </div>
                <div className="flex flex-wrap items-center gap-1.5 pl-[3.375rem]">
                  <OperatorSelect
                    value={filter.operator}
                    onChange={(next) => updateFilter(index, { operator: next, operand: operandFor(next, filter.operator, filter.operand) })}
                    className="h-7 w-28"
                    testId={`mapping-builder-assertion-where-operator-${index}`}
                  />
                  <OperandControl
                    operator={filter.operator}
                    value={filter.operand}
                    onChange={(next) => updateFilter(index, { operand: next })}
                    className="h-7 min-w-24"
                    testId={`mapping-builder-assertion-where-operand-${index}`}
                  />
                  {ignoresCase(filter.operator) && (
                    <Label className="flex items-center gap-1.5 text-[11px] font-normal text-muted-foreground">
                      <Switch
                        size="sm"
                        checked={filter.ignoreCase}
                        onCheckedChange={(checked) => updateFilter(index, { ignoreCase: checked })}
                        data-testid={`mapping-builder-assertion-where-ignore-case-${index}`}
                      />
                      ignore case
                    </Label>
                  )}
                  {takesTolerance(filter.operator) && (
                    <Input
                      className="h-7 w-24 font-mono text-[12px] placeholder:font-sans"
                      inputMode="decimal"
                      placeholder="tolerance"
                      value={filter.tolerance}
                      onChange={(event) => updateFilter(index, { tolerance: event.target.value })}
                      aria-invalid={filterTolerances[index].error !== null}
                      aria-label="Tolerance"
                      data-testid={`mapping-builder-assertion-where-tolerance-${index}`}
                    />
                  )}
                </div>
              </div>
            ))}
            <Button
              variant="ghost"
              size="xs"
              className="self-start"
              disabled={filters.length >= MAX_ASSERTION_CONDITIONS}
              onClick={() => setFilters((current) => [...current, { path: "", operator: "equals", operand: "", ignoreCase: false, tolerance: "" }])}
              data-testid="mapping-builder-assertion-where-add"
            >
              <Plus />
              {filters.length >= MAX_ASSERTION_CONDITIONS ? `At most ${MAX_ASSERTION_CONDITIONS} conditions` : "Add a condition"}
            </Button>
          </Setting>

          <Setting
            label="Name"
            tip={`What messages, the ledger and the reports call the assertion; without one, a label read off its condition. Each assertion of an entry has a name of its own, 1 to ${MAX_ASSERTION_NAME} characters.`}
          >
            <Input
              className="h-8 text-[12px]"
              maxLength={MAX_ASSERTION_NAME}
              placeholder="Optional, such as top-depth-range"
              value={name}
              onChange={(event) => setName(event.target.value)}
              data-testid="mapping-builder-assertion-name"
            />
          </Setting>

          <Setting label="Description" tip="Why the rule holds, for whoever reads the mapping or a failure it finds.">
            <Input
              className="h-8 text-[12px]"
              placeholder="Optional"
              value={description}
              onChange={(event) => setDescription(event.target.value)}
              data-testid="mapping-builder-assertion-description"
            />
          </Setting>
        </div>

        {problems.length > 0 && (
          <div className="flex flex-col gap-1" data-testid="mapping-builder-assertion-problems">
            {problems.map((problem) => (
              <p key={problem} className="flex items-start gap-1.5 text-xs text-destructive">
                <TriangleAlert className="mt-px size-3.5 shrink-0" aria-hidden />
                {problem}
              </p>
            ))}
          </div>
        )}
      </div>
      <SheetFooter className="flex-row flex-wrap items-center justify-end gap-2 border-t border-border">
        {error !== null && <p className="mr-auto text-xs font-medium text-destructive" data-testid="mapping-builder-assertion-error">{error}</p>}
        <Button variant="ghost" size="sm" onClick={onClose} data-testid="mapping-builder-assertion-cancel">Cancel</Button>
        <Button size="sm" onClick={save} disabled={error !== null} data-testid="mapping-builder-assertion-save">Save assertion</Button>
      </SheetFooter>
    </>
  );
}

/** One assertion in a few lines, for the hover over its row: what it asserts, the value it judges, what a failure does, and why. */
function assertionDetail(assertion: MappingDraftAssertion): string {
  return [
    assertionText(assertion),
    `Judges ${assertionStageWord(assertion.stage)}${assertion.anyValue ? ", one of its values meeting it" : ""}.`,
    `When it fails: ${assertionActionWord(assertion.onFail)}.`,
    assertion.description,
  ].filter((line): line is string => line !== null && line.trim() !== "").join("\n");
}

/** Which assertion the nested form edits: its place (the end for a new one), and a number per opening. */
interface EditedAssertion {
  index: number;
  assertion: MappingDraftAssertion | null;
  session: number;
}

interface MappingAssertionsSectionProps {
  assertions: MappingDraftAssertion[];
  onChange: (assertions: MappingDraftAssertion[]) => void;
  node: AssertionNode;
  /** The dataset columns the draft names, offered to a condition of the incoming stage. */
  columns: string[];
  /** How a column reads where the entry is: `column`, or `child.column` inside a repeated array. */
  columnPlaceholder: string;
  /** The fields of the record the template has, offered to a condition of the record stage. */
  fields: string[];
}

/**
 * An entry's assertions ($assert, osdu/docs/reference/flow/mapping-assertions.md): one row each, with what it asserts, its
 * name, the stage when it judges the row's value, and what a failure does; added, edited, reordered and removed here, each
 * edited in a form of its own. What the entry's settings leave an assertion unable to say is listed under the rows, as the
 * check would refuse it.
 */
export function MappingAssertionsSection({ assertions, onChange, node, columns, columnPlaceholder, fields }: MappingAssertionsSectionProps) {
  const [editing, setEditing] = useState<EditedAssertion | null>(null);
  const [openings, setOpenings] = useState(0);
  const conflicts = assertionConflicts(assertions, node);
  const full = assertions.length >= MAX_ENTRY_ASSERTIONS;

  const open = (index: number, assertion: MappingDraftAssertion | null) => {
    const session = openings + 1;
    setOpenings(session);
    setEditing({ index, assertion, session });
  };

  const tip = [
    "Business rules the value must meet, judged on every record the mapping renders ($assert). Each says what a record that fails it does: held, sent with the failure recorded, or sent without the value. What they find is recorded on every attempt.",
    node.input === "Repeat"
      ? "On a repeat they judge the array as a whole, such as length { atLeast: 2 }."
      : node.input === "Coalesce"
        ? "On a first value of several they judge the value written, whichever alternative gave it."
        : null,
    `An entry states at most ${MAX_ENTRY_ASSERTIONS}.`,
  ].filter((line): line is string => line !== null).join("\n\n");

  return (
    <section className="flex flex-col gap-2" data-testid="mapping-builder-entry-assertions">
      <div className="flex min-h-7 items-center gap-1.5">
        <h3 className="text-[13px] font-medium">Assertions</h3>
        <InfoTip title="Assertions" body={tip} />
        {assertions.length > 0 && <span className="font-mono text-[11px] tabular-nums text-muted-foreground">{assertions.length}</span>}
        <Button
          variant="outline"
          size="xs"
          className="ml-auto"
          disabled={full}
          onClick={() => open(assertions.length, null)}
          data-testid="mapping-builder-entry-assertion-add"
        >
          <Plus />
          {full ? `At most ${MAX_ENTRY_ASSERTIONS}` : "Add an assertion"}
        </Button>
      </div>
      {assertions.length === 0
        ? <p className="text-xs text-muted-foreground">No assertions.</p>
        : (
          <div className="flex flex-col divide-y divide-border rounded-md border border-border">
            {assertions.map((assertion, index) => {
              const name = (assertion.name ?? "").trim();
              return (
                <div key={index} className="flex min-w-0 items-center gap-1.5 px-2 py-1" data-testid={`mapping-builder-entry-assertion-${index}`}>
                  <span className="w-4 shrink-0 text-[11px] tabular-nums text-muted-foreground">{index + 1}</span>
                  <RichTooltip title={name === "" ? `Assertion ${index + 1}` : name} body={assertionDetail(assertion)}>
                    <span className="min-w-0 flex-1 truncate font-mono text-[12px]" data-testid={`mapping-builder-entry-assertion-text-${index}`}>
                      {assertionText(assertion)}
                    </span>
                  </RichTooltip>
                  {name !== "" && <span className="min-w-0 max-w-36 shrink truncate text-[11px] text-muted-foreground">{name}</span>}
                  {assertion.stage === "incoming" && (
                    <RichTooltip title="Incoming" body={STAGE_TIPS.incoming}>
                      <Badge variant="outline" className="shrink-0 font-normal text-[11px]" data-testid={`mapping-builder-entry-assertion-incoming-${index}`}>incoming</Badge>
                    </RichTooltip>
                  )}
                  <AssertionActionBadge action={assertion.onFail} className="shrink-0 text-[11px]" />
                  <div className="flex shrink-0 items-center">
                    <IconAction label="Edit this assertion" onClick={() => open(index, assertion)} testId={`mapping-builder-entry-assertion-edit-${index}`}>
                      <Pencil />
                    </IconAction>
                    <IconAction label="Move up" onClick={() => onChange(moveItem(assertions, index, -1))} disabled={index === 0} testId={`mapping-builder-entry-assertion-up-${index}`}>
                      <ArrowUp />
                    </IconAction>
                    <IconAction
                      label="Move down"
                      onClick={() => onChange(moveItem(assertions, index, 1))}
                      disabled={index === assertions.length - 1}
                      testId={`mapping-builder-entry-assertion-down-${index}`}
                    >
                      <ArrowDown />
                    </IconAction>
                    <IconAction
                      label="Remove this assertion"
                      onClick={() => onChange(assertions.filter((_, i) => i !== index))}
                      testId={`mapping-builder-entry-assertion-remove-${index}`}
                    >
                      <X />
                    </IconAction>
                  </div>
                </div>
              );
            })}
          </div>
        )}
      {conflicts.map((conflict) => (
        <p key={conflict} className="text-xs text-destructive" data-testid="mapping-builder-entry-assertion-conflict">{conflict}</p>
      ))}
      {/* One assertion, edited as a form of its own over the entry's. */}
      <Sheet open={editing !== null} onOpenChange={(opened) => { if (!opened) { setEditing(null); } }}>
        <SheetContent className="w-full gap-0 sm:max-w-2xl" data-testid="mapping-builder-assertion-editor">
          {editing !== null && (
            <AssertionForm
              key={editing.session}
              target={node.target}
              number={editing.index + 1}
              assertion={editing.assertion}
              others={assertions.filter((_, i) => i !== editing.index)}
              node={node}
              columns={columns}
              columnPlaceholder={columnPlaceholder}
              fields={fields}
              onSave={(saved) => {
                const at = editing.index;
                onChange(at < assertions.length ? assertions.map((old, i) => (i === at ? saved : old)) : [...assertions, saved]);
                setEditing(null);
              }}
              onClose={() => setEditing(null)}
            />
          )}
        </SheetContent>
      </Sheet>
    </section>
  );
}
