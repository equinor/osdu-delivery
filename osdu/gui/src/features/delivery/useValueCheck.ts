import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState } from "react";
import { useMutation, useQueries, useQuery } from "@tanstack/react-query";
import { toast } from "sonner";
import { isApiError } from "@/api/client";
import type { ComputeTask, DeclaredRunParameter, RunValue } from "@/api/types";
import { useAuth } from "@/auth/AuthContext";
import {
  deliveryApi,
  type DeliveryFlowScope,
  type DeliveryMappingFlow,
  type DeliveryValueCheck,
  type DeliveryValueCheckFinding,
  type DeliveryValueCheckRows,
  type DeliveryValueCheckVariable,
  type DeliveryValueIssuesExport,
} from "../../api/delivery";
import { useActivePartition } from "./activePartition";
import { workbookBlob } from "./checkedDocument";
import { computeTaskQuery, isTerminalTask, settledTask } from "./useComputeTask";
import {
  DEFAULT_ROW_BUDGET,
  SAMPLES_ALL,
  SAMPLES_ONE,
  SAMPLES_PAGE,
  assertedOf,
  assertionRowsLine,
  countOf,
  failingOf,
  findingKey,
  flowKey,
  flowLabel,
  formatCount,
  within,
  type OutcomeKey,
} from "./valueCheck";
import { parametersFor, parameterValuesToSend, type ParameterFieldValues } from "@/features/runs/runInputs";
import { usePipelineParameters, useRunExplanation } from "@/features/runs/useRunInputs";

/** Which variables the tree narrows to after a check: any that fails (an assertion included), or those failing one way. */
export type FailingFilter = "any" | "held" | "invalid" | "empty" | "asserted";

/** What each narrowing reads in a variable's rows and findings. */
const FILTER_KINDS: Record<FailingFilter, readonly OutcomeKey[]> = {
  any: ["held", "invalid", "empty", "asserted"],
  held: ["held"],
  invalid: ["invalid"],
  empty: ["empty"],
  asserted: ["asserted"],
};

/** What a check was run against and what it read, which every result it answered with carries. */
export interface CheckMeta {
  taskId: string;
  flow: DeliveryMappingFlow;
  /** The variables it was asked for; null for every variable of the mapping. */
  targets: string[] | null;
  rows: DeliveryValueCheckRows;
  values: Record<string, RunValue>;
  maxRows: number;
  cacheVersion: string | null;
  notes: string[];
  issues: string[];
  checkedUtc: string;
}

/** One variable as the latest check of it found it. */
export interface VariableCheck {
  variable: DeliveryValueCheckVariable;
  meta: CheckMeta;
}

/** A finding about a variable found while checking another: a property inside a value written whole. */
export interface NestedFinding {
  finding: DeliveryValueCheckFinding;
  from: VariableCheck;
}

/** The record the Document tab shows by the reader's choice, with the scope's values its row is read with. */
export interface PickedRecord {
  key: string;
  values: Record<string, RunValue>;
}

/** A check queued on a node and not yet taken in. */
interface PendingTask {
  taskId: string;
  flowKey: string;
  flow: DeliveryMappingFlow;
  targets: string[] | null;
  /** For the next page of one finding: the variable, and the finding's key. */
  page: { target: string; finding: string } | null;
  values: Record<string, RunValue>;
  maxRows: number;
}

interface Completion {
  taskId: string;
  ok: boolean;
  text: string;
}

interface Store {
  flowKey: string | null;
  variables: ReadonlyMap<string, VariableCheck>;
  /** The latest check of the flow, of whatever it was asked for. */
  last: CheckMeta | null;
  /** The latest check of every variable, which the bar's summary is of while there is one. */
  lastAll: CheckMeta | null;
  /** Why the latest check that failed did; cleared by the next one that answers. */
  failure: string | null;
  /** What finished, for the toasts; the last few only. */
  completions: readonly Completion[];
  /** The tasks already taken in, so one taken in twice (a render React repeats) changes nothing the second time. */
  done: ReadonlySet<string>;
}

function emptyStore(key: string | null): Store {
  return { flowKey: key, variables: new Map(), last: null, lastAll: null, failure: null, completions: [], done: new Set() };
}

/** How long the toast line list is kept: long enough that no completion is lost between two renders. */
const MAX_COMPLETIONS = 20;

/** A value check gives the plan it is explained as no payload. */
const NO_PAYLOAD: Readonly<Record<string, unknown>> = {};

