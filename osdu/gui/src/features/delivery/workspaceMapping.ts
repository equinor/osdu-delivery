// What a mapping's workspace (DeliveryMappingPage's MappingWorkspace: Overview, Staged records, Properties) reads of the
// mapping it shows. The workspace is the same for a mapping the estate synced and for a document an editor has open that
// the estate has not seen yet (hosted/WorkingCopyMappingPage.tsx): what it draws from the text is drawn for either, and
// what reads rows says it waits for the sync.

import type { DeliveryMapping } from "../../api/delivery";

export interface WorkspaceMapping extends Pick<DeliveryMapping, "reference" | "relativePath" | "contentHash" | "templatePath"> {
  /** The estate's id of the mapping, by which the flows rendering with it are found; null for a document it has not seen. */
  id: string | null;
  /** The synced repository whose template folders settle its template; null for a document that belongs to none. */
  repoId: string | null;
}

/** A mapping and the text the workspace shows of it. */
export interface WorkspaceDetail {
  mapping: WorkspaceMapping;
  yaml: string;
}
