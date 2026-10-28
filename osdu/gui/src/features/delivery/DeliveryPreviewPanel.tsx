import { useEffect, useMemo, useRef, useState } from "react";
import { useSearchParams } from "react-router-dom";
import { useMutation } from "@tanstack/react-query";
import { Eye, Loader2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import type { RunValue } from "@/api/types";
import { useAuth } from "@/auth/AuthContext";
import { ExplainMark } from "@/components/ExplainTip";
import { RichTooltip } from "@/components/RichTooltip";
import { RunParameterFields } from "@/features/runs/RunParameterFields";
import { parametersFor, parameterValuesToSend, type ParameterFieldValues } from "@/features/runs/runInputs";
import { usePipelineParameters, useRunExplanation } from "@/features/runs/useRunInputs";
import { deliveryApi, type DeliveryRecordPreview } from "../../api/delivery";
import { InterfacePicker } from "./InterfacePicker";
import { OutsidePartition } from "./PartitionNotice";
import { useInterfaceChoice } from "./useInterfaceChoice";
import { RecordPreviewView } from "./RecordPreviewView";
import { ProblemView, TaskProgress } from "./TemplateSheet";

/** The longest key the control plane takes; a longer text is a paste of something else. */
const MAX_KEY = 4000;

/** A preview is explained as a plan with no payload: it reads and renders one row as a plan would. */
const NO_PAYLOAD: Readonly<Record<string, unknown>> = {};

/** What a preview does and what its key field takes, behind the bar's info mark rather than above the bar. */
function howItWorks(keyColumns: readonly string[]): string {
  const keyed = keyColumns.length > 0 ? ` The records are keyed by ${keyColumns.join(", ")}.` : "";
  return [
    "Renders one record exactly as a delivery would, on a node, with the flow's mapping, cache and route. Nothing is sent, and the ledger and the work location are left as they are.",
    `Leave the key empty for the first record of the scope, or give a source key, a delivery key, an OSDU id the ledger holds, or the key's parts as ["part", "part"].${keyed}`,
    "The fields beside it are the flow's parameters, which fill its scope; one left empty takes the flow's default.",
  ].join("\n\n");
}

/**
 * The scope's values a link names (`previewValues`, a JSON object of strings, and of arrays of strings for a parameter
 * given several), as a value check's record links carry them; none when the link names none or names them in any other
 * form.
 */
function linkedValues(text: string | null): ParameterFieldValues {
  if (text === null) {
    return {};
  }

  try {
    const parsed: unknown = JSON.parse(text);
    return parsed !== null && typeof parsed === "object" && !Array.isArray(parsed)
      ? Object.fromEntries(Object.entries(parsed).filter((entry): entry is [string, string | string[]] => typeof entry[1] === "string"
        || (Array.isArray(entry[1]) && entry[1].every((item) => typeof item === "string"))))
      : {};
  } catch {
    return {};
  }
}

/**
 * The Preview tab of a delivery flow: one record rendered exactly as a delivery would render it, and sent
 * nowhere. The record is the scope's first, or the one a key names; the flow's parameters fill the scope, their defaults
 * where none is given, in the fields a run form gives them (as the flow's document declares them), and the control plane
 * says what a plan given those values would refuse. What comes back is the record's document as its route sends it, what
 * the next run would do with it, the requests the route would make, the files it would upload and what it refers to.
 */
export function DeliveryPreviewPanel({ pipelineId, flowName }: { pipelineId: string; flowName: string }) {
  const { hasScope } = useAuth();
  const canOperate = hasScope("operate");
  const {
    interfaces, names, many, interfaceName, current, partitions, headerPartition, outside, unplaced, active, scope, selectInterface,
  } = useInterfaceChoice(pipelineId);
  const inputs = usePipelineParameters(pipelineId);
  const parameters = useMemo(() => parametersFor(inputs.data?.inputs, "plan"), [inputs.data]);
  const keyColumns = current?.keyColumns ?? [];
  // A link to one record (a value check's) names its key and the scope's values, and the record is previewed on arrival.
  const [searchParams, setSearchParams] = useSearchParams();
  const linkedKey = searchParams.get("previewKey");
  const [key, setKey] = useState(linkedKey ?? "");
  const [values, setValues] = useState<ParameterFieldValues>(() => linkedValues(searchParams.get("previewValues")));
  // What the last preview answered: kept while the next one runs, so the page does not jump, and replaced when the next
  // one lands. A preview that fails says why in its place.
  const [result, setResult] = useState<DeliveryRecordPreview | null>(null);

  // Another interface is another table and another scope, and another partition another cache and target: its preview
  // starts afresh.
  const shownFor = current?.flowId ?? null;
  const [previewedFor, setPreviewedFor] = useState<string | null>(shownFor);
  if (shownFor !== previewedFor) {
    // The flow first shown keeps what a link gave; another interface or partition starts afresh.
    if (previewedFor !== null) {
      setValues({});
    }

    setPreviewedFor(shownFor);
    setResult(null);
  }

  // A parameter left empty takes its declared default on the node.
  const sent = useMemo(() => parameterValuesToSend(parameters, values), [parameters, values]);
  const preview = useMutation({
    mutationFn: () => deliveryApi.preview(pipelineId, { key: key.trim() === "" ? null : key.trim(), values: sent }, scope),
    onSuccess: setResult,
    onError: () => setResult(null),
  });

  // What a plan given these values would refuse, as the control plane checks it: a required value missing, a value outside
  // a parameter's choices.
  const explainedValues = useMemo<Record<string, RunValue>>(
    () => (scope.partition ? { ...sent, partition: scope.partition } : sent),
    [sent, scope.partition],
  );
  const { parameterProblems, inputProblems, refused } = useRunExplanation(
    pipelineId, "plan", explainedValues, NO_PAYLOAD, current !== undefined && parameters.length > 0);
  const tooLong = key.trim().length > MAX_KEY;
  const running = preview.isPending;
  const blocked = !canOperate || current === undefined || refused || tooLong || running;
  // What the button does, or why it cannot: on hover, since a disabled button says nothing of itself.
  const runWhy = !canOperate
    ? "A preview reads the flow's tables and renders with its credentials, which takes a session allowed to operate."
    : running
      ? "Rendering the record."
      : refused || tooLong
        ? "The line under the bar says what to change first."
        : "Render the record as a delivery would. Nothing is sent.";
  const problems = [
    ...Object.entries(parameterProblems).map(([name, problem]) => ({ key: name, text: problem, testId: `preview-parameter-${name}-problem` })),
    ...inputProblems.map((problem) => ({ key: problem, text: problem, testId: "preview-problem" })),
    ...(tooLong ? [{ key: "too-long", text: `The key is ${key.trim().length.toLocaleString()} characters; a record key is at most ${MAX_KEY.toLocaleString()}.`, testId: "preview-key-too-long" }] : []),
  ];
  const submit = () => {
    if (!blocked) {
      preview.mutate();
    }
  };

  // A linked record is previewed once, as soon as the interface and the scope's values let it be; the link's parameters
  // then leave the address, so coming back to the tab does not preview it again.
  const linkedRun = useRef(false);
  const { mutate } = preview;
  useEffect(() => {
    if (linkedKey === null || linkedRun.current || blocked || current === undefined) {
      return;
    }

    linkedRun.current = true;
    mutate();
    setSearchParams((existing) => {
      const next = new URLSearchParams(existing);
      next.delete("previewKey");
      next.delete("previewValues");
      return next;
    }, { replace: true });
  }, [linkedKey, blocked, current, mutate, setSearchParams]);

  if (interfaces.isError) {
    return <ProblemView error={interfaces.error} testId="preview-interfaces-error" />;
  }

  // A preview renders with the partition's cache, ids and configuration: the title bar's, which this flow must serve.
  if (outside !== null || unplaced) {
    return (
      <div className="flex flex-col gap-4" data-testid="delivery-panel-preview">
        <OutsidePartition flowName={flowName} active={active} partitions={partitions} headerPartition={headerPartition} />
      </div>
    );
  }

  return (
    <div className="flex flex-col gap-3" data-testid="delivery-panel-preview">
      {many && (
        <InterfacePicker names={names} interfaceName={interfaceName} onSelect={selectInterface} caption={`of ${names.length} interfaces of ${flowName}`} />
      )}

      <Card className="gap-1.5 rounded-lg px-3 py-2.5" data-testid="preview-form-card">
        {current === undefined
          ? <Skeleton className="h-8 w-full rounded-md" />
          : (
            <form
              className="flex flex-wrap items-center gap-2"
              onSubmit={(event) => { event.preventDefault(); submit(); }}
              data-testid="preview-form"
            >
              <Input
                id="preview-key"
                value={key}
                onChange={(event) => setKey(event.target.value)}
                placeholder="First record, or a key or OSDU id"
                aria-label={keyColumns.length > 0 ? `Record key (${keyColumns.join(", ")})` : "Record key"}
                className="h-8 min-w-48 flex-1 basis-64 font-mono text-[12px]"
                spellCheck={false}
                autoComplete="off"
                aria-invalid={tooLong}
                data-testid="preview-key"
              />
              {parameters.length > 0 && (
                <RunParameterFields
                  pipelineId={pipelineId}
                  parameters={parameters}
                  values={values}
                  onChange={(name, value) => setValues((was) => ({ ...was, [name]: value }))}
                  lookupValues={scope.partition ? { partition: scope.partition } : undefined}
                  prefix="preview"
                  layout="inline"
                  problems={parameterProblems}
                  className="w-52"
                />
              )}
              <RichTooltip body={runWhy} delayDuration={300}>
                {/* A disabled button takes no hover, so its wrapper carries the reason it cannot run. */}
                <span className="inline-flex">
                  <Button type="submit" size="sm" className="h-8" disabled={blocked} data-testid="preview-run">
                    {running ? <Loader2 className="animate-spin" /> : <Eye />}
                    Preview
                  </Button>
                </span>
              </RichTooltip>
              <ExplainMark title="What a preview does" text={howItWorks(keyColumns)} testId="preview-how" />
            </form>
          )}
        {problems.length > 0 && (
          <ul className="flex flex-wrap gap-x-4 gap-y-0.5 text-[12px] text-destructive" data-testid="preview-problems">
            {problems.map((problem) => <li key={problem.key} data-testid={problem.testId}>{problem.text}</li>)}
          </ul>
        )}
      </Card>

      {preview.isError && <ProblemView error={preview.error} testId="preview-error" />}
      {running && <TaskProgress label="Rendering the record" testId="preview-progress" />}
      {result !== null && <RecordPreviewView preview={result} />}
    </div>
  );
}
