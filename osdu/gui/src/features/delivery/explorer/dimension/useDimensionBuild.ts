import { useEffect, useState } from "react";
import { useSearchParams } from "react-router-dom";
import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import {
  composeDimension, dimensionKeyCandidates, dimensionKeys, explorerApi, type DimensionDraft, type DimensionKeysRequest, type ExplorerIndex,
} from "../../../../api/explorer";
import { failureText } from "../../answers";
import { withoutVersion } from "../../osduDocument";
import type { RecordFollow } from "../../OsduRecordView";
import {
  DRAFT_PARAM, EXAMPLES, applyPlan, chooseKey, draftFromParam, draftParam, emptyDraft, firstValueAt, kindMatches, planOf,
  type AmbiguousPick, type BuildPick, type PickPlan, type SegmentFilter,
} from "./dimensionDraft";
import { firstLocationAt, keyEntityType, kindTypeName, typeName } from "./dimensionStory";

/** How long a draft rests before it is written and checked again: a person typing a name is not asked about every letter. */
const SETTLE_MS = 350;

/** How long the keys read stand before the same are read again. */
const FRESH_MS = 60_000;

/** The ways the search service indexes a property, which a search condition names. */
const INDEXES: readonly string[] = ["text", "keyword", "number", "boolean", "date"] satisfies ExplorerIndex[];

/** `value`, once it has stopped changing for `ms`. */
function useSettled<T>(value: T, ms: number): T {
  const [settled, setSettled] = useState(value);
  useEffect(() => {
    const timer = window.setTimeout(() => setSettled(value), ms);
    return () => window.clearTimeout(timer);
  }, [value, ms]);
  return settled;
}

const isText = (value: unknown): value is string => typeof value === "string";

/** The record a key names, to open on the explorer's trail after `root`, the record of the dimension's kind holding the key. */
export interface KeyRecordFollow extends RecordFollow {
  root: string;
}

/**
 * The dimension a person builds in the explorer (osdu/docs/reference/concepts/explorer-dimension-builder.md): the
 * draft, held here and changed at once as any input is, with the explorer's address following it (its `dim` part,
 * written over the address as it stands, so the explorer's own navigation and the build never undo each other); the
 * keys the kind's template suggests, the likeliest made the key as a build starts; the commonest keys and the record in
 * view, which the example steps through, that record's own key first; the YAML and its check, once the draft has
 * settled; the picks made in the records on the explorer's trail, each asked about where it passes through one item of a
 * list; and the record the example key names, opened on the trail for its values to be picked (from the record in view
 * when it holds the key, else from a record of the kind the search finds holding it, which `openRecord` opens first).
 */
