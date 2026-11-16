import { useEffect, useMemo, useState, type FormEvent, type ReactNode } from "react";
import { useLocation, useNavigate } from "react-router-dom";
import {
  AppWindow,
  BookOpen,
  Bot,
  ChevronDown,
  CloudOff,
  Ellipsis,
  Eye,
  FilePlus2,
  FileText,
  GitCompareArrows,
  History,
  ListChecks,
  LogIn,
  LogOut,
  PencilLine,
  Play,
  PlugZap,
  RefreshCw,
  type LucideIcon,
} from "lucide-react";
import { SearchInput } from "@/components/SearchInput";
import { NodeLabel, TreeNode, TreeRoot, type TreeState } from "@/components/Tree";
import { Avatar, AvatarFallback } from "@/components/ui/avatar";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { Skeleton } from "@/components/ui/skeleton";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { cn } from "@/lib/utils";
import { PanelProvider } from "../layout/workbench/PanelContext";
import { SideBarSections } from "../layout/workbench/SideBar";
import { flowKindIdentity, flowKindToneClasses } from "../modules/flowKinds";
import type { GuiModule } from "../modules/registry";
import { pageHost } from "./hostBridge";
import { useHostStart, useWorkspace } from "./hostState";
import { HostedSignIn } from "./HostedSignIn";
import type { DocumentAction, HostAction, HostStart, WorkspaceDocument } from "./protocol";
import { HostedProviders, startHosted, type HostedOptions } from "./startHosted";
import { folderOf, GROUP_STRETCH, groupDocuments, groupOf, termWords, type DocumentGroup } from "./workspaceListing";

/** The address the side bar's own router starts on: no page, so the menu marks none as shown. */
const NOWHERE = "/.side-bar";

function act(action: HostAction, argument?: string): void {
  pageHost().send({ type: "act", action, argument });
}

// ---- A section of the side bar --------------------------------------------------------------------------------------

function HeaderAction({ label, icon: Icon, onClick, testId }: { label: string; icon: LucideIcon; onClick: () => void; testId: string }) {
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <button
          type="button"
          aria-label={label}
          onClick={onClick}
          data-testid={testId}
          className="flex size-5 items-center justify-center rounded-sm text-muted-foreground hover:bg-sidebar-accent hover:text-sidebar-accent-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
        >
          <Icon className="size-3.5" />
        </button>
      </TooltipTrigger>
      <TooltipContent side="bottom">{label}</TooltipContent>
    </Tooltip>
  );
}

/** A folding part of the side bar under a heading set as the editor sets the headings of its own views. */
function Section({ id, label, open, onToggle, actions, children }: {
  id: string;
  label: string;
  open: boolean;
  onToggle: () => void;
  actions?: ReactNode;
  children: ReactNode;
}) {
  return (
    <section aria-label={label} data-testid={`side-bar-section-${id}`}>
      <div className="flex h-[22px] items-center gap-1 pl-1 pr-2">
        <button
          type="button"
          aria-expanded={open}
          onClick={onToggle}
          className="flex min-w-0 flex-1 items-center gap-0.5 rounded-sm text-left text-[11px] font-bold uppercase tracking-wide text-sidebar-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
        >
          <ChevronDown className={cn("size-4 shrink-0 transition-transform duration-120", !open && "-rotate-90")} />
          <span className="truncate">{label}</span>
        </button>
        {open && actions}
      </div>
      {open && <div className="pb-2">{children}</div>}
    </section>
  );
}

// ---- The working copy ----------------------------------------------------------------------------------------------

/** The glyph an action's name draws; an action of a name not here is listed by its label alone. */
const ACTION_ICONS: Readonly<Record<string, LucideIcon>> = {
  run: Play,
  page: AppWindow,
  preview: Eye,
  compare: GitCompareArrows,
  check: ListChecks,
  history: History,
};

