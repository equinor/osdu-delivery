import { useEffect, useId, useMemo, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { Checkbox } from "@/components/ui/checkbox";
import { Skeleton } from "@/components/ui/skeleton";
import { SearchInput } from "@/components/SearchInput";
import type { TriggerBodyContribution, TriggerFieldsProps } from "@/modules/registry";
import { deliveryApi, type DeliveryAssertionFlow } from "../../../api/delivery";
import { useActivePartition } from "../activePartition";
import type { RunValue } from "@/api/types";
import { TagChip } from "./AssertionBadges";
import { counted, disabledWhy, kindEntity, selectionPayload } from "./assertionFormat";

/** The run value naming the partition a run tests (osdu/docs/reference/flow/assertion.md, Which partition a run tests). */
const PARTITION = "partition";

/** The payload keys these fields own; anything else the run being repeated carried goes with it unchanged. */
const OWNED_KEYS = new Set(["tests", "tags"]);

/** A test's name as the flow document allows it (AssertionNames.IsName). */
const TEST_NAME = /^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$/;

/** The list gets a filter once it is longer than this. */
const FILTER_FROM = 8;

function stringsOf(value: unknown): string[] {
  return Array.isArray(value) ? value.filter((item): item is string => typeof item === "string" && item.trim() !== "") : [];
}

/** The partition a run of the flow tests, and why it cannot run while the title bar is where it is. */
function partitionOf(flow: DeliveryAssertionFlow | undefined, active: string | null): { partition: string | null; error: string | null } {
  if (flow === undefined) {
    return { partition: null, error: "Reading the flow's assertions." };
  }

  if (flow.partitions.length > 0) {
    if (active === null) {
      return { partition: null, error: "Pick a partition in the title bar: a run checks the partition picked there." };
    }

    return flow.partitions.includes(active)
      ? { partition: active, error: null }
      : {
        partition: null,
        error: `This flow does not check ${active}, the partition picked in the title bar; it checks ${flow.partitions.join(", ")}. Pick ${flow.partitions.length > 1 ? "one of them" : "it"} in the title bar to run it.`,
      };
  }

  if (flow.problem !== null) {
    return { partition: null, error: flow.problem };
  }

  return flow.partition !== null && active !== null && flow.partition !== active
    ? {
      partition: null,
      error: `This flow checks ${flow.partition}, the partition its data-partition-id header names, and the title bar is on ${active}. Pick ${flow.partition} in the title bar to run it.`,
    }
    : { partition: null, error: null };
}

/**
 * The trigger dialog's fields for an assertion flow: the partition it tests (the title bar's), and which of its tests to
 * run, by name and by tag; its parameters are the dialog's Flow parameters, read from the flow's document. Nothing picked
 * runs every test. A run being repeated opens with the tests and tags it was given, and keeps any other part of its
 * payload as it was.
 */
export function AssertionTriggerFields({ pipelineId, initialPayload, onChange }: TriggerFieldsProps) {
  const idPrefix = useId();
  const [active] = useActivePartition();
  const board = useQuery({
    queryKey: ["delivery", "assertions", "flow", pipelineId, active],
    queryFn: () => deliveryApi.assertionFlowBoard(pipelineId!),
    enabled: pipelineId !== null,
    staleTime: 15000,
  });
  const flow = board.data?.flows[0];
  const [tests, setTests] = useState<ReadonlySet<string>>(() => new Set(stringsOf(initialPayload?.tests)));
  const [tags, setTags] = useState<ReadonlySet<string>>(() => new Set(stringsOf(initialPayload?.tags)));
  const [filter, setFilter] = useState("");
  const [carried] = useState<Record<string, unknown>>(
    () => Object.fromEntries(Object.entries(initialPayload ?? {}).filter(([key]) => !OWNED_KEYS.has(key))),
  );

  const allTests = useMemo(() => flow?.tests ?? [], [flow]);
  const allTags = useMemo(
    () => [...new Set(allTests.flatMap((test) => test.tags))].sort((a, b) => a.localeCompare(b)),
    [allTests],
  );
  const { partition, error: partitionError } = partitionOf(flow, active);
  // What the run will run, as the flow selects it: the tests named and the tests carrying a tag picked, in document order.
  const chosen = useMemo(() => allTests.filter((test) => tests.has(test.name) || test.tags.some((tag) => tags.has(tag))), [allTests, tests, tags]);
  const shown = useMemo(() => {
    const term = filter.trim().toLowerCase();
    return term === ""
      ? allTests
      : allTests.filter((test) => test.name.toLowerCase().includes(term) || test.kind.toLowerCase().includes(term) || test.tags.some((tag) => tag.toLowerCase().includes(term)));
  }, [allTests, filter]);

  const body = useMemo<TriggerBodyContribution>(() => {
    const known = new Set(allTests.map((test) => test.name.toLowerCase()));
    const missing = flow === undefined ? [] : [...tests].filter((name) => !known.has(name.toLowerCase()));
    const unusedTags = flow === undefined ? [] : [...tags].filter((tag) => !allTags.includes(tag));
    const error = partitionError
      ?? ([...tests].some((name) => !TEST_NAME.test(name)) ? "An assertion's name is a letter or digit, then letters, digits, '.', '_' and '-'." : null)
      ?? (missing.length > 0 ? `The flow has no assertion named ${missing.join(", ")}; clear ${missing.length === 1 ? "it" : "them"} to run.` : null)
      ?? (unusedTags.length > 0 ? `No assertion of the flow carries the tag ${unusedTags.join(", ")}; clear ${unusedTags.length === 1 ? "it" : "them"} to run.` : null);
    const values: Record<string, RunValue> = {};
    if (partition !== null) {
      values[PARTITION] = partition;
    }

    const payload = { ...carried, ...selectionPayload([...tests], [...tags]) };
    return {
      values: Object.keys(values).length > 0 ? values : undefined,
      payload: Object.keys(payload).length > 0 ? payload : undefined,
      error,
    };
  }, [allTags, allTests, carried, flow, partition, partitionError, tags, tests]);

  useEffect(() => {
    onChange(body);
  }, [body, onChange]);

  const toggle = (set: ReadonlySet<string>, value: string): Set<string> => {
    const next = new Set(set);
    if (!next.delete(value)) {
      next.add(value);
    }

    return next;
  };

  return (
    <div className="flex flex-col gap-3" data-testid="trigger-kind-fields">
      <div className="flex flex-col gap-1" data-testid="trigger-partition">
        <span className="text-[13px] font-medium">Partition</span>
        <span className="font-mono text-[13px]" data-testid="trigger-partition-value">
          {flow !== undefined && flow.partitions.length === 0
            ? flow.partition ?? "the one its data-partition-id header names"
            : partition ?? active ?? "none picked"}
        </span>
        <p className="text-xs text-muted-foreground">
          The partition picked in the title bar: the assertions read it, and the report is kept under it. Nothing is written to it.
        </p>
      </div>


      <div className="flex flex-col gap-2" data-testid="trigger-tests">
        <div className="flex flex-wrap items-baseline justify-between gap-2">
          <span className="text-[13px] font-medium">Assertions</span>
          <span className="text-xs text-muted-foreground" data-testid="trigger-tests-summary">
            {tests.size === 0 && tags.size === 0
              ? `Every assertion runs${allTests.length > 0 ? ` (${allTests.length})` : ""}.`
              : `${counted(chosen.length, "assertion")} of ${allTests.length} run.`}
          </span>
        </div>
        {board.isPending && pipelineId !== null && <Skeleton className="h-24 w-full" />}
        {allTags.length > 0 && (
          <div className="flex flex-wrap items-center gap-1.5" role="group" aria-label="Run the assertions carrying a tag">
            <span className="text-xs text-muted-foreground">Tags</span>
            {allTags.map((tag) => (
              <TagChip key={tag} tag={tag} pressed={tags.has(tag)} onToggle={() => setTags((was) => toggle(was, tag))} />
            ))}
          </div>
        )}
        {allTests.length > FILTER_FROM && (
          <SearchInput value={filter} onChange={setFilter} placeholder="Filter assertions by name, kind or tag" label="Filter assertions" testId="trigger-tests-filter" />
        )}
        {allTests.length > 0 && (
          <div className="max-h-64 overflow-y-auto rounded-md border" data-testid="trigger-tests-list">
            {shown.map((test) => {
              const byTag = !tests.has(test.name) && test.tags.some((tag) => tags.has(tag));
              const id = `${idPrefix}-test-${test.name}`;
              return (
                <label
                  key={test.name}
                  htmlFor={id}
                  className="flex cursor-pointer items-center gap-2.5 border-b px-2.5 py-1.5 last:border-b-0 hover:bg-muted/50"
                >
                  <Checkbox
                    id={id}
                    checked={tests.has(test.name) || byTag}
                    disabled={byTag}
                    onCheckedChange={() => setTests((was) => toggle(was, test.name))}
                    data-testid={`trigger-test-${test.name}`}
                  />
                  <span className="min-w-0 flex-1">
                    <span className="block truncate font-mono text-[12px]">{test.name}</span>
                    <span className="block truncate text-[11px] text-muted-foreground">
                      {kindEntity(test.kind)}
                      {byTag ? ", picked by its tag" : ""}
                      {test.runsHere ? "" : ", skipped: it does not check this partition"}
                      {test.runsHere && disabledWhy(test) !== null ? ", skipped: disabled" : ""}
                      {test.runsHere && disabledWhy(test) === null && test.needsNewProofValues === true ? ", flagged: needs new proof values" : ""}
                    </span>
                  </span>
                  {test.tags.length > 0 && <span className="hidden font-mono text-[10.5px] text-muted-foreground sm:inline">{test.tags.map((tag) => `#${tag}`).join(" ")}</span>}
                </label>
              );
            })}
            {shown.length === 0 && <p className="px-2.5 py-3 text-xs text-muted-foreground">No assertion matches the filter.</p>}
          </div>
        )}
        <p className="text-xs text-muted-foreground">
          Pick assertions by name or by tag to run only those; the others keep their last results on the board.
        </p>
      </div>
    </div>
  );
}
