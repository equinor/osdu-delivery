import { useRef, useState, type ChangeEvent, type KeyboardEvent, type ReactNode } from "react";
import { Check, ChevronsUpDown, Loader2, TriangleAlert, X } from "lucide-react";
import { Command, CommandEmpty, CommandItem, CommandList } from "@/components/ui/command";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Popover, PopoverAnchor, PopoverContent } from "@/components/ui/popover";
import type { RunValue } from "@/api/types";
import { runValueItems, runValueText } from "@/lib/runValues";
import { cn } from "@/lib/utils";
import type { DeliveryFlowScope, DeliveryParameter, DeliveryScopeParameterValues } from "../../api/delivery";
import type { ScopeFieldValues } from "./scopeValues";
import { useScopeValues } from "./useScopeValues";

const count = new Intl.NumberFormat("en-US");

/** The input surface every field here wears, so a field with its name inside it reads as the inputs beside it. */
const SURFACE = "rounded-md border border-input bg-transparent shadow-xs transition-[color,box-shadow] dark:bg-input/30";

interface ScopeParameterFieldsProps {
  /** The flow whose scope the values fill, or null before one is known. */
  pipelineId: string | null;
  /** Its interface and partition. */
  scope: DeliveryFlowScope;
  /** The parameters the flow declares, each saying the column its scope reads, if any. */
  parameters: DeliveryParameter[];
  values: ScopeFieldValues;
  onChange: (name: string, value: RunValue) => void;
  /** Whether the reader may queue the node read of the values; without it every field is typed. */
  canRead: boolean;
  /** Ids and testids are `<prefix>-parameter-<name>`. */
  prefix: string;
  /**
   * `stacked` puts each parameter's name above its field, for a form; `inline` puts it inside the field, for a toolbar
   * that keeps its controls on one line.
   */
  layout?: "stacked" | "inline";
  /** The width of each field. */
  className?: string;
}

/**
 * A flow's parameters as fields, for the pages that read a scope of it (a flow's Preview tab, a mapping's value check, the
 * trigger dialog). A parameter the scope binds to a column of the record table offers that column's values, with how many
 * rows hold each, read from the flow's own table whatever the source, and takes several of them: the scope then reads the
 * rows holding any (osdu/docs/reference/flow/delivery.md, Parameters). It still takes any value typed; a value the column
 * holds no row of says the scope would read nothing of it. Any other parameter (one naming the work location) is typed and
 * takes one value. The fields are returned side by side for the page to lay out.
 */
export function ScopeParameterFields({
  pipelineId, scope, parameters, values, onChange, canRead, prefix, layout = "stacked", className,
}: ScopeParameterFieldsProps) {
  const scoped = parameters.some((parameter) => parameter.scopeColumn);
  const read = useScopeValues(pipelineId, scope, canRead && scoped);
  const readError = read.isError ? (read.error instanceof Error ? read.error.message : String(read.error)) : null;

  return (
    <>
      {parameters.map((parameter) => {
        const id = `${prefix}-parameter-${parameter.name}`;
        const needed = parameter.required && (parameter.default === null || parameter.default === undefined);
        const placeholder = parameter.default ?? (parameter.required ? "required" : "optional");
        const column = parameter.scopeColumn ?? null;
        const given = values[parameter.name];
        const name = (
          <>
            <span className="font-mono">{parameter.name}</span>
            {needed && <span className="text-destructive">*</span>}
          </>
        );
        const field = column === null || !canRead
          ? (
            <TypedInput
              id={id}
              value={given === undefined ? "" : runValueText(given)}
              onChange={(value) => onChange(parameter.name, value)}
              placeholder={placeholder}
              label={layout === "inline" ? name : null}
              title={parameter.description ?? undefined}
              className={className}
            />
          )
          : (
            <ScopeValuesInput
              id={id}
              column={column}
              value={runValueItems(given)}
              onChange={(items) => onChange(parameter.name, items)}
              placeholder={placeholder}
              listed={read.data?.parameters.find((listed) => listed.parameter === parameter.name)}
              loading={read.isFetching && read.data === undefined}
              error={readError}
              label={layout === "inline" ? name : null}
              title={parameter.description ?? undefined}
              className={className}
            />
          );

        return layout === "inline"
          ? <div key={parameter.name}>{field}</div>
          : (
            <div key={parameter.name} className="flex flex-col gap-1">
              <Label htmlFor={id} className="text-[12px] text-muted-foreground" title={parameter.description ?? undefined}>{name}</Label>
              {field}
            </div>
          );
      })}
    </>
  );
}

