import { useQueries } from "@tanstack/react-query";
import { isApiError } from "@/api/client";
import { pipelineApi } from "@/api/endpoints";
import type { DeclaredRunParameter, RunValue } from "@/api/types";
import { parameterOffer } from "@/features/runs/runInputs";
import { wantsFirstOption } from "./scopeDefaults";

/** How long a parameter's values are kept, as the field listing them keeps them. */
const VALUES_STALE_MS = 60_000;

/** What a read of a flow's rows starts from for the parameters it cannot go without. */
export interface FirstOptions {
  /** The first option of each parameter that wants one: its document's first choice, or the value a lookup finds most used. */
  first: Readonly<Record<string, string>>;
  /** True while a lookup is on its way. */
  reading: boolean;
  /** Why a parameter has no first option, by name: its lookup failed, or the source holds no value of it. */
  problems: Readonly<Record<string, string>>;
}

/**
 * The first option of each required parameter of a flow that has no default. A lookup is asked under the query its field reads
 * (RunParameterFields), so the field and this are one request.
 */
export function useFirstOptions(
  pipelineId: string | null,
  parameters: readonly DeclaredRunParameter[],
  context: Readonly<Record<string, RunValue>>,
): FirstOptions {
  const wanted = parameters.filter(wantsFirstOption);
  const lookedUp = wanted.filter((parameter) => parameterOffer(parameter) === "lookup");
  const contextKey = JSON.stringify(Object.entries(context).sort(([a], [b]) => a.localeCompare(b)));
  const answers = useQueries({
    queries: lookedUp.map((parameter) => ({
      queryKey: ["pipeline-parameter-values", pipelineId, parameter.name, contextKey, null],
      queryFn: () => pipelineApi.parameterValues(pipelineId!, parameter.name, { values: { ...context } }),
      enabled: pipelineId !== null,
      staleTime: VALUES_STALE_MS,
    })),
  });

  const first: Record<string, string> = {};
  const problems: Record<string, string> = {};
  for (const parameter of wanted) {
    if (parameterOffer(parameter) === "choices") {
      first[parameter.name] = parameter.choices[0];
    }
  }

  let reading = false;
  lookedUp.forEach((parameter, index) => {
    const answer = answers[index];
    if (answer.isPending) {
      reading = true;
    } else if (answer.isError) {
      const why = isApiError(answer.error) ? answer.error.detail ?? answer.error.title : String(answer.error);
      problems[parameter.name] = `The values of ${parameter.name} could not be read: ${why}`;
    } else if (answer.data.values.length === 0) {
      problems[parameter.name] = `${answer.data.source} holds no value, so nothing is staged for ${parameter.name} yet.`;
    } else {
      first[parameter.name] = answer.data.values[0].value;
    }
  });

  return { first, reading, problems };
}
