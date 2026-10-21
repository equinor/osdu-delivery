import { useEffect, useId, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { GitPullRequest, Loader2, TriangleAlert } from "lucide-react";
import { toast } from "sonner";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Sheet, SheetContent, SheetDescription, SheetFooter, SheetHeader, SheetTitle } from "@/components/ui/sheet";
import { Textarea } from "@/components/ui/textarea";
import { deliveryApi, type DeliveryBuilderRepo } from "../../api/delivery";
import { repoSourceApi } from "@/api/endpoints";
import { LinkRef } from "@/components/LinkRef";
import { problemText } from "./problemText";
import { templateSchemaQuery } from "./templateFormat";
import { ProblemView } from "./TemplateSheet";

interface MappingProposeSheetProps {
  open: boolean;
  onClose: () => void;
  repo: DeliveryBuilderRepo;
  /** The repository's git source, which the pull request is opened against. */
  sourceId: string;
  name: string;
  version: string;
  /** The checked YAML, exactly as the last check wrote it. */
  yaml: string;
  /** The template the mapping fills: its kind and the saved version the builder started it from. */
  templateKind: string;
  templateVersion: string;
}

/**
 * Proposes the mapping to its repository as a pull request, through the same proposal path flows use: the control plane
 * pushes a branch with the source's own credential and opens the pull request. Nothing reaches the catalog until a person
 * merges it and the repository syncs. A mapping reads its template from the template folders on its path, so when no
 * folder there holds the version the mapping fills, the pull request also writes the saved version's file into the
 * repository's top template folder, where the mapping reads it.
 */