/**
 * A field whose name sits inside it, before the value, on the input's own surface: what a toolbar uses to keep a
 * parameter on one line. The whole field takes the focus ring its input would.
 */
function Shell({ id, label, title, className, invalid, children }: {
  id: string;
  label: ReactNode;
  title?: string;
  className?: string;
  invalid?: boolean;
  children: ReactNode;
}) {
  return (
    <div
      className={cn(
        "relative flex h-8 items-center", SURFACE,
        "focus-within:border-ring focus-within:ring-[3px] focus-within:ring-ring/50",
        invalid && "border-warning",
        className,
      )}
      title={title}
    >
      <label htmlFor={id} className="flex h-full shrink-0 items-center gap-0.5 border-r px-2 text-[11px] text-muted-foreground">{label}</label>
      {children}
    </div>
  );
}

const BARE = "h-full min-w-0 flex-1 bg-transparent px-2 font-mono text-[12px] outline-none placeholder:text-muted-foreground";

function TypedInput({ id, value, onChange, placeholder, label, title, className }: {
  id: string;
  value: string;
  onChange: (value: string) => void;
  placeholder: string;
  label: ReactNode | null;
  title?: string;
  className?: string;
}) {
  if (label === null) {
    return (
      <Input
        id={id}
        value={value}
        onChange={(event) => onChange(event.target.value)}
        placeholder={placeholder}
        className={cn("h-8 font-mono text-[12px]", className)}
        spellCheck={false}
        autoComplete="off"
        data-testid={id}
      />
    );
  }

  return (
    <Shell id={id} label={label} title={title} className={className}>
      <input
        id={id}
        value={value}
        onChange={(event) => onChange(event.target.value)}
        placeholder={placeholder}
        className={BARE}
        spellCheck={false}
        autoComplete="off"
        data-testid={id}
      />
    </Shell>
  );
}

/** A value picked, as a chip in the field: the value, a mark when the column holds no row of it, and a button that drops it. */
function ValueChip({ value, unknown, column, onDrop, testId }: {
  value: string;
  unknown: boolean;
  column: string;
  onDrop: () => void;
  testId: string;
}) {
  return (
    <span
      className="inline-flex h-5 max-w-[10rem] shrink-0 items-center gap-0.5 rounded border bg-muted/60 pr-0.5 pl-1.5 font-mono text-[11.5px]"
      data-testid={testId}
      data-value={value}
    >
      {unknown && (
        <span className="flex shrink-0 text-warning" title={`${column} holds no row with this value`}>
          <TriangleAlert className="size-3" />
        </span>
      )}
      <span className="min-w-0 truncate" title={value}>{value}</span>
      <button
        type="button"
        className="rounded-sm p-0.5 text-muted-foreground hover:bg-accent hover:text-foreground"
        onClick={onDrop}
        aria-label={`Drop ${value}`}
      >
        <X className="size-3" />
      </button>
    </span>
  );
}

/**
 * The values of a parameter the scope reads: the values picked, as chips, then the value being typed, which counts as one
 * of them as it stands. The list under the field offers the values the column holds, filtered by what is typed; picking
 * one adds it (or drops it, when it is picked already) and leaves the list open for the next. Enter adds what is typed,
 * and Backspace in an empty field drops the last value. A value the complete list does not hold is marked once the list
 * is closed, since the scope would read no row of it.
 */
