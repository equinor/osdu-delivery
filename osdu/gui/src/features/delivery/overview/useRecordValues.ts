import { useMemo, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { deliveryApi, type DeliveryRecordPreview, type MappingOutline } from "../../../api/delivery";
import { flowScope, type ValueCheckSession } from "../useValueCheck";
import { lineOfRow, type StagedLine } from "../dryRun";
import { fieldId, valueAt } from "./outlineModel.ts";

/** The rows the overview steps through: enough to see how a mapping treats different rows, without paging a table. */
const ROWS = 50;

/** How a value reads on the canvas. */
function shown(value: unknown): string {
  if (value === null || value === undefined) {
    return "no value";
  }

  return typeof value === "object" ? JSON.stringify(value) : String(value);
}

/**
 * What one record's rows and document hold, as the canvas shows them: each field's value by its id and each property's by
 * its path, a repeated property by the first item it writes. A `$coalesce`'s alternatives say which of them gave the value.
 */
export function valuesOf(outline: MappingOutline, preview: DeliveryRecordPreview): Map<string, string> {
  const values = new Map<string, string>();
  const source = preview.source;
  if (source !== null && source !== undefined) {
    for (const [column, value] of Object.entries(source.row)) {
      values.set(fieldId("dataset", column), shown(value));
    }

    for (const [name, rows] of Object.entries(source.datasets)) {
      for (const [column, value] of Object.entries(rows.rows[0] ?? {})) {
        values.set(fieldId(`dataset.${name}`, column), shown(value));
      }
    }
  }

  const rendered = preview.document?.rendered;
  const walk = (nodes: MappingOutline["record"]) => {
    for (const node of nodes) {
      if (node.shape === "repeat") {
        const items = valueAt(rendered, node.path);
        const count = Array.isArray(items) ? items.length : 0;
        values.set(node.path, count === 0 ? "no items" : `${count} ${count === 1 ? "item" : "items"}, the first shown`);
      } else if ((node.shape === "value" || node.shape === "choice") && !node.path.includes("|")) {
        values.set(node.path, shown(valueAt(rendered, node.path)));
      }

      walk(node.children);
    }
  };

  walk(outline.record);
  for (const choice of preview.document?.choices ?? []) {
    const path = choice.target.replace(/^osdu\./, "");
    for (let alternative = 1; alternative <= choice.of; alternative++) {
      values.set(`${path}|${alternative}`, alternative === choice.alternative ? "gave the value" : alternative < choice.alternative ? "gave none" : "not tried");
    }
  }

  return values;
}

/** The record the canvas shows the values of, and how to move through the rows. */
export interface RecordValues {
  /** A record is being shown. */
  on: boolean;
  toggle: () => void;
  /** The rows there are to step through; null until they are read. */
  lines: StagedLine[] | null;
  /** How many rows the scope holds. */
  total: number | null;
  at: number;
  step: (by: number) => void;
  line: StagedLine | null;
  loading: boolean;
  problem: unknown;
  values: Map<string, string>;
  /** Why a record cannot be shown; null when it can. */
  blocked: string | null;
}

/**
 * Follows one real record through a mapping: the rows staged for the flow it renders with, and a preview of the one stopped
 * on. Nothing is read until the reader asks, and nothing is sent or stored.
 */
export function useRecordValues(outline: MappingOutline, session: ValueCheckSession): RecordValues {
  const [on, setOn] = useState(false);
  const [at, setAt] = useState(0);
  const { flow, ready, sentValues } = session;
  const reading = on && flow !== null && ready;
  const rows = useQuery({
    queryKey: ["delivery", "overview-rows", flow?.pipelineId ?? null, flow?.interface ?? null, flow?.partition ?? null, sentValues],
    queryFn: () => deliveryApi.stagedRows(flow!.pipelineId, { values: sentValues, limit: ROWS }, flowScope(flow!)),
    enabled: reading,
    staleTime: 30000,
    retry: false,
  });

  const lines = useMemo(() => (rows.data === undefined ? null : rows.data.rows.map((row) => lineOfRow(row, null)).filter((line) => line.keyJson !== null && line.deletedUtc === null)), [rows.data]);
  const line = lines?.[Math.min(at, lines.length - 1)] ?? null;
  const preview = useQuery({
    queryKey: ["delivery", "overview-record", flow?.pipelineId ?? null, flow?.interface ?? null, flow?.partition ?? null, line?.keyJson ?? null, sentValues],
    queryFn: () => deliveryApi.preview(flow!.pipelineId, { key: line!.keyJson, values: sentValues }, flowScope(flow!)),
    enabled: reading && line !== null,
    staleTime: 30000,
    retry: false,
  });

  const values = useMemo(() => (on && preview.data !== undefined ? valuesOf(outline, preview.data) : new Map<string, string>()), [on, outline, preview.data]);
  const blocked = flow === null
    ? "No flow renders with this mapping yet, so there is no record to show."
    : ready ? null : session.blocked ?? "The rows cannot be read yet.";

  return {
    on,
    toggle: () => setOn((current) => !current),
    lines,
    total: rows.data?.total ?? null,
    at,
    step: (by) => setAt((current) => Math.max(0, Math.min((lines?.length ?? 1) - 1, current + by))),
    line,
    loading: reading && (rows.isFetching || preview.isFetching),
    problem: rows.error ?? preview.error,
    values,
    blocked,
  };
}
