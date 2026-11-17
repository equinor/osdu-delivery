import { useMemo, useState, type ReactNode } from "react";
import { Link as RouterLink, useSearchParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { CloudOff, FileCode, FilePen, Info, LayoutTemplate, type LucideIcon } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { GlyphRef } from "@/components/GlyphRef";
import { ExplainPageButton } from "@/features/chat/ExplainPageButton";
import { useWatchedDocument } from "@/embed/hostState";
import { useTabTitle } from "@/layout/workbench/TabsContext";
import { deliveryApi, type DeliveryMapping } from "../api/delivery";
import { MappingWorkspace } from "../features/delivery/DeliveryMappingPage";
import { KindText } from "../features/delivery/KindText";
import { mappingRoute } from "../features/delivery/mappingSearch";
import { ProblemView } from "../features/delivery/TemplateSheet";
import type { WorkspaceMapping } from "../features/delivery/workspaceMapping";
import { declaredMapping, placeWorkingCopy, textKey, workspaceMappingOf } from "./workingCopy";

/** The text the page shows, and the document it is of. */
interface ShownText {
  document: string;
  text: string;
}

function normalise(text: string): string {
  return text.replace(/\r\n/g, "\n").trim();
}

/** A line under the heading that says something about how the page is read: never a status, so it wears no status colour. */
function Note({ icon: Icon, testId, children }: { icon: LucideIcon; testId: string; children: ReactNode }) {
  return (
    <p className="flex items-start gap-1.5 text-[12px] text-muted-foreground" data-testid={testId}>
      <Icon className="mt-0.5 size-3.5 shrink-0" />
      <span className="min-w-0">{children}</span>
    </p>
  );
}

/**
 * The mapping's page over the text an editor holds: the same workspace a synced mapping's page is, read from the file,
 * whether or not the estate has seen it. What is drawn from the text (Overview, Properties, Record shape) is drawn for
 * either; what reads rows renders them with the mapping the estate synced, and says it waits for a sync while there is none.
 */
function WorkingCopy({ shown, path, unsavedAhead, onShowUnsaved }: {
  shown: ShownText;
  path: string;
  /** The editor holds text the page does not show: changes typed since the file was last saved. */
  unsavedAhead: boolean;
  onShowUnsaved: () => void;
}) {
  const declared = useMemo(() => declaredMapping(shown.text), [shown.text]);
  const contentHash = useMemo(() => textKey(shown.text), [shown.text]);
  useTabTitle(declared.reference === null ? undefined : `${declared.reference} (working copy)`);

  const mappings = useQuery({
    queryKey: ["delivery", "mappings", "all"],
    queryFn: () => deliveryApi.mappings(),
    staleTime: 30000,
  });
  const place = useMemo(
    () => (mappings.data === undefined ? null : placeWorkingCopy(mappings.data, path, declared.reference)),
    [mappings.data, path, declared.reference]);
  const synced: DeliveryMapping | null = place?.synced ?? null;

  // The text the estate synced, to say whether the rows it reads are rendered with this text or with an earlier one.
  const syncedText = useQuery({
    queryKey: ["delivery", "mapping", synced?.id ?? null],
    queryFn: () => deliveryApi.mapping(synced!.id),
    enabled: synced !== null,
  });
  const differs = syncedText.data !== undefined && normalise(syncedText.data.yaml) !== normalise(shown.text);

  // A mapping the estate has not seen settles its template as a synced one does: from its repository's template folders.
  const template = useQuery({
    queryKey: ["delivery", "template-resolve", place?.repoId ?? null, path, declared.kind],
    queryFn: () => deliveryApi.resolveTemplate(place!.repoId!, path, declared.kind!),
    enabled: place !== null && synced === null && place.repoId !== null && declared.kind !== null,
    retry: false,
  });
  const templatePath = template.data?.found === true ? template.data.file ?? "" : "";
  const mapping = useMemo<WorkspaceMapping | null>(
    () => (place === null ? null : workspaceMappingOf(place, { path, reference: declared.reference, contentHash, templatePath })),
    [place, path, declared.reference, contentHash, templatePath]);

  return (
    <div className="flex flex-col gap-2" data-testid="page-working-copy-mapping">
      <div className="flex min-w-0 flex-wrap items-center gap-x-2 gap-y-1">
        <div className="flex min-w-0 flex-[1_1_20rem] items-center gap-1.5">
          <FilePen className="size-4 shrink-0 text-muted-foreground" aria-hidden />
          <h1 className="min-w-0 truncate text-lg font-semibold leading-7" data-testid="working-copy-mapping-name">
            {declared.reference ?? path}
          </h1>
          <ExplainPageButton />
          {declared.kind !== null && (
            <span className="min-w-0 flex-1 pl-1 text-muted-foreground" data-testid="working-copy-mapping-kind"><KindText kind={declared.kind} /></span>
          )}
        </div>
        <div className="ml-auto flex shrink-0 items-center gap-1">
          <GlyphRef icon={FileCode} title="The file in the editor" body={path} mono />
          {synced !== null && (
            <Button asChild size="sm" variant="outline" data-testid="working-copy-mapping-synced">
              <RouterLink to={mappingRoute(synced.id)} title="The mapping as the estate last synced it">
                <LayoutTemplate />
                As synced
              </RouterLink>
            </Button>
          )}
        </div>
      </div>

      {unsavedAhead && (
        <Note icon={Info} testId="working-copy-mapping-unsaved">
          This is the file as last saved. The editor holds changes since then: save the file, or{" "}
          <button type="button" className="text-primary hover:underline" onClick={onShowUnsaved} data-testid="working-copy-mapping-show-unsaved">
            show them now
          </button>
          .
        </Note>
      )}
      {mappings.isError && <ProblemView error={mappings.error} testId="working-copy-mapping-error" />}
      {place !== null && synced === null && (
        <Note icon={CloudOff} testId="working-copy-mapping-local">
          The estate has not seen this mapping yet, so there are no rows to read it with. Push it and sync the repository to
          see its staged records and run a dry run.
        </Note>
      )}
      {synced !== null && differs && (
        <Note icon={Info} testId="working-copy-mapping-differs">
          Overview, Properties and Record shape show this file. Staged records and the dry run read rows with the mapping
          the estate last synced, which differs from it: push and sync to render rows with this text.
        </Note>
      )}

      {place === null && !mappings.isError && <Skeleton className="h-80 w-full" data-testid="working-copy-mapping-placing" />}
      {mapping !== null && (
        // Keyed by the mapping alone: a save draws the text again inside the workspace, and the tab, the scope and what
        // a dry run found stay as they were, where another mapping, or this one once the estate has synced it, starts afresh.
        <MappingWorkspace key={mapping.id ?? `working-copy|${path}`} detail={{ mapping, yaml: shown.text }} />
      )}
    </div>
  );
}

/**
 * A mapping document as it is being written: the page of a mapping (DeliveryMappingPage), read from the file an editor
 * has open rather than from the last sync, so what a mapping does is seen while it is written and before it is pushed.
 * The editor hosting the page names the document in the address (`?document=`) and sends its text; the page follows
 * the file as it is saved, so a line half typed never redraws it, and says so when the editor is ahead of the file.
 */
export default function WorkingCopyMappingPage() {
  const [params] = useSearchParams();
  const document = params.get("document");
  const watched = useWatchedDocument(document);

  // The text shown is the file as last saved. It is taken whenever the editor's text is the file's, and once at the
  // start whatever state the editor is in, so the page never opens empty; "show them now" takes the editor's text as it is.
  const [shown, setShown] = useState<ShownText | null>(null);
  if (watched !== null && watched.text !== null) {
    const first = shown === null || shown.document !== watched.document;
    if ((first || !watched.unsaved) && (first || shown.text !== watched.text)) {
      setShown({ document: watched.document, text: watched.text });
    }
  }

  if (document === null) {
    return <p className="text-[13px] text-muted-foreground" data-testid="working-copy-mapping-unnamed">This page shows a mapping an editor has open; open it from a mapping's file.</p>;
  }

  if (watched === null) {
    return <Skeleton className="h-80 w-full" data-testid="working-copy-mapping-waiting" />;
  }

  if (watched.text === null || watched.path === null || shown === null || shown.document !== watched.document) {
    return <p className="text-[13px] text-muted-foreground" data-testid="working-copy-mapping-gone">The file this page showed is no longer in the folder open in the editor.</p>;
  }

  return (
    <WorkingCopy
      shown={shown}
      path={watched.path}
      unsavedAhead={watched.unsaved && watched.text !== shown.text}
      onShowUnsaved={() => setShown({ document: watched.document, text: watched.text ?? shown.text })}
    />
  );
}