function ScopeValuesInput({
  id, column, value, onChange, placeholder, listed, loading, error, label, title, className,
}: {
  id: string;
  column: string;
  /** Every value the field holds: those picked, then the one being typed, if any. */
  value: string[];
  onChange: (value: string[]) => void;
  placeholder: string;
  listed: DeliveryScopeParameterValues | undefined;
  loading: boolean;
  error: string | null;
  label: ReactNode | null;
  title?: string;
  className?: string;
}) {
  const [open, setOpen] = useState(false);
  const [text, setText] = useState("");
  const anchorRef = useRef<HTMLDivElement>(null);
  const inputRef = useRef<HTMLInputElement>(null);
  // What is typed is the last of the values the page holds, so the values picked are the ones before it. Values the page
  // replaced from outside (another interface's scope, say) no longer end with it, and the text goes with them.
  const held = text.trim() === "" || (value.length > 0 && value[value.length - 1] === text.trim());
  if (!held) {
    setText("");
  }

  const typed = held ? text.trim() : "";
  const picked = typed !== "" ? value.slice(0, -1) : value;
  const options = listed?.values ?? [];
  const exact = options.some((option) => option.value === typed);
  const filter = typed.toLowerCase();
  const matches = typed === "" || exact ? options : options.filter((option) => option.value.toLowerCase().includes(filter));
  const complete = listed !== undefined && !listed.more;
  const isUnknown = (candidate: string) => complete && !options.some((option) => option.value === candidate);
  // Said once the list is closed: while it is open, the list itself shows what the text matches.
  const unknownTyped = !open && typed !== "" && isUnknown(typed);
  const unknownPicked = picked.filter(isUnknown);
  const unknown = unknownTyped || unknownPicked.length > 0;
  const unknownNamed = [...unknownPicked, ...(unknownTyped ? [typed] : [])];

  const report = (nextPicked: readonly string[], nextText: string) => {
    const pending = nextText.trim();
    onChange(pending === "" ? [...nextPicked] : [...nextPicked, pending]);
  };
  const add = (candidate: string) => (picked.includes(candidate) ? picked : [...picked, candidate]);
  const toggle = (candidate: string) => {
    const next = picked.includes(candidate) ? picked.filter((other) => other !== candidate) : [...picked, candidate];
    setText("");
    report(next, "");
    inputRef.current?.focus();
  };
  const drop = (candidate: string) => report(picked.filter((other) => other !== candidate), text);

  // A form lists every value picked; a toolbar keeps to one line, so it shows one token, the first value and how many
  // more, each named on hover, and leaves the value being typed its room. The list drops a value there.
  const inline = label !== null;
  const chips = inline
    ? picked.length > 0 && (
      <span
        className="inline-flex h-5 min-w-0 items-center gap-1 rounded border bg-muted/60 px-1.5 font-mono text-[11.5px]"
        title={picked.join("\n")}
        data-testid={`${id}-picked`}
        data-value={picked.join("\n")}
      >
        <span className="min-w-0 truncate">{picked[0]}</span>
        {picked.length > 1 && <span className="shrink-0 text-muted-foreground" data-testid={`${id}-more`}>{`+${picked.length - 1}`}</span>}
      </span>
    )
    : picked.map((chip) => (
      <ValueChip key={chip} value={chip} unknown={isUnknown(chip)} column={column} onDrop={() => drop(chip)} testId={`${id}-picked`} />
    ));
  const icon = (
    <span className="pointer-events-none flex shrink-0 items-center pr-2.5 text-muted-foreground">
      {loading ? <Loader2 className="size-4 animate-spin" /> : <ChevronsUpDown className="size-4 opacity-60" />}
    </span>
  );
  const inputProps = {
    ref: inputRef,
    id,
    role: "combobox",
    "aria-expanded": open,
    "aria-autocomplete": "list" as const,
    value: text,
    onChange: (event: ChangeEvent<HTMLInputElement>) => {
      setText(event.target.value);
      report(picked, event.target.value);
      setOpen(true);
    },
    onClick: () => setOpen(true),
    onKeyDown: (event: KeyboardEvent<HTMLInputElement>) => {
      if (event.key === "ArrowDown") {
        setOpen(true);
      } else if (event.key === "Escape") {
        setOpen(false);
      } else if (event.key === "Enter" && typed !== "") {
        // Enter adds the value typed; with nothing typed it is the form's, as it was.
        event.preventDefault();
        setText("");
        report(add(typed), "");
      } else if (event.key === "Backspace" && text === "" && picked.length > 0) {
        report(picked.slice(0, -1), "");
      }
    },
    placeholder: picked.length === 0 ? placeholder : "",
    spellCheck: false,
    autoComplete: "off",
    "data-testid": id,
  };

  return (
    <div className={cn("flex flex-col gap-1", !inline && className)}>
      <Popover open={open} onOpenChange={setOpen}>
        <PopoverAnchor asChild>
          <div ref={anchorRef}>
            {!inline
              ? (
                <div
                  className={cn(
                    "relative flex min-h-8 w-full flex-wrap items-center gap-1 py-[5px] pr-8 pl-1.5", SURFACE,
                    "focus-within:border-ring focus-within:ring-[3px] focus-within:ring-ring/50",
                    unknown && "border-warning",
                  )}
                >
                  {chips}
                  <input {...inputProps} className="h-5 min-w-[6rem] flex-1 bg-transparent px-0.5 font-mono text-[12px] outline-none placeholder:text-muted-foreground" />
                  <span className="absolute top-[7px] right-0">{icon}</span>
                </div>
              )
              : (
                <Shell id={id} label={label} title={title} className={className} invalid={unknown}>
                  {picked.length > 0 && <div className="flex min-w-0 max-w-[60%] items-center pl-1.5">{chips}</div>}
                  <input {...inputProps} className={cn(BARE, "min-w-12")} />
                  {/* A toolbar keeps to one line, so a value the column holds no row of is flagged inside the field. */}
                  {unknown && (
                    <span className="flex shrink-0 text-warning" title={`${column} holds no row with ${unknownNamed.join(", ")}`} data-testid={`${id}-unknown`}>
                      <TriangleAlert className="size-3.5" />
                    </span>
                  )}
                  {icon}
                </Shell>
              )}
          </div>
        </PopoverAnchor>
        <PopoverContent
          className="w-[max(var(--radix-popover-trigger-width),16rem)] p-0"
          align="start"
          onOpenAutoFocus={(event) => event.preventDefault()}
          onInteractOutside={(event) => {
            // A click back into the field must not dismiss the list it anchors.
            if (event.target instanceof Node && anchorRef.current?.contains(event.target)) {
              event.preventDefault();
            }
          }}
          data-testid={`${id}-values`}
        >
          <Command shouldFilter={false}>
            <CommandList>
              <CommandEmpty>
                {loading
                  ? `Reading the values of ${column}...`
                  : error !== null
                    ? `The values of ${column} could not be read: ${error}`
                    : options.length === 0 ? `${column} holds no value.` : `No value of ${column} holds "${typed}".`}
              </CommandEmpty>
              {matches.map((option) => {
                const chosen = picked.includes(option.value) || option.value === typed;
                return (
                  <CommandItem
                    key={option.value}
                    value={option.value}
                    onSelect={() => toggle(option.value)}
                    className="flex items-center gap-2"
                    data-testid={`${id}-value`}
                    data-picked={chosen ? "true" : "false"}
                  >
                    <Check className={cn("size-3.5 shrink-0", chosen ? "opacity-100" : "opacity-0")} />
                    <span className={cn("min-w-0 flex-1 truncate font-mono text-[12px]", chosen && "font-semibold")}>{option.value}</span>
                    <span className="shrink-0 font-mono text-[11px] text-muted-foreground tabular-nums">{`${count.format(option.rows)} rows`}</span>
                  </CommandItem>
                );
              })}
            </CommandList>
          </Command>
          <p className="border-t px-2 py-1.5 text-[11px] text-muted-foreground">
            {listed?.more === true
              ? `${column} holds more values than the ${count.format(options.length)} with the most rows listed here; type one to use it. Pick several to read the rows of each.`
              : "Pick several to read the rows of each; Enter adds the value typed."}
          </p>
        </PopoverContent>
      </Popover>
      {unknown && !inline && (
        <p className="flex items-center gap-1 text-[11px] text-warning" data-testid={`${id}-unknown`}>
          <TriangleAlert className="size-3.5 shrink-0" />
          {`${column} holds no row with ${unknownNamed.join(", ")}`}
        </p>
      )}
    </div>
  );
}
