import type { ReactNode } from "react";
import { FolderGit2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { RelativeTime } from "@/components/RelativeTime";
import { ALL_REPOSITORIES, pickedRepository, type GridRepository } from "./repositoryRows";

/** A repository's node in a grid: its name, then in a quieter voice how many rows it holds and when it was synced. */
export function RepositoryHeading({ name, count, seenUtc, problem }: {
  name: string;
  count: string;
  seenUtc: string | null;
  problem: string | null;
}) {
  return (
    <span className="flex min-w-0 items-baseline gap-2 py-0.5 text-[13px]" data-testid="repository-group">
      <FolderGit2 className="size-3.5 shrink-0 self-center text-muted-foreground" aria-hidden />
      <span className="truncate font-medium" data-testid="repository-group-name">{name}</span>
      <span className="shrink-0 text-xs text-muted-foreground">{count}</span>
      {seenUtc !== null && (
        <span className="shrink-0 text-xs text-muted-foreground">
          {"synced "}
          <RelativeTime value={seenUtc} absolute={false} />
        </span>
      )}
      {problem !== null && <span className="shrink-0 text-xs font-medium text-destructive">{problem}</span>}
    </span>
  );
}

/**
 * The repository a grid lists: every one, or one of them. With one repository there is nothing to pick, and no picker.
 * A pick no longer among the repositories reads as every one (`pickedRepository`).
 */
export function RepositoryPicker({ value, onChange, repositories, testId }: {
  value: string;
  onChange: (value: string) => void;
  repositories: readonly GridRepository[];
  testId: string;
}) {
  if (repositories.length < 2) {
    return null;
  }

  const picked = pickedRepository(value, repositories);
  return (
    <Select value={picked} onValueChange={onChange}>
      <SelectTrigger size="sm" className="h-8 w-56" active={picked !== ALL_REPOSITORIES} aria-label="Repository" data-testid={testId}>
        <SelectValue />
      </SelectTrigger>
      <SelectContent>
        <SelectItem value={ALL_REPOSITORIES}>Every repository</SelectItem>
        {repositories.map((repository) => <SelectItem key={repository.id} value={repository.id}>{repository.name}</SelectItem>)}
      </SelectContent>
    </Select>
  );
}

/**
 * The line a grid opens with when some of its rows are a problem: how many, and a button that lists only them, or every
 * row again. A grid with no problem has no line, so a healthy list starts with its rows.
 */
export function ProblemsLine({ problem, only, onOnly, icon, testId }: {
  /** What is wrong, counted: "2 mappings do not load". */
  problem: string;
  only: boolean;
  onOnly: (only: boolean) => void;
  icon: ReactNode;
  testId: string;
}) {
  return (
    <div className="flex h-9 items-center gap-2 border-b border-border px-3 text-[13px]" data-testid={testId}>
      {icon}
      <span className="min-w-0 truncate font-medium">{problem}</span>
      <Button variant="ghost" size="xs" className="ml-auto shrink-0" onClick={() => onOnly(!only)} data-testid={`${testId}-toggle`}>
        {only ? "List every row" : "List only these"}
      </Button>
    </div>
  );
}