export function MappingProposeSheet({ open, onClose, repo, sourceId, name, version, yaml, templateKind, templateVersion }: MappingProposeSheetProps) {
  const ids = useId();
  const queryClient = useQueryClient();
  const path = `mappings/${name}@${version}.yaml`;
  const defaultTitle = `Mapping ${name}@${version}`;
  const [title, setTitle] = useState(defaultTitle);
  const [body, setBody] = useState("");

  const held = useQuery({
    queryKey: ["delivery", "templates", "resolve", repo.repoId, path, templateKind, templateVersion],
    queryFn: () => deliveryApi.resolveTemplate(repo.repoId, path, templateKind, templateVersion),
    enabled: open,
  });
  const resolution = held.data;

  const propose = useMutation({
    mutationFn: async () => {
      const files = [{ path, content: yaml }];
      if (resolution !== undefined && !resolution.found) {
        files.push({ path: resolution.suggestedPath, content: await queryClient.fetchQuery(templateSchemaQuery(templateKind, templateVersion)) });
      }

      return repoSourceApi.propose(sourceId, {
        title: title.trim(),
        body: body.trim() === "" ? null : body.trim(),
        baseBranch: null,
        headBranch: null,
        files,
      });
    },
    onSuccess: (created) => toast.success(`Pull request #${created.pullRequestNumber} opened on ${repo.name}.`),
    onError: (error) => toast.error(problemText(error)),
  });

  // Every opening, and another mapping while open, starts from the default title and an empty description.
  const seedFor = open ? defaultTitle : null;
  const [seededFor, setSeededFor] = useState(seedFor);
  if (seedFor !== seededFor) {
    setSeededFor(seedFor);
    if (seedFor !== null) {
      setTitle(seedFor);
      setBody("");
    }
  }

  // The mutation is an external store, so the last opening's pull request or failure is cleared from it afterwards.
  const { reset } = propose;
  useEffect(() => {
    if (open) {
      reset();
    }
  }, [open, defaultTitle, reset]);

  const created = propose.data;

  return (
    <Sheet open={open} onOpenChange={(next) => { if (!next && !propose.isPending) { onClose(); } }}>
      <SheetContent className="w-full gap-0 sm:max-w-lg" data-testid="mapping-builder-propose-dialog">
        <SheetHeader>
          <SheetTitle>Propose to repository</SheetTitle>
          <SheetDescription>
            Opens a pull request on {repo.name}
            {repo.sourceBranch !== null ? ` against ${repo.sourceBranch}` : ""} that writes {path}. Nothing is delivered with
            it until it is merged and the repository syncs.
          </SheetDescription>
        </SheetHeader>
        <div className="flex flex-1 flex-col gap-4 overflow-y-auto px-4 pb-4">
          {propose.isError && <ProblemView error={propose.error} testId="mapping-builder-propose-error" />}
          {created !== undefined ? (
            <div className="flex flex-col gap-2 rounded-lg border border-border p-3" data-testid="mapping-builder-propose-result">
              <p className="text-[13px] font-medium">Pull request #{created.pullRequestNumber} is open.</p>
              <LinkRef
                url={created.pullRequestUrl}
                title="Pull request"
                variant="inline"
                maxWidth={380}
                testId="mapping-builder-propose-link"
                copyTestId="mapping-builder-propose-link-copy"
              />
              <p className="text-xs text-muted-foreground">
                Branch <span className="font-mono">{created.headBranch}</span>, commit{" "}
                <span className="font-mono">{created.commitSha.slice(0, 12)}</span>, {created.filesChanged} file
                {created.filesChanged === 1 ? "" : "s"} changed.
              </p>
              {created.warnings.length > 0 && (
                <Alert data-testid="mapping-builder-propose-warnings">
                  <TriangleAlert />
                  <AlertTitle>
                    The preflight found {created.warnings.length} warning{created.warnings.length === 1 ? "" : "s"}
                  </AlertTitle>
                  <AlertDescription>
                    <ul className="flex list-disc flex-col gap-0.5 pl-4">
                      {created.warnings.map((warning, index) => <li key={`${index}-${warning}`}>{warning}</li>)}
                    </ul>
                    <p>They are in the pull request description too, for the reviewer.</p>
                  </AlertDescription>
                </Alert>
              )}
            </div>
          ) : (
            <>
              <div className="flex flex-col gap-1.5">
                <Label htmlFor={`${ids}-title`}>Title</Label>
                <Input
                  id={`${ids}-title`}
                  className="h-8"
                  value={title}
                  onChange={(event) => setTitle(event.target.value)}
                  data-testid="mapping-builder-propose-title"
                />
              </div>
              <div className="flex flex-col gap-1.5">
                <Label htmlFor={`${ids}-body`}>Description (optional)</Label>
                <Textarea
                  id={`${ids}-body`}
                  className="field-sizing-fixed h-28 resize-y"
                  placeholder="What the mapping is for, and what a reviewer should check."
                  value={body}
                  onChange={(event) => setBody(event.target.value)}
                  data-testid="mapping-builder-propose-body"
                />
              </div>
              {held.isError && <ProblemView error={held.error} testId="mapping-builder-propose-template-error" />}
              {resolution !== undefined && (resolution.found ? (
                <p className="text-xs text-muted-foreground" data-testid="mapping-builder-propose-template">
                  The mapping reads template version <span className="font-mono">{templateVersion}</span> from{" "}
                  <span className="font-mono">{resolution.file}</span>, which the repository already holds.
                </p>
              ) : (
                <p className="text-xs text-muted-foreground" data-testid="mapping-builder-propose-template">
                  No template folder on the mapping's path holds template version <span className="font-mono">{templateVersion}</span>,
                  so the pull request also writes the saved version as <span className="font-mono">{resolution.suggestedPath}</span>,
                  where the mapping reads it.
                </p>
              ))}
              <p className="text-xs text-muted-foreground">
                The mapping is <span className="font-mono">{path}</span>. An existing file at a path is revised, and a new one is added.
              </p>
            </>
          )}
        </div>
        <SheetFooter className="flex-row justify-end gap-2 border-t border-border">
          {created !== undefined ? (
            <Button size="sm" onClick={onClose} data-testid="mapping-builder-propose-done">Done</Button>
          ) : (
            <>
              <Button variant="ghost" size="sm" onClick={onClose} disabled={propose.isPending} data-testid="mapping-builder-propose-cancel">
                Cancel
              </Button>
              <Button
                size="sm"
                onClick={() => propose.mutate()}
                disabled={title.trim() === "" || propose.isPending || resolution === undefined}
                data-testid="mapping-builder-propose-submit"
              >
                {propose.isPending ? <Loader2 className="animate-spin" /> : <GitPullRequest />}
                Open pull request
              </Button>
            </>
          )}
        </SheetFooter>
      </SheetContent>
    </Sheet>
  );
}