/** Takes in a task a node finished: its variables merged, or one finding's next examples appended, with a line for the toast. */
function taken(store: Store, task: PendingTask, settled: ComputeTask | undefined, error: unknown): Store {
  if (store.done.has(task.taskId)) {
    return store;
  }

  store = { ...store, done: new Set([...store.done, task.taskId]) };
  const completions = (completion: Completion) => [...store.completions, completion].slice(-MAX_COMPLETIONS);
  if (settled === undefined || settled.status !== "succeeded" || settled.result === null || settled.result === undefined) {
    const why = settled?.error
      ?? (error !== undefined ? (isApiError(error) ? error.detail ?? error.title : String(error)) : null)
      ?? (settled?.status === "cancelled" ? "The check was cancelled before a node finished it." : "The check ended without an answer.");
    return { ...store, failure: why, completions: completions({ taskId: task.taskId, ok: false, text: `The value check could not be made: ${why}` }) };
  }

  const check = settled.result as DeliveryValueCheck;
  const meta: CheckMeta = {
    taskId: task.taskId,
    flow: task.flow,
    targets: task.targets,
    rows: check.rows,
    values: task.values,
    maxRows: task.maxRows,
    cacheVersion: check.inputs.cacheVersion ?? null,
    notes: check.notes,
    issues: check.issues,
    checkedUtc: check.checkedUtc,
  };

  if (task.page !== null) {
    const page = task.page;
    const current = store.variables.get(page.target);
    const fresh = check.variables.find((variable) => variable.target === page.target)?.findings.find((finding) => findingKey(finding) === page.finding);
    if (current === undefined || fresh === undefined) {
      return { ...store, completions: completions({ taskId: task.taskId, ok: true, text: `No more rows of ${page.target} to list: the rows changed since they were checked. Check it again.` }) };
    }

    const variable: DeliveryValueCheckVariable = {
      ...current.variable,
      findings: current.variable.findings.map((finding) => (findingKey(finding) === page.finding
        ? { ...finding, count: fresh.count, rows: fresh.rows, samples: [...finding.samples, ...fresh.samples] }
        : finding)),
    };
    const variables = new Map(store.variables);
    variables.set(page.target, { ...current, variable });
    return {
      ...store,
      variables,
      failure: null,
      completions: completions({ taskId: task.taskId, ok: true, text: `Listed ${formatCount(fresh.samples.length)} more rows of ${page.target}.` }),
    };
  }

  const variables = new Map(store.variables);
  for (const variable of check.variables) {
    variables.set(variable.target, { variable, meta });
  }

  const rows = check.rows;
  const asked = task.targets === null ? [] : check.variables.filter((variable) => task.targets!.includes(variable.target));
  const asserted = asked.reduce((sum, variable) => sum + assertedOf(variable.rows), 0);
  const assertedRows = assertionRowsLine(rows);
  const text = task.targets === null
    ? `Checked ${formatCount(rows.checked)} rows of ${flowLabel(task.flow)}: ${formatCount(rows.withHeld)} held, ${formatCount(rows.withInvalid)} with an invalid value, ${formatCount(rows.withEmpty)} leaving an attribute out${assertedRows === null ? "" : `; ${assertedRows}`}.`
    : `Checked ${task.targets.join(", ")} over ${formatCount(rows.checked)} rows: ${formatCount(asked
      .reduce((sum, variable) => sum + failingOf(variable.rows), 0))} rows will not give it an expected value${asserted > 0 ? `, ${formatCount(asserted)} fail an assertion of the mapping` : ""}.`;
  return {
    ...store,
    variables,
    last: meta,
    lastAll: task.targets === null ? meta : store.lastAll,
    failure: null,
    completions: completions({ taskId: task.taskId, ok: true, text }),
  };
}

