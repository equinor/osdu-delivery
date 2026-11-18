import { useEffect } from "react";
import { useLocation } from "react-router-dom";
import { BookOpen, Bot, LogOut, PlugZap } from "lucide-react";
import { Avatar, AvatarFallback } from "@/components/ui/avatar";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { PanelProvider } from "../layout/workbench/PanelContext";
import { SideBarSearch, SideBarSections } from "../layout/workbench/SideBar";
import type { GuiModule } from "../modules/registry";
import { pageHost } from "./hostBridge";
import { useHostStart } from "./hostState";
import { HostedSignIn } from "./HostedSignIn";
import { HostedSync } from "./HostedSync";
import type { HostAction, HostSession, HostStart } from "./protocol";
import { HostedProviders, startHosted, type HostedOptions } from "./startHosted";

/** The address the side bar's own router starts on: no page, so the menu marks none as shown. */
const NOWHERE = "/.side-bar";

function act(action: HostAction): void {
  pageHost().send({ type: "act", action });
}

// ---- Who is signed in ------------------------------------------------------------------------------------------------

function AccountMenu({ start, session }: { start: HostStart; session: HostSession }) {
  return (
    <DropdownMenu>
      <DropdownMenuTrigger
        aria-label={`Signed in as ${session.subject}`}
        data-testid="side-bar-account"
        className="flex size-8 shrink-0 items-center justify-center rounded-md hover:bg-sidebar-accent focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
      >
        <Avatar className="size-6">
          <AvatarFallback className="bg-primary text-[11px] font-semibold text-primary-foreground">
            {session.subject.slice(0, 1).toUpperCase()}
          </AvatarFallback>
        </Avatar>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end" className="w-64">
        <DropdownMenuLabel className="font-normal" data-testid="side-bar-account-subject">
          <div className="truncate text-foreground">
            {`${session.subject}${session.role === null ? "" : ` (${session.role})`}`}
          </div>
          {start.apiBaseUrl !== null && <div className="truncate font-mono text-[11px] text-muted-foreground">{start.apiBaseUrl}</div>}
        </DropdownMenuLabel>
        <DropdownMenuSeparator />
        <DropdownMenuItem onSelect={() => act("connectAssistant")} data-testid="side-bar-connect-assistant"><Bot />Connect an AI assistant</DropdownMenuItem>
        <DropdownMenuItem onSelect={() => act("searchDocs")} data-testid="side-bar-search-docs"><BookOpen />Search the documentation</DropdownMenuItem>
        <DropdownMenuItem onSelect={() => act("setControlPlane")} data-testid="side-bar-set-control-plane">
          <PlugZap />
          {start.apiBaseUrl === null ? "Set the control plane" : "Change the control plane"}
        </DropdownMenuItem>
        <DropdownMenuSeparator />
        <DropdownMenuItem onSelect={() => act("signOut")} data-testid="side-bar-sign-out"><LogOut />Sign out</DropdownMenuItem>
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

/**
 * The host's side bar: the workbench's own navigation, each entry opening its page in a tab of the editor. A header
 * first, as the workbench's side bar has one: its search, which opens the search page; the sync of a repository on the
 * control plane signed in to; and who is signed in, and to where. Under it the menu. While nobody is signed in the side
 * bar is the way in alone (HostedSignIn), since nothing of the header would answer before then. The files of the folder
 * open in the editor are the editor's own Explorer's, and a flow is run from its page.
 */
function HostedSideBar() {
  const start = useHostStart();
  const session = start.session;
  return (
    <div className="flex h-screen min-h-0 flex-col bg-sidebar text-sidebar-foreground" data-testid="hosted-side-bar">
      <MenuOpens />
      {session === null
        ? (
          <div className="min-h-0 flex-1 overflow-y-auto">
            <HostedSignIn start={start} compact />
          </div>
        )
        : (
          <>
            <div className="flex shrink-0 items-center gap-1 border-b border-sidebar-border px-2 py-1.5">
              <SideBarSearch className="min-w-0 flex-1 border-b-0 p-0" />
              <HostedSync start={start} />
              <AccountMenu start={start} session={session} />
            </div>
            <nav aria-label="Pages" className="min-h-0 flex-1 overflow-y-auto py-1" data-testid="side-bar-menu">
              <SideBarSections reveal={null} />
            </nav>
          </>
        )}
    </div>
  );
}

/** Renders the host's side bar into #root, with the given modules installed: their pages are in its menu. */
export function renderHostedSideBar(modules: readonly GuiModule[], options: HostedOptions): void {
  startHosted(modules, options, "side-bar", ({ queryClient }) => (
    <HostedProviders queryClient={queryClient} initialPath={NOWHERE}>
      <PanelProvider>
        <HostedSideBar />
      </PanelProvider>
    </HostedProviders>
  ));
}