export function useDimensionBuild({ partition, reachable, scopeKind, recordId, recordVersion, openRecord }: {
  partition: string | null;
  reachable: boolean;
  /** The kind the explorer's place reads (a type's every version), which a build that has no kind yet takes; null for none. */
  scopeKind: string | null;
  /** The record the explorer has open, the first of its trail; null for none. */
  recordId: string | null;
  recordVersion: number | null;
  /** Opens a record in the explorer, at its latest version, as the first of its trail. */
  openRecord: (id: string) => void;
}) {
  const queryClient = useQueryClient();
  const [params, setParams] = useSearchParams();
  const [draft, setDraft] = useState<DimensionDraft | null>(() => {
    const held = params.get(DRAFT_PARAM);
    return held === null ? null : draftFromParam(held);
  });

  // The address follows the build: written over the address as it stands now, which the router has already moved to when a
  // navigation of the explorer's is still being applied, and only where it says something else.
  const address = params.toString();
  useEffect(() => {
    const current = new URLSearchParams(window.location.search);
    const wanted = draft === null ? null : draftParam(draft);
    if (current.get(DRAFT_PARAM) === wanted) {
      return;
    }

    if (wanted === null) {
      current.delete(DRAFT_PARAM);
    } else {
      current.set(DRAFT_PARAM, wanted);
    }

    setParams(current, { replace: true });
  }, [draft, address, setParams]);

  // A build with no kind yet reads the place the explorer is at, once it is at one.
  if (draft !== null && draft.kind === "" && scopeKind !== null) {
    setDraft({ ...draft, kind: scopeKind });
  }

  const kind = draft?.kind ?? "";
  const building = draft !== null;

  // The record in view, read as the explorer reads it (the same read, shared), when it is one of the kind the build reads.
  const rootRead = useQuery({
    queryKey: ["explorer", "read", partition, recordId, recordVersion],
    queryFn: () => explorerApi.read(partition, recordId!, recordVersion ?? undefined),
    enabled: building && reachable && recordId !== null,
    staleTime: Number.POSITIVE_INFINITY,
    gcTime: 60_000,
    retry: false,
    refetchOnWindowFocus: false,
  });
  const read = recordId === null ? null : rootRead.data ?? null;
  const record = read?.found === true && read.record ? read.record : null;
  const recordKind = record !== null && isText(record.kind) ? record.kind : null;
  const root = record !== null && recordKind !== null && (kind === "" || kindMatches(kind, recordKind)) ? record : null;

  const candidates = useQuery({
    queryKey: ["explorer", "dimension", "candidates", kind],
    queryFn: () => dimensionKeyCandidates(kind),
    enabled: building && reachable && kind !== "",
    staleTime: 5 * 60_000,
    retry: false,
    refetchOnWindowFocus: false,
  });
  const suggestions = candidates.data?.keys ?? null;
  const namesOf = (path: string) => suggestions?.find((candidate) => candidate.path === path)?.names ?? [];

  // As a build starts, the likeliest key the template suggests is made the key: the first the record in view holds a value
  // at, once that record has been read. A key picked since, or a build that had one, is left as it is.
  const [autoFor, setAutoFor] = useState<string | null>(null);
  const recordSettled = recordId === null || !rootRead.isPending;
  if (draft !== null && kind !== "" && (draft.path === null || draft.path === "") && suggestions !== null && recordSettled && autoFor !== kind) {
    setAutoFor(kind);
    const chosen = chooseKey(suggestions, root);
    if (chosen !== null) {
      const plan: PickPlan = { action: "key", kind, key: chosen.path, keyExample: firstValueAt(root, chosen.path), path: chosen.path, steps: [], questions: [] };
      setDraft(applyPlan(draft, plan, [], [], chosen.names));
    }
  }

  const settled = useSettled(draft, SETTLE_MS);
  const keysRequest: DimensionKeysRequest | null = settled !== null && settled.kind !== "" && settled.path !== null && settled.path !== ""
    ? { kind: settled.kind, query: settled.query, path: settled.path }
    : null;
  const keys = useQuery({
    queryKey: ["explorer", "dimension", "keys", partition, keysRequest],
    queryFn: () => dimensionKeys(partition, keysRequest!),
    enabled: reachable && keysRequest !== null,
    staleTime: FRESH_MS,
    retry: false,
    refetchOnWindowFocus: false,
  });

  // The examples: the key the record in view holds first, then the commonest keys.
  const recordKey = draft?.path ? firstValueAt(root, draft.path) : null;
  const examples = [...new Set([recordKey, ...(keys.data?.answer.keys ?? []).map((k) => k.key)].filter(isText))].slice(0, EXAMPLES);
  const [at, setAt] = useState(0);
  const exampleFor = `${draft?.path ?? ""}|${recordKey ?? ""}`;
  const [seenExampleFor, setSeenExampleFor] = useState(exampleFor);
  if (seenExampleFor !== exampleFor) {
    setSeenExampleFor(exampleFor);
    setAt(0);
  }

  const exampleKey = examples.length === 0 ? null : examples[Math.min(at, examples.length - 1)];
  const keyPath = draft?.path ?? "";
  // The type of record the key names (the template's word for it, else the example key's), null for a key naming none.
  const keyEntity = keyPath === "" ? null : keyEntityType(namesOf(keyPath), exampleKey);

  const compose = useQuery({
    queryKey: ["explorer", "dimension", "compose", partition, settled, exampleKey],
    queryFn: () => composeDimension(partition, settled!, exampleKey),
    enabled: reachable && settled !== null && settled.kind !== "",
    placeholderData: keepPreviousData,
    retry: false,
    refetchOnWindowFocus: false,
  });
  const columns = [compose.data?.dimension?.keyColumn, compose.data?.dimension?.valueColumn].filter(isText);

  // A pick waiting on its questions, answered one at a time; the draft is changed once every one is.
  const [pending, setPending] = useState<{ plan: PickPlan; answers: (SegmentFilter | null)[] } | null>(null);
  const finish = (plan: PickPlan, answers: (SegmentFilter | null)[]) =>
    setDraft((current) => (current === null ? current : applyPlan(current, plan, answers, columns, namesOf(plan.key))));

  // The record the example key names, opened on the trail after a record of the kind that holds the key: the one in view
  // when it does, else the first the search finds holding it, read into the explorer's own read before it is opened, so the
  // record view finds it read and the record the key names opens straight after it.
  const [follow, setFollow] = useState<KeyRecordFollow | null>(null);
  const opener = useMutation({
    mutationFn: async (key: string): Promise<KeyRecordFollow> => {
      const id = withoutVersion(key);
      const here = root === null ? null : firstLocationAt(root, keyPath, key);
      if (here !== null && recordId !== null) {
        return { root: recordId, id, from: here };
      }

      const records = `${kindTypeName(kind)} record`;
      const field = keys.data?.answer.keyField ?? null;
      if (field === null || !INDEXES.includes(field.index)) {
        throw new Error(`The search service reports no index of ${keyPath}, so no ${records} holding ${key} can be looked for. Open one on the left.`);
      }

      const found = await explorerApi.search(partition, {
        kind,
        filters: [{ path: field.path, index: field.index as ExplorerIndex, ...(field.nestedPath === null ? {} : { nested: field.nestedPath }), value: key }],
        limit: 1,
      });
      if (found.answer.refusal) {
        throw new Error(`The search service would not look for a ${records} holding ${key}: ${found.answer.refusal}`);
      }

      const hit = found.answer.hits[0];
      if (hit === undefined) {
        throw new Error(`The search service found no ${records} holding ${key} at ${keyPath}.`);
      }

      const held = await queryClient.fetchQuery({
        queryKey: ["explorer", "read", partition, hit.id, null],
        queryFn: () => explorerApi.read(partition, hit.id),
        staleTime: Number.POSITIVE_INFINITY,
      });
      const from = held.found && held.record ? firstLocationAt(held.record, keyPath, key) : null;
      if (from === null) {
        throw new Error(`${hit.id} no longer holds ${key} at ${keyPath}: the search service has not caught up with its latest version.`);
      }

      return { root: hit.id, id, from };
    },
    onSuccess: (asked) => {
      if (asked.root !== recordId) {
        openRecord(asked.root);
      }

      setFollow(asked);
    },
    onError: (error) => toast.error(failureText(error)),
  });

  return {
    /** The draft; null while no dimension is built. */
    draft,
    /** The draft once it has stopped changing, which is written, checked and read by. */
    settled,
    /** The partition the build reads, and whether its connection can be read through. */
    partition,
    reachable,
    /** Starts a build reading `start` (empty for none yet), leaving any build before it. */
    start: (start: string) => {
      setAutoFor(null);
      setPending(null);
      setFollow(null);
      setAt(0);
      setDraft(emptyDraft(start));
    },
    /** Ends the build, its draft with it. */
    stop: () => {
      setPending(null);
      setFollow(null);
      setDraft(null);
    },
    /** Replaces the draft with what the table's settings make of it. */
    update: (next: DimensionDraft) => setDraft(next),
    /** Makes a pick in a record on the explorer's trail: at once, or once the questions it raises are answered. */
    pick: (pick: BuildPick) => {
      if (draft === null) {
        return;
      }

      const plan = planOf(draft, pick);
      if ("problem" in plan) {
        toast.error(plan.problem);
      } else if (plan.questions.length === 0) {
        finish(plan, []);
      } else {
        setPending({ plan, answers: [] });
      }
    },
    /** The question the pick waiting asks now; null when none waits. */
    question: (pending?.plan.questions[pending.answers.length] ?? null) as AmbiguousPick | null,
    /** Answers the question asked: a filter, or none to keep the first value found (or follow every record). */
    answer: (filter: SegmentFilter | null) => {
      if (pending === null) {
        return;
      }

      const answers = [...pending.answers, filter];
      if (answers.length === pending.plan.questions.length) {
        setPending(null);
        finish(pending.plan, answers);
      } else {
        setPending({ ...pending, answers });
      }
    },
    /** Leaves the pick waiting unmade. */
    cancel: () => setPending(null),
    candidates,
    keys,
    compose,
    /** The kind of the record in view, whichever kind the build reads. */
    recordKind,
    /** The record in view, when it is one of the kind the build reads; null otherwise. */
    inView: root,
    /** The examples the build steps through, the record in view's key first, and the one shown. */
    examples,
    exampleAt: Math.min(at, Math.max(0, examples.length - 1)),
    exampleKey,
    recordKey,
    stepExample: (step: number) => setAt((current) => (examples.length === 0 ? 0 : (current + step + examples.length) % examples.length)),
    /** Shows the example key at its place among the examples. */
    showExample: (index: number) => setAt(Math.max(0, Math.min(index, examples.length - 1))),
    /** The type of record the key names (`master-data--Wellbore`); null for a key naming none. */
    keyEntity,
    /** The key's records' type as a person reads it (`Wellbore`); null for a key naming none. */
    keyType: keyEntity === null ? null : typeName(keyEntity),
    /** Opens the record the example key names on the explorer's trail, for its values to be picked. */
    openKeyRecord: () => {
      if (exampleKey !== null && keyEntity !== null && !opener.isPending) {
        opener.mutate(exampleKey);
      }
    },
    /** Whether the record the example key names is being found. */
    openingKeyRecord: opener.isPending,
    /** The record to open on the trail after the record in view, until the record view has opened it. */
    follow,
    /** Lets the record to open go, once the record view has opened it. */
    followed: () => setFollow(null),
  };
}

/** What a build holds and does, as the explorer's page and the build panel use it. */
export type DimensionBuild = ReturnType<typeof useDimensionBuild>;
