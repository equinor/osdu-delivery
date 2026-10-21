import { useQuery } from "@tanstack/react-query";
import { deliveryApi, type DeliveryMappingParseResult } from "../../api/delivery";
import type { UseQueryResult } from "@tanstack/react-query";

/**
 * A mapping document read back into a draft: the entries as the builder edits them, which is what every view of one
 * document reads it through. Keyed by the document's content hash and its repository, so the parse is asked for once
 * however many views of the same mapping are open, and a mapping synced with new content is read again. `repoId` names
 * the synced repository whose template folders settle a template the document names by kind alone.
 */
export function useParsedMapping(yaml: string, path: string, contentHash: string, repoId?: string | null): UseQueryResult<DeliveryMappingParseResult> {
  return useQuery({
    queryKey: ["delivery", "mapping-parsed", repoId ?? null, path, contentHash],
    queryFn: () => deliveryApi.parseMapping(yaml, path, repoId),
  });
}