/** What a row does on the document it lists: the actions the host marked for the row itself, and every one in a menu. */
function RowActions({ document }: { document: WorkspaceDocument }) {
  const run = (action: DocumentAction) => pageHost().send({ type: "documentAction", document: document.id, action: action.id });
  if (document.actions.length === 0) {
    return null;
  }

  return (
    // Shown while the row is pointed at, holds focus or has its menu open, so a long list reads as names, not buttons.
    <span
      className="flex shrink-0 items-center gap-0.5 opacity-0 focus-within:opacity-100 has-[[data-state=open]]:opacity-100 [[data-tree-row]:hover_&]:opacity-100 [[data-tree-row]:focus-visible_&]:opacity-100"
      onClick={(event) => event.stopPropagation()}
    >
      {document.actions.filter((action) => action.primary === true).map((action) => {
        const Icon = action.icon === undefined ? undefined : ACTION_ICONS[action.icon];
        return Icon === undefined ? null : (
          <Tooltip key={action.id}>
            <TooltipTrigger asChild>
              <button
                type="button"
                aria-label={action.label}
                onClick={() => run(action)}
                data-testid={`document-action-${action.id}`}
                className="flex size-5 items-center justify-center rounded-sm text-muted-foreground hover:bg-background hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
              >
                <Icon className="size-3.5" />
              </button>
            </TooltipTrigger>
            <TooltipContent side="bottom">{action.label}</TooltipContent>
          </Tooltip>
        );
      })}
      <DropdownMenu>
        <DropdownMenuTrigger
          aria-label={`What can be done with ${document.name}`}
          data-testid="document-menu"
          className="flex size-5 items-center justify-center rounded-sm text-muted-foreground hover:bg-background hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
        >
          <Ellipsis className="size-3.5" />
        </DropdownMenuTrigger>
        <DropdownMenuContent align="end" className="min-w-48">
          {document.actions.map((action) => {
            const Icon = action.icon === undefined ? undefined : ACTION_ICONS[action.icon];
            return (
              <DropdownMenuItem key={action.id} onSelect={() => run(action)} data-testid={`document-menu-${action.id}`}>
                {Icon !== undefined && <Icon />}
                {action.label}
              </DropdownMenuItem>
            );
          })}
        </DropdownMenuContent>
      </DropdownMenu>
    </span>
  );
}

/** How a document stands against the estate, said only when it differs from it: a row that is what runs says nothing. */
function Standing({ document }: { document: WorkspaceDocument }) {
  if (document.standing !== "differs" && document.standing !== "local") {
    return null;
  }

  const differs = document.standing === "differs";
  const Icon = differs ? PencilLine : CloudOff;
  const text = differs ? "Differs from what the estate runs" : "Not in the estate yet: push it and sync the repository";
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <span className="inline-flex shrink-0 text-muted-foreground" aria-label={text} data-testid={`document-standing-${document.standing}`}>
          <Icon className="size-3.5" />
        </span>
      </TooltipTrigger>
      <TooltipContent side="bottom">{text}</TooltipContent>
    </Tooltip>
  );
}

function DocumentRow({ document }: { document: WorkspaceDocument }) {
  const identity = document.flowKind === undefined ? null : flowKindIdentity(document.flowKind);
  const Icon = identity?.icon ?? FileText;
  const folder = folderOf(document.path);
  return (
    <TreeNode
      id={document.id}
      testId="working-copy-document"
      label={(
        <>
          <span className={cn("inline-flex shrink-0", identity === null ? "text-muted-foreground" : flowKindToneClasses(identity.tone).icon)}>
            <Icon className="size-4" />
          </span>
          <span className="min-w-0 flex-1 truncate" title={document.path}>
            {document.name}
            {folder !== "" && <span className="ml-1.5 text-[11px] text-muted-foreground">{folder}</span>}
          </span>
          <Standing document={document} />
          <RowActions document={document} />
        </>
      )}
    />
  );
}

function GroupRows({ group, shown, onMore }: { group: DocumentGroup; shown: number; onMore: () => void }) {
  const rest = group.documents.length - shown;
  return (
    <>
      {group.documents.slice(0, shown).map((document) => <DocumentRow key={document.id} document={document} />)}
      {rest > 0 && (
        <TreeNode
          id={`${group.label}#more`}
          disabled
          label={(
            <button
              type="button"
              onClick={onMore}
              className="rounded-sm text-[12px] text-primary hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
              data-testid="working-copy-more"
            >
              {`Show ${Math.min(rest, GROUP_STRETCH).toLocaleString("en-US")} more of ${rest.toLocaleString("en-US")}`}
            </button>
          )}
        />
      )}
    </>
  );
}

/**
 * The working copy as a tree: the documents of the folder open in the editor, by the sort they are, read from disk by
 * the host, so it lists what is being written before any of it is pushed. A row opens its file; the document in front
 * of the editor is the row selected, with its group unfolded.
 */
