// Runs a report's queries: each on its own, so a slow one never holds the others; again only when a value its statement
// binds changes; and never unless the page is open on it. An answer stays as it is until someone asks for it again
// (Refresh, or a changed value): nothing polls a database behind the reader's back.

import { useCallback, useMemo, useRef, useState } from "react";
import { keepPreviousData, useQueries, useQueryClient } from "@tanstack/react-query";
import { isApiError, type ApiError } from "../../api/client";
import { executeReportQuery, type ReportQueryRun } from "../../api/endpoints";
import type { Report, ReportQuery, RunQueryResult } from "../../api/types";
import { parseUtc } from "../../lib/time";
import { missingRequired, valuesFor, valuesKey, type GivenValues } from "./reportModel";

/** The root of every report query's cache key; with a report's id after it, the key of that report's answers. */
export const REPORT_QUERY_ROOT = "report-query";

/** Where one query of a report stands, and what the page shows for it. */
export interface QueryState {
  query: ReportQuery;
  /**
   * - `blocked`: the statement cannot run (it is not one read-only SELECT); `problem` says why.
   * - `waiting`: a required parameter the statement binds has no value yet; `waitingFor` names it.
   * - `running`: queued or on a worker, with no answer to show yet.
   * - `answered`: `answer` holds the rows. `refreshing` says a newer answer is on its way and this one is held meanwhile.
   * - `failed`: the worker or the control plane refused or failed it; `failure` says why.
   * - `cancelled`: someone stopped it before it answered.
   */
  status: "blocked" | "waiting" | "running" | "answered" | "failed" | "cancelled";
  answer: RunQueryResult | null;
  /** The run the shown answer or failure came from: what it was bound with, and the task that holds who asked and when. */
  run: ReportQueryRun | null;
  refreshing: boolean;
  problem: string | null;
  waitingFor: string[];
  failure: { message: string; error: ApiError | null } | null;
  /** When the run in flight was asked for, in epoch milliseconds; null when nothing is in flight. */
  askedAtMs: number | null;
  /** Runs the query again with the values in effect. */
  runAgain: () => void;
  /** Stops the run in flight, on the worker too. */
  cancel: () => void;
}

/** A report's queries as its page reads them. */
export interface ReportQueries {
  /** Every query's state by the query's name, hidden ones included (a parameter's choices are read from one). */
  states: ReadonlyMap<string, QueryState>;
  /** Whether any query is in flight. */
  running: boolean;
  /** When the newest shown answer was read, or null when none has arrived. */
  asOf: Date | null;
  /** Runs every query again. */
  refresh: () => void;
}

function isAnswer(result: unknown): result is RunQueryResult {
  return typeof result === "object" && result !== null && Array.isArray((result as RunQueryResult).columns)
    && Array.isArray((result as RunQueryResult).rows);
}

export function useReportQueries(report: Report, given: GivenValues): ReportQueries {
  const client = useQueryClient();
  // A cancelled run leaves no error behind (the request was simply aborted), so what was cancelled is remembered here,
  // by the key it ran under, until it is asked for again.
  const [cancelled, setCancelled] = useState<ReadonlySet<string>>(new Set());
  // When each run in flight was asked for, by its key: written as the request is made, read for the "running for"
  // a tile shows. A run's start is not part of what the query library keeps.
  const askedAt = useRef(new Map<string, number>());

  const plans = useMemo(() => report.queries.map((query) => {
    const values = valuesFor(query, given);
    const waitingFor = missingRequired(query, report.parameters, given);
    const key = [REPORT_QUERY_ROOT, report.id, query.name, valuesKey(values)] as const;
    return { query, values, waitingFor, key, keyText: key.join("|") };
  }), [report, given]);

  const results = useQueries({
    queries: plans.map((plan) => ({
      queryKey: plan.key,
      queryFn: ({ signal }: { signal: AbortSignal }) => {
        askedAt.current.set(plan.keyText, Date.now());
        return executeReportQuery(report.id, plan.query.name, plan.values, signal);
      },
      enabled: plan.query.problem === null && plan.waitingFor.length === 0 && !cancelled.has(plan.keyText),
      // An answer is what the database held when it was asked: it does not go stale by the clock, and it is read
      // again only when someone asks.
      staleTime: Number.POSITIVE_INFINITY,
      gcTime: 10 * 60_000,
      retry: false,
      refetchOnWindowFocus: false,
      refetchOnReconnect: false,
      // While a changed value's answer is on its way, the answer before it is held (and shown dimmed) rather than
      // blanked, so the page never jumps.
      placeholderData: keepPreviousData,
    })),
  });

  const forget = useCallback((keyText: string) => {
    setCancelled((current) => {
      if (!current.has(keyText)) return current;
      const next = new Set(current);
      next.delete(keyText);
      return next;
    });
  }, []);

  const states = new Map<string, QueryState>();
  let running = false;
  let newest: Date | null = null;
  for (let index = 0; index < plans.length; index++) {
    const plan = plans[index];
    const result = results[index];
    const run = result.data ?? null;
    const task = run?.task ?? null;
    const answer = task !== null && task.status === "succeeded" && isAnswer(task.result) ? task.result : null;
    const inFlight = result.fetchStatus === "fetching";
    const wasCancelled = cancelled.has(plan.keyText);

    let status: QueryState["status"];
    let failure: QueryState["failure"] = null;
    if (plan.query.problem !== null) {
      status = "blocked";
    } else if (plan.waitingFor.length > 0) {
      status = "waiting";
    } else if (wasCancelled && !inFlight && (answer === null || result.isPlaceholderData)) {
      // Stopped before it answered. An answer held from other values is not this run's, so it is not shown as one.
      status = "cancelled";
    } else if (inFlight && answer === null) {
      status = "running";
    } else if (result.error !== null && !inFlight) {
      status = "failed";
      failure = isApiError(result.error)
        ? { message: result.error.detail ?? result.error.title, error: result.error }
        : { message: result.error instanceof Error ? result.error.message : "The query could not be asked for.", error: null };
    } else if (answer !== null) {
      status = "answered";
    } else if (task !== null && task.status === "cancelled") {
      status = "cancelled";
    } else if (task !== null) {
      status = "failed";
      failure = { message: task.error ?? `The query ended ${task.status} without an answer.`, error: null };
    } else {
      status = "running";
    }

    if (inFlight) running = true;
    if (status === "answered" && !result.isPlaceholderData && task !== null && task.endUtc !== null) {
      const ended = parseUtc(task.endUtc);
      if (newest === null || ended > newest) newest = ended;
    }

    states.set(plan.query.name, {
      query: plan.query,
      status,
      answer: status === "answered" ? answer : null,
      run: status === "answered" || status === "failed" ? run : null,
      refreshing: status === "answered" && inFlight,
      problem: plan.query.problem,
      waitingFor: plan.waitingFor,
      failure,
      askedAtMs: inFlight ? askedAt.current.get(plan.keyText) ?? null : null,
      runAgain: () => {
        forget(plan.keyText);
        void client.invalidateQueries({ queryKey: plan.key, exact: true });
      },
      cancel: () => {
        setCancelled((current) => new Set(current).add(plan.keyText));
        void client.cancelQueries({ queryKey: plan.key, exact: true });
      },
    });
  }

  const refresh = useCallback(() => {
    setCancelled(new Set());
    void client.invalidateQueries({ queryKey: [REPORT_QUERY_ROOT, report.id] });
  }, [client, report.id]);

  return { states, running, asOf: newest, refresh };
}
