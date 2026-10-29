// Download flows (osdu/docs/reference/flow/download.md): a download flow writes every record of OSDU kinds in a partition to a
// folder, one JSON file per record named after its id, holding the record's metadata as storage holds it. Its ledger keeps, per
// id, the file it is written to and the version that file holds, so a run writes only what is new or changed. Same conventions
// as delivery.ts: one function per endpoint, pages compose them with TanStack Query. An answer of the download endpoints leaves
// out what holds no value, so every field that may hold none is optional here, and a page tests it with `=== undefined` or
// `??`, never `=== null`.

import { get } from "@/api/client";
import type { DeliveryParameter } from "./delivery";

/** Where a downloaded record stands: its file holds the version listed, a run is yet to write it, its last write failed, or OSDU no longer serves it. */
export type DownloadRecordState = "written" | "due" | "problem" | "gone";

/** Every state, in the order a page lists them. */
export const DOWNLOAD_RECORD_STATES: readonly DownloadRecordState[] = ["written", "due", "problem", "gone"];

/** How a download run ended, or that it still runs. */
export type DownloadRunStatus = "running" | "completed" | "failed";

/** Why a run wrote every record OSDU serves again: the run asked, or its folder is not the one the last completed run wrote. */
export type DownloadRewrite = "forced" | "moved";

/** How many records of a download stand where, and the bytes their files hold. */
export interface DownloadCounts {
  written: number;
  due: number;
  problem: number;
  gone: number;
  bytes: number;
}

/** One run of a download: where it wrote, what its listing changed, and the files it wrote. */
export interface DownloadRun {
  downloadRunId: number;
  downloadId: number;
  runId?: string;
  actor: string;
  status: DownloadRunStatus;
  startedUtc: string;
  completedUtc?: string;
  readMode: string;
  location: string;
  rewrite?: DownloadRewrite;
  listed: number;
  pages: number;
  requests: number;
  added: number;
  changed: number;
  gone: number;
  returned: number;
  written: number;
  bytes: number;
  missing: number;
  error?: string;
}

/** One download as the partition keeps it: what it reads, the folder its last completed run wrote, its counts and its last run. */
export interface Download {
  downloadId: number;
  partition: string;
  name: string;
  kind: string;
  query?: string;
  readMode: string;
  location?: string;
  createdUtc: string;
  updatedUtc: string;
  lastCompletedUtc?: string;
  counts: DownloadCounts;
  lastRun?: DownloadRun;
}

/** A download a flow declares or keeps, with what the partition keeps of it (absent until a run registers it). */
export interface DownloadEntry {
  name: string;
  description?: string;
  kind: string;
  query?: string;
  declared: boolean;
  download?: Download;
}

/** One download flow in the partition it is read in, with its downloads and what keeps it from being shown. */
export interface DownloadFlow {
  pipelineId: string;
  repoId: string;
  name: string;
  description?: string;
  batch?: string;
  partition?: string;
  readsPartition: boolean;
  partitions: string[];
  ledgerId?: string;
  read?: string;
  location?: string;
  parameters: DeliveryParameter[];
  problem?: string;
  downloads: DownloadEntry[];
}

/** One record of a download: its id, the file it is written to, where it stands, and what the file holds. */
export interface DownloadRecord {
  downloadRecordId: number;
  downloadId: number;
  targetId: string;
  kind?: string;
  version?: number;
  fileName: string;
  state: DownloadRecordState;
  firstSeenUtc: string;
  changedUtc: string;
  goneUtc?: string;
  writtenVersion?: number;
  writtenUtc?: string;
  writtenRunId?: number;
  bytes?: number;
  sha256?: string;
  problem?: string;
  problemUtc?: string;
}

/** A page of a download's records, with the number the next page starts after; absent on the last page. */
export interface DownloadRecordPage {
  items: DownloadRecord[];
  next?: number;
}

/** A download by its partition and number, as a route names it. */
export interface DownloadRef {
  partition: string;
  downloadId: number;
}

const downloadsPath = "/api/v1/delivery/downloads";

const downloadPath = (ref: DownloadRef) => `${downloadsPath}/${encodeURIComponent(ref.partition)}/${ref.downloadId}`;

export const downloadApi = {
  /** A download flow in the workbench's partition: each download it declares or keeps, with its counts and last run. */
  flow: (pipelineId: string) => get<DownloadFlow>(`/api/v1/delivery/flows/${pipelineId}/downloads`),
  /** A download's latest runs, newest first. */
  runs: (ref: DownloadRef, limit?: number) => get<DownloadRun[]>(`${downloadPath(ref)}/runs`, limit === undefined ? {} : { limit }),
  /** A page of a download's records after `after`, of one state or all, those whose id or file name holds `q` when it is given. */
  records: (ref: DownloadRef, query: { state?: DownloadRecordState; q?: string; after?: number; limit?: number } = {}) =>
    get<DownloadRecordPage>(`${downloadPath(ref)}/records`, query),
};