function WorkingCopy({ term }: { term: string }) {
  const workspace = useWorkspace();
  // What the person folded and unfolded by hand. Beyond it, the group of the document in front of the editor is
  // unfolded, and a term unfolds every group it finds something in; a group folded by hand stays folded through both.
  const [opened, setOpened] = useState<ReadonlySet<string>>(new Set());
  const [closed, setClosed] = useState<ReadonlySet<string>>(new Set());
  const [stretch, setStretch] = useState<ReadonlyMap<string, number>>(new Map());
  // A flow's group goes by the name its kind is shown under everywhere else, which the host does not know.
  const documents = useMemo(
    () => (workspace?.documents ?? []).map((document) => (document.flowKind === undefined
      ? document
      : { ...document, group: `${flowKindIdentity(document.flowKind).label} flows` })),
    [workspace]);
  const groups = useMemo(() => groupDocuments(documents, term), [documents, term]);
  const active = workspace?.active ?? null;
  const activeGroup = groupOf(groups, active);
  const searching = termWords(term).length > 0;
  const expanded = useMemo(() => {
    const unfolded = new Set(searching ? groups.map((group) => group.label) : opened);
    if (activeGroup !== null) {
      unfolded.add(activeGroup);
    }

    for (const label of closed) {
      unfolded.delete(label);
    }

    return unfolded;
  }, [searching, groups, opened, closed, activeGroup]);

  if (workspace === null || (workspace.reading && workspace.documents.length === 0)) {
    return (
      <div className="flex flex-col gap-1.5 px-3 py-1" data-testid="working-copy-reading">
        <Skeleton className="h-4 w-40" />
        <Skeleton className="h-4 w-32" />
        <Skeleton className="h-4 w-36" />
      </div>
    );
  }

  if (workspace.documents.length === 0) {
    return (
      <div className="flex flex-col items-start gap-2 px-3 py-1 text-[12px] text-muted-foreground" data-testid="working-copy-empty">
        <p>No flow, mapping or other pipeline document is in the folder open here.</p>
        <button
          type="button"
          onClick={() => act("newDocument")}
          className="rounded-sm text-primary hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
        >
          Start a document
        </button>
      </div>
    );
  }

  if (groups.length === 0) {
    return <p className="px-3 py-1 text-[12px] text-muted-foreground" data-testid="working-copy-no-match">No document here has a word starting with that.</p>;
  }

  const withMember = (members: ReadonlySet<string>, id: string, held: boolean): ReadonlySet<string> => {
    const next = new Set(members);
    if (held) {
      next.add(id);
    } else {
      next.delete(id);
    }

    return next;
  };
  const setOpen = (id: string, open: boolean) => {
    setOpened((current) => withMember(current, id, open));
    setClosed((current) => withMember(current, id, !open));
  };
  const labels = new Set(groups.map((group) => group.label));
  const tree: TreeState = {
    expanded,
    setOpen,
    toggle: (id) => setOpen(id, !expanded.has(id)),
    selectedId: active,
    select: (id) => {
      if (!labels.has(id)) {
        act("openDocument", id);
      }
    },
  };

  return (
    <TreeRoot label="Working copy" state={tree} className="px-1" testId="working-copy-tree">
      {groups.map((group) => (
        <TreeNode key={group.label} id={group.label} testId="working-copy-group" label={<NodeLabel text={group.label} count={group.documents.length} />}>
          <GroupRows
            group={group}
            shown={stretch.get(group.label) ?? GROUP_STRETCH}
            onMore={() => setStretch((current) => new Map(current).set(group.label, (current.get(group.label) ?? GROUP_STRETCH) + GROUP_STRETCH))}
          />
        </TreeNode>
      ))}
    </TreeRoot>
  );
}

// ---- Who is signed in ------------------------------------------------------------------------------------------------

