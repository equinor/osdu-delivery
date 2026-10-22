import { useEffect, useId, useState } from "react";
import { useMutation, useQuery } from "@tanstack/react-query";
import { GitPullRequest, Loader2, TriangleAlert } from "lucide-react";
import { toast } from "sonner";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Sheet, SheetContent, SheetDescription, SheetFooter, SheetHeader, SheetTitle } from "@/components/ui/sheet";
import { Textarea } from "@/components/ui/textarea";
import type { DeliveryBuilderRepo } from "../../api/delivery";
import { repoSourceApi } from "@/api/endpoints";
import { LinkRef } from "@/components/LinkRef";
import { problemText } from "./problemText";
import { ProblemView } from "./TemplateSheet";

interface MappingProposeSheetProps {
  open: boolean;
  onClose: () => void;
  repo: DeliveryBuilderRepo;
  /** The repository's git source, which the pull request is opened against. */
  sourceId: string;
  name: string;
  version: string;
  /** Where the mapping goes in the repository, where the check read its templates from the template folders on its path. */
  path: string;
  /** The checked YAML, exactly as the last check wrote it. */
  yaml: string;
  /** The repository's template file the mapping fills; null when the builder cannot tell it. */
  template: string | null;
}

/**
 * Proposes the mapping to its repository as a pull request, through the same proposal path flows use: the control plane
 * pushes a branch with the source's own credential and opens the pull request. Nothing reaches the catalog until a person
 * merges it and the repository syncs. The mapping fills a template the repository already holds, and goes where the check
 * read it from, so the pull request writes the mapping alone. The sheet says which credential pushes it, since a
 * read-only GitHub App (enough for syncing) cannot.
 */
export function MappingProposeSheet({ open, onClose, repo, sourceId, name, version, path, yaml, template }: MappingProposeSheetProps) {
  const ids = useId();
  const defaultTitle = `Mapping ${name}@${version}`;
  const [title, setTitle] = useState(defaultTitle);
  const [body, setBody] = useState("");

  const propose = useMutation({
    mutationFn: () => repoSourceApi.propose(sourceId, {
      title: title.trim(),
      body: body.trim() === "" ? null : body.trim(),
      baseBranch: null,
      headBranch: null,
      files: [{ path, content: yaml }],
    }),
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

  // The credential the control plane pushes the branch and opens the pull request with, as the Repos page lists it.
  const sources = useQuery({
    queryKey: ["repo-sources", "list", 200],
    queryFn: () => repoSourceApi.list({ page: 1, pageSize: 200 }),
    enabled: open,
  });
  const credential = sources.data?.items.find((source) => source.id === sourceId)?.credential;

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
          {created === undefined && credential != null && (
            <p className="text-xs text-muted-foreground" data-testid="mapping-builder-propose-credential">
              Pushed and opened with {credential.summary}.
              {credential.kind === "github-app"
                ? " A GitHub App proposes only with Contents and Pull requests set to read and write; a read-only one is refused."
                : null}
            </p>
          )}
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
              {template !== null && (
                <p className="text-xs text-muted-foreground" data-testid="mapping-builder-propose-template">
                  The mapping reads its template from <span className="font-mono">{template}</span>, which the repository holds.
                </p>
              )}
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
                disabled={title.trim() === "" || propose.isPending}
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
