import { useEffect, useState } from "react";
import { useSearchParams } from "react-router-dom";
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { toast } from "sonner";
import { composeCacheType, explorerApi, type CacheTypeDraft } from "../../../../api/explorer";
import { kindMatches, type AmbiguousPick, type SegmentFilter } from "../dimension/dimensionDraft";
import { applyCachePlan, planCachePick, type CachePick, type CachePlan } from "./cacheDraft";
import { CACHE_PARAM, cacheDraftFromParam, cacheDraftParam, emptyCacheDraft } from "./cacheStory";

/** How long a draft rests before it is written and checked again: a person typing a name is not asked about every letter. */
const SETTLE_MS = 350;

/** `value`, once it has stopped changing for `ms`. */
function useSettled<T>(value: T, ms: number): T {
  const [settled, setSettled] = useState(value);
  useEffect(() => {
    const timer = window.setTimeout(() => setSettled(value), ms);
    return () => window.clearTimeout(timer);
  }, [value, ms]);
  return settled;
}

/**
 * The cached type a person builds in the explorer (osdu/docs/reference/concepts/explorer-cache-builder.md), as a
 * dimension is built there (`useDimensionBuild`): the draft, held here and changed at once as any input is, with the
 * explorer's address following it (its `cache` part, written over the address as it stands); the record in view, when it
 * is one the type would cache; the YAML and its check, once the draft has settled; and the picks made in the records on
 * the explorer's trail, each asked about where it passes through one item of a list.
 */
export function useCacheBuild({ partition, reachable, scopeKind, recordId, recordVersion }: {
  partition: string | null;
  reachable: boolean;
  /** The kind the explorer's place reads (a type's every version), which a build that has no kind yet takes; null for none. */
  scopeKind: string | null;
  /** The record the explorer has open, the first of its trail; null for none. */
  recordId: string | null;
  recordVersion: number | null;
}) {
  const [params, setParams] = useSearchParams();
  const [draft, setDraft] = useState<CacheTypeDraft | null>(() => {
    const held = params.get(CACHE_PARAM);
    return held === null ? null : cacheDraftFromParam(held);
  });

  // The address follows the build: written over the address as it stands now, and only where it says something else.
  const address = params.toString();
  useEffect(() => {
    const current = new URLSearchParams(window.location.search);
    const wanted = draft === null ? null : cacheDraftParam(draft);
    if (current.get(CACHE_PARAM) === wanted) {
      return;
    }

    if (wanted === null) {
      current.delete(CACHE_PARAM);
    } else {
      current.set(CACHE_PARAM, wanted);
    }

    setParams(current, { replace: true });
  }, [draft, address, setParams]);

  // A build with no kind yet caches the place the explorer is at, once it is at one.
  if (draft !== null && draft.kind === "" && scopeKind !== null) {
    setDraft({ ...draft, kind: scopeKind });
  }

  const kind = draft?.kind ?? "";
  const building = draft !== null;

  // The record in view, read as the explorer reads it (the same read, shared), when it is one the type would cache.
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
  const recordKind = record !== null && typeof record.kind === "string" ? record.kind : null;
  const root = record !== null && recordKind !== null && (kind === "" || kindMatches(kind, recordKind)) ? record : null;

  const settled = useSettled(draft, SETTLE_MS);
  const compose = useQuery({
    queryKey: ["explorer", "cache", "compose", settled],
    queryFn: () => composeCacheType(settled!),
    enabled: reachable && settled !== null && settled.kind !== "",
    placeholderData: keepPreviousData,
    retry: false,
    refetchOnWindowFocus: false,
  });

  // A pick waiting on its questions, answered one at a time; the draft is changed once every one is.
  const [pending, setPending] = useState<{ plan: CachePlan; answers: (SegmentFilter | null)[] } | null>(null);
  const finish = (plan: CachePlan, answers: (SegmentFilter | null)[]) =>
    setDraft((current) => (current === null ? current : applyCachePlan(current, plan, answers)));

  return {
    /** The draft; null while no cached type is built. */
    draft,
    /** The draft once it has stopped changing, which is written, checked and read by. */
    settled,
    /** The partition the build reads, and whether its connection can be read through. */
    partition,
    reachable,
    /** Starts a build caching `start` (empty for none yet), leaving any build before it. */
    start: (start: string, query: string | null = null) => {
      setPending(null);
      setDraft({ ...emptyCacheDraft(start), query });
    },
    /** Ends the build, its draft with it. */
    stop: () => {
      setPending(null);
      setDraft(null);
    },
    /** Replaces the draft with what the panel's settings make of it. */
    update: (next: CacheTypeDraft) => setDraft(next),
    /** Makes a pick in a record on the explorer's trail: at once, or once the questions it raises are answered. */
    pick: (pick: CachePick) => {
      if (draft === null) {
        return;
      }

      const plan = planCachePick(draft, pick);
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
    compose,
    /** The kind of the record in view, whichever kind the build caches. */
    recordKind,
    /** The record in view, when it is one the type would cache; null otherwise. */
    inView: root,
  };
}

/** What a build of a cached type holds and does, as the explorer's page and the build panel use it. */
export type CacheBuild = ReturnType<typeof useCacheBuild>;