function AccountMenu({ start }: { start: HostStart }) {
  const session = start.session;
  return (
    <DropdownMenu>
      <DropdownMenuTrigger
        aria-label={session === null ? "Not signed in" : `Signed in as ${session.subject}`}
        data-testid="side-bar-account"
        className="flex size-8 shrink-0 items-center justify-center rounded-md hover:bg-sidebar-accent focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
      >
        <Avatar className="size-6">
          <AvatarFallback className={cn("text-[11px] font-semibold", session === null ? "bg-muted text-muted-foreground" : "bg-primary text-primary-foreground")}>
            {session === null ? <LogIn className="size-3.5" /> : session.subject.slice(0, 1).toUpperCase()}
          </AvatarFallback>
        </Avatar>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end" className="w-64">
        <DropdownMenuLabel className="font-normal" data-testid="side-bar-account-subject">
          <div className="truncate text-foreground">
            {session === null ? "Not signed in" : `${session.subject}${session.role === null ? "" : ` (${session.role})`}`}
          </div>
          {start.apiBaseUrl !== null && <div className="truncate font-mono text-[11px] text-muted-foreground">{start.apiBaseUrl}</div>}
        </DropdownMenuLabel>
        <DropdownMenuSeparator />
        {session === null && start.apiBaseUrl !== null && (
          <DropdownMenuItem onSelect={() => act("signIn")} data-testid="side-bar-sign-in"><LogIn />Sign in with the browser</DropdownMenuItem>
        )}
        <DropdownMenuItem onSelect={() => act("connectAssistant")} data-testid="side-bar-connect-assistant"><Bot />Connect an AI assistant</DropdownMenuItem>
        <DropdownMenuItem onSelect={() => act("searchDocs")} data-testid="side-bar-search-docs"><BookOpen />Search the documentation</DropdownMenuItem>
        <DropdownMenuItem onSelect={() => act("setControlPlane")} data-testid="side-bar-set-control-plane">
          <PlugZap />
          {start.apiBaseUrl === null ? "Set the control plane" : "Change the control plane"}
        </DropdownMenuItem>
        {session !== null && (
          <>
            <DropdownMenuSeparator />
            <DropdownMenuItem onSelect={() => act("signOut")} data-testid="side-bar-sign-out"><LogOut />Sign out</DropdownMenuItem>
          </>
        )}
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

// ---- The side bar --------------------------------------------------------------------------------------------------

/**
 * Opens what the menu picks in a tab of the host's. The menu is the workbench's own (SideBarSections), which goes to a
 * page through the router; here the router only holds which page was picked last, so the menu marks it, and every pick,
 * the same page again included, asks the host for the page.
 */
function MenuOpens() {
  const location = useLocation();
  useEffect(() => {
    if (location.pathname !== NOWHERE) {
      pageHost().send({ type: "open", path: `${location.pathname}${location.search}` });
    }
  }, [location.key, location.pathname, location.search]);
  return null;
}

/** What the side bar keeps between loads: which of its sections are folded. */
interface KeptSideBar {
  folded?: unknown;
}

function keptFolded(): ReadonlySet<string> {
  const folded = pageHost().kept<KeptSideBar>()?.folded;
  return new Set(Array.isArray(folded) ? folded.filter((id): id is string => typeof id === "string") : []);
}

/**
 * The host's side bar: the working copy, and the estate's pages. A search first, which narrows the working copy as it
 * is typed and on Enter searches the estate; then the documents of the folder open in the editor, which need no
 * sign-in; then the workbench's own menu, each entry opening its page in a tab of the editor, or the way to sign in
 * while nobody is. Who is signed in, and to where, is one click away beside the search.
 */
function HostedSideBar() {
  const start = useHostStart();
  const navigate = useNavigate();
  const [term, setTerm] = useState("");
  const [folded, setFolded] = useState<ReadonlySet<string>>(keptFolded);

  const toggle = (id: string) => {
    const next = new Set(folded);
    if (!next.delete(id)) {
      next.add(id);
    }

    setFolded(next);
    pageHost().keep({ folded: [...next] });
  };

  const search = (event: FormEvent) => {
    event.preventDefault();
    const trimmed = term.trim();
    if (trimmed !== "" && start.session !== null) {
      navigate(`/search?q=${encodeURIComponent(trimmed)}`);
    }
  };

  return (
    <div className="flex h-screen min-h-0 flex-col bg-sidebar text-sidebar-foreground" data-testid="hosted-side-bar">
      <MenuOpens />
      <div className="flex shrink-0 items-center gap-1 border-b border-sidebar-border px-2 py-1.5">
        <form onSubmit={search} className="min-w-0 flex-1">
          <SearchInput
            value={term}
            onChange={setTerm}
            placeholder="Find a document"
            label="Find a document of the working copy; Enter searches the estate"
            testId="side-bar-search"
            className="sm:w-full"
          />
        </form>
        <AccountMenu start={start} />
      </div>
      <div className="min-h-0 flex-1 overflow-y-auto py-1">
        <Section
          id="working-copy"
          label="Working copy"
          open={!folded.has("working-copy")}
          onToggle={() => toggle("working-copy")}
          actions={(
            <>
              <HeaderAction label="New document" icon={FilePlus2} onClick={() => act("newDocument")} testId="working-copy-new" />
              <HeaderAction label="Check every document" icon={ListChecks} onClick={() => act("validateWorkspace")} testId="working-copy-validate" />
              <HeaderAction label="Read the folder again" icon={RefreshCw} onClick={() => act("refreshWorkspace")} testId="working-copy-refresh" />
            </>
          )}
        >
          <WorkingCopy term={term} />
        </Section>
        <Section id="pages" label="Estate" open={!folded.has("pages")} onToggle={() => toggle("pages")}>
          {start.session === null
            ? <HostedSignIn start={start} compact />
            : (
              <nav aria-label="Pages of the estate">
                <SideBarSections reveal={null} />
              </nav>
            )}
        </Section>
      </div>
    </div>
  );
}

/**
 * Renders the host's side bar into #root, with the given modules installed: their pages are in its menu, and their
 * flow kinds draw the working copy's rows.
 */
export function renderHostedSideBar(modules: readonly GuiModule[], options: HostedOptions): void {
  startHosted(modules, options, "side-bar", ({ queryClient }) => (
    <HostedProviders queryClient={queryClient} initialPath={NOWHERE}>
      <PanelProvider>
        <HostedSideBar />
      </PanelProvider>
    </HostedProviders>
  ));
}