/** Everything the mapping sheet's Properties tab needs to check values and show what a check found. */
export interface ValueCheckSession {
  /** The flows that render with the mapping in the title bar's partition, and the partitions others deliver to. */
  flows: DeliveryMappingFlow[];
  otherPartitions: string[];
  flowsLoading: boolean;
  flowsError: unknown;
  /** The flow whose rows a check reads, and choosing another. */
  flow: DeliveryMappingFlow | null;
  chooseFlow: (key: string) => void;
  /** The flow's parameters a plan resolves (as its document declares them), the values given, and giving one. */
  parameters: DeclaredRunParameter[];
  keyColumns: string[];
  values: ParameterFieldValues;
  setValue: (name: string, value: RunValue | undefined) => void;
  /** What refuses each parameter's value, as the control plane explains the values given. */
  parameterProblems: Readonly<Record<string, string>>;
  /** The rows a check reads; 0 for the whole scope. */
  maxRows: number;
  setMaxRows: (rows: number) => void;
  /** Whether the reader may queue node work: a check, and the read of the scope's values. */
  canOperate: boolean;
  /** Whether a check can be run now, and why not when it cannot. */
  ready: boolean;
  blocked: string | null;
  /** Queues a check of the variables named, or of every variable when null. */
  check: (targets: string[] | null) => void;
  /** Queues the next page of examples of a finding of a variable. */
  more: (target: string, finding: DeliveryValueCheckFinding) => void;
  /** True while a check reaching the variable is queued or running. */
  isChecking: (path: string) => boolean;
  /** True while the next page of the finding is being read. */
  isPaging: (target: string, finding: DeliveryValueCheckFinding) => boolean;
  /** True while any check of every variable is queued or running. */
  checkingAll: boolean;
  /** How many checks are queued or running. */
  running: number;
  /** The latest check's answer about a variable, and what checks of others found at it. */
  resultFor: (path: string) => VariableCheck | undefined;
  nestedFor: (path: string) => NestedFinding[];
  /** The checked variables that lie inside a variable (a group filled by what it holds). */
  inside: (path: string) => VariableCheck[];
  /** Every variable checked, in the order a check answered them. */
  checked: VariableCheck[];
  /** The latest check, of whatever it was asked for. */
  last: CheckMeta | null;
  /** What the bar sums up: the latest check of every variable, else the latest check. */
  summary: CheckMeta | null;
  failure: string | null;
  /** Narrowing the tree to the variables that fail. */
  filter: FailingFilter | null;
  setFilter: (filter: FailingFilter | null) => void;
  /** Whether a check found rows of the variable failing the way named, itself or inside a value another writes whole. */
  fails: (path: string, how: FailingFilter) => boolean;
  /** A request to show one variable in the tree, from outside it. */
  focus: { path: string; nonce: number } | null;
  requestFocus: (path: string) => void;
  /** The scope's values as a read of the flow's rows is given them now. */
  sentValues: Record<string, RunValue>;
  /** The record the Document tab shows by the reader's choice; none shows the one the attribute leads to. */
  pickedRecord: PickedRecord | null;
  pickRecord: (record: PickedRecord | null) => void;
  /** Runs the check of one attribute again on a node, over the rows it read, and answers with the workbook of the rows that fail it. */
  exportIssues: (check: VariableCheck) => Promise<Blob>;
}

export const ValueCheckContext = createContext<ValueCheckSession | null>(null);

/** The value check of the mapping sheet being read, or null outside one (the Templates page reads the same tree). */
export function useValueCheckContext(): ValueCheckSession | null {
  return useContext(ValueCheckContext);
}

/**
 * The value check of one mapping document: the flows that render with it in the title bar's partition (a check reads a
 * flow's rows), the scope's values and the row budget, the checks queued on nodes and polled to their end, and what they
 * found, merged variable by variable so a check of one variable refines what a check of every variable found. Choosing
 * another flow starts afresh, since its rows are others.
 */
export function useValueCheckSession(mappingId: string, reference: string): ValueCheckSession {
  const { hasScope } = useAuth();
  const canOperate = hasScope("operate");
  const [active] = useActivePartition();
  const flowsQuery = useQuery({
    queryKey: ["delivery", "mapping-flows", mappingId],
    queryFn: () => deliveryApi.mappingFlows(mappingId),
    staleTime: 30000,
  });
  const flows = useMemo(
    () => (flowsQuery.data ?? []).filter((flow) => flow.partition === null || flow.partition === active),
    [flowsQuery.data, active]);
  const otherPartitions = useMemo(
    () => [...new Set((flowsQuery.data ?? []).map((flow) => flow.partition).filter((p): p is string => p !== null && p !== active))],
    [flowsQuery.data, active]);

  const [picked, setPicked] = useState<string | null>(null);
  const flow = flows.find((candidate) => flowKey(candidate) === picked) ?? flows[0] ?? null;
  const currentKey = flow === null ? null : flowKey(flow);

  // The record key's columns are the interface's, as the flow's own pages list them.
  const interfaces = useQuery({
    queryKey: ["delivery", "interfaces", flow?.pipelineId ?? null, flow?.partition ?? null],
    queryFn: () => deliveryApi.interfaces(flow!.pipelineId, flow!.partition),
    enabled: flow !== null,
    staleTime: 30000,
  });
  const described = (interfaces.data ?? []).find((row) => (row.interface ?? null) === (flow?.interface ?? null)
    && (row.partition ?? null) === (flow?.partition ?? null));
  const keyColumns = described?.keyColumns ?? [];

  // The parameters are the flow's document's, as a run form gives them: a check reads and renders rows as a plan does, so
  // its values are the ones a plan resolves, checked by the control plane as it would check the plan.
  const inputs = usePipelineParameters(flow?.pipelineId ?? null);
  const parameters = useMemo(() => parametersFor(inputs.data?.inputs, "plan"), [inputs.data]);
  const [values, setValues] = useState<ParameterFieldValues>({});
  const sent = useMemo(() => parameterValuesToSend(parameters, values), [parameters, values]);
  const partition = flow?.partition ?? null;
  const explainedValues = useMemo<Record<string, RunValue>>(
    () => (partition !== null ? { ...sent, partition } : sent),
    [sent, partition],
  );
  const { parameterProblems, inputProblems, refused, explained } = useRunExplanation(
    flow?.pipelineId ?? null, "plan", explainedValues, NO_PAYLOAD, flow !== null && parameters.length > 0);
  const [maxRows, setMaxRows] = useState(DEFAULT_ROW_BUDGET);
  const [filter, setFilter] = useState<FailingFilter | null>(null);
  const [focus, setFocus] = useState<{ path: string; nonce: number } | null>(null);
  const [store, setStore] = useState<Store>(() => emptyStore(null));
  const [tasks, setTasks] = useState<PendingTask[]>([]);
  const [pickedRecord, pickRecord] = useState<PickedRecord | null>(null);

  // Another flow is other rows: what was found in the one before no longer describes what is shown.
  if (store.flowKey !== currentKey) {
    setStore(emptyStore(currentKey));
    setTasks([]);
    setFilter(null);
    pickRecord(null);
  }

  const polled = useQueries({ queries: tasks.map((task) => computeTaskQuery(task.taskId)) });

  // A task a node finished is taken in once: merged, dropped from the ones polled, and kept for its toast. One per render;
  // the render it causes takes the next.
  const settledAt = polled.findIndex((query) => query.isError || (query.data !== undefined && isTerminalTask(query.data)));
  if (settledAt >= 0 && tasks[settledAt] !== undefined) {
    const task = tasks[settledAt];
    const query = polled[settledAt];
    setTasks((current) => current.filter((pending) => pending.taskId !== task.taskId));
    if (task.flowKey === store.flowKey) {
      setStore((current) => taken(current, task, query.data, query.isError ? query.error : undefined));
    }
  }

  // Every check that ends says so, including one that ends while the reader is looking elsewhere (DESIGN.md 8.2).
  const toasted = useRef(new Set<string>());
  useEffect(() => {
    for (const completion of store.completions) {
      if (!toasted.current.has(completion.taskId)) {
        toasted.current.add(completion.taskId);
        if (completion.ok) {
          toast.success(completion.text);
        } else {
          toast.error(completion.text);
        }
      }
    }
  }, [store.completions]);

  const queue = useMutation({
    mutationFn: (pending: Omit<PendingTask, "taskId"> & { samples: number; skipSamples: number }) => deliveryApi.checkValues(
      pending.flow.pipelineId,
      {
        targets: pending.targets ?? undefined,
        values: pending.values,
        maxRows: pending.maxRows,
        samples: pending.samples,
        skipSamples: pending.skipSamples,
        mapping: reference,
      },
      { interfaceName: pending.flow.interface, partition: pending.flow.partition }),
    onSuccess: (accepted, pending) => setTasks((current) => [...current, {
      taskId: accepted.taskId,
      flowKey: pending.flowKey,
      flow: pending.flow,
      targets: pending.targets,
      page: pending.page,
      values: pending.values,
      maxRows: pending.maxRows,
    }]),
    onError: (error) => toast.error(`The value check could not be queued: ${isApiError(error) ? error.detail ?? error.title : String(error)}`),
  });

  const { mutate } = queue;
  let blocked: string | null = null;
  if (!canOperate) {
    blocked = "A check runs on a node, which takes a signed-in session.";
  } else if (flowsQuery.isPending) {
    blocked = "Finding the flows that render with this mapping.";
  } else if (flow === null) {
    blocked = otherPartitions.length > 0
      ? `The flows rendering with this mapping deliver to partition ${otherPartitions.join(", ")}; pick it in the title bar to check their rows.`
      : "No flow renders with this mapping yet, so there are no rows to check.";
  } else if (interfaces.isPending || inputs.isPending) {
    blocked = "Reading what the flow's scope needs.";
  } else if (refused) {
    blocked = Object.values(parameterProblems)[0] ?? inputProblems[0] ?? explained.data?.problems[0] ?? null;
  }

  const check = useCallback((targets: string[] | null) => {
    if (blocked !== null || flow === null || currentKey === null) {
      return;
    }

    mutate({
      flowKey: currentKey,
      flow,
      targets,
      page: null,
      values: sent,
      maxRows,
      samples: targets === null ? SAMPLES_ALL : SAMPLES_ONE,
      skipSamples: 0,
    });
  }, [blocked, flow, currentKey, mutate, sent, maxRows]);

  const more = useCallback((target: string, finding: DeliveryValueCheckFinding) => {
    const current = store.variables.get(target);
    if (!canOperate || flow === null || currentKey === null || current === undefined) {
      return;
    }

    // The next page reads the rows the first did: the same scope's values and the same row budget.
    mutate({
      flowKey: currentKey,
      flow,
      targets: [target],
      page: { target, finding: findingKey(finding) },
      values: current.meta.values,
      maxRows: current.meta.maxRows,
      samples: SAMPLES_PAGE,
      skipSamples: finding.samplesFrom + finding.samples.length,
    });
  }, [canOperate, flow, currentKey, mutate, store.variables]);

  const exportIssues = useCallback(async ({ variable, meta }: VariableCheck) => {
    const accepted = await deliveryApi.exportValueIssues(
      meta.flow.pipelineId,
      { targets: [variable.target], values: meta.values, maxRows: meta.maxRows, mapping: reference },
      flowScope(meta.flow));
    const task = await settledTask(accepted.taskId);
    if (task.status !== "succeeded" || task.result === null || task.result === undefined) {
      throw new Error(task.error ?? (task.status === "cancelled" ? "The export was cancelled before a node finished it." : "The export ended without an answer."));
    }

    const exported = task.result as DeliveryValueIssuesExport;
    if (exported.listed < exported.issues) {
      toast.warning(`The workbook lists the first ${formatCount(exported.listed)} of ${formatCount(exported.issues)} issues, which is all a file from a node can hold. Its counts are whole; read fewer rows to list the rest.`);
    }

    return workbookBlob(exported.workbook);
  }, [reference]);

  const checked = useMemo(() => [...store.variables.values()], [store.variables]);

  const resultFor = useCallback((path: string) => store.variables.get(path), [store.variables]);
  const nestedFor = useCallback((path: string) => checked
    .filter((result) => result.variable.target !== path)
    .flatMap((result) => result.variable.findings.filter((finding) => finding.at === path).map((finding) => ({ finding, from: result }))), [checked]);
  const inside = useCallback((path: string) => checked
    .filter((result) => result.variable.target !== path && within(result.variable.target, path)), [checked]);

  const fails = useCallback((path: string, how: FailingFilter) => {
    const kinds = FILTER_KINDS[how];
    const own = store.variables.get(path);
    return (own !== undefined && kinds.some((kind) => countOf(own.variable.rows, kind) > 0))
      || checked.some((result) => result.variable.target !== path
        && result.variable.findings.some((finding) => finding.at === path && kinds.includes(finding.outcome)));
  }, [store.variables, checked]);

  const isChecking = useCallback((path: string) => tasks.some((task) => task.page === null
    && (task.targets === null || task.targets.some((target) => within(path, target) || within(target, path)))), [tasks]);
  const isPaging = useCallback((target: string, finding: DeliveryValueCheckFinding) => {
    const key = findingKey(finding);
    return tasks.some((task) => task.page?.target === target && task.page.finding === key);
  }, [tasks]);

  return {
    flows,
    otherPartitions,
    flowsLoading: flowsQuery.isPending,
    flowsError: flowsQuery.isError ? flowsQuery.error : null,
    flow,
    chooseFlow: setPicked,
    parameters,
    keyColumns,
    values,
    setValue: (name, value) => setValues((current) => ({ ...current, [name]: value })),
    parameterProblems,
    maxRows,
    setMaxRows,
    canOperate,
    ready: blocked === null,
    blocked,
    check,
    more,
    isChecking,
    isPaging,
    checkingAll: tasks.some((task) => task.page === null && task.targets === null) || (queue.isPending && queue.variables?.targets === null),
    running: tasks.length + (queue.isPending ? 1 : 0),
    resultFor,
    nestedFor,
    inside,
    checked,
    last: store.last,
    summary: store.lastAll ?? store.last,
    failure: store.failure,
    filter,
    setFilter,
    fails,
    focus,
    requestFocus: (path) => setFocus((current) => ({ path, nonce: (current?.nonce ?? 0) + 1 })),
    sentValues: sent,
    pickedRecord,
    pickRecord,
    exportIssues,
  };
}

/** The scope a flow's rows are read in, as the flow's own pages name it. */
export function flowScope(flow: DeliveryMappingFlow): DeliveryFlowScope {
  return { interfaceName: flow.interface, partition: flow.partition };
}
