import { useEffect, useMemo, useSyncExternalStore, type ReactNode } from "react";
import { Outlet, useLocation, useNavigate, useNavigationType } from "react-router-dom";
import { useQueryClient } from "@tanstack/react-query";
import { ArrowLeft, ArrowRight, ExternalLink, RotateCw } from "lucide-react";
import { ResizableHandle, ResizablePanel, ResizablePanelGroup } from "@/components/ui/resizable";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { ExplainDrawer, ExplainProvider } from "../features/chat/ExplainContext";
import { RepoFileDrawer, RepoFileWindowProvider } from "../features/repos/RepoFileWindow";
import { RunDock } from "../features/runs/RunDock";
import { RunDockProvider } from "../features/runs/RunDockContext";
import { routeTitle } from "../layout/nav";
import { PanelHost } from "../layout/workbench/PanelHost";
import { PanelProvider, usePanel } from "../layout/workbench/PanelContext";
import { SideBarRoomContext, type SideBarRoom } from "../layout/workbench/SideBarRoom";
import { TabsProvider, useWorkbenchTabs } from "../layout/workbench/TabsContext";
import { TraceIndicator } from "../layout/workbench/TraceIndicator";
import { TraceTrayProvider } from "../layout/workbench/TraceTray";
import { usePersistentLayout } from "../layout/workbench/usePersistentLayout";
import { moduleTitleBarItems } from "../modules/registry";
import { pageHost } from "./hostBridge";
import { useHostStart } from "./hostState";

// ---- Where the page stands in what it has shown ---------------------------------------------------------------------

/** The pages this tab has shown, in order, and the one in front: what Back and Forward go through. */
interface Trail {
  keys: readonly string[];
  index: number;
}

let trail: Trail = { keys: [], index: -1 };
const trailListeners = new Set<() => void>();

function readTrail(): Trail {
  return trail;
}

function subscribeTrail(listener: () => void): () => void {
  trailListeners.add(listener);
  return () => trailListeners.delete(listener);
}

/** Records the page the router now shows: a new one after the one in front, or one already on the trail. */
function recordPlace(key: string, how: "PUSH" | "POP" | "REPLACE"): void {
  if (trail.keys[trail.index] === key) {
    return;
  }

  const known = trail.keys.indexOf(key);
  if (how === "POP" && known >= 0) {
    trail = { keys: trail.keys, index: known };
  } else if (how === "REPLACE" && trail.index >= 0) {
    trail = { keys: trail.keys.map((existing, index) => (index === trail.index ? key : existing)), index: trail.index };
  } else {
    trail = { keys: [...trail.keys.slice(0, trail.index + 1), key], index: trail.index + 1 };
  }

  for (const listener of [...trailListeners]) {
    listener();
  }
}

// ---- What the host is told ------------------------------------------------------------------------------------------

/**
 * Tells the host the page shown and what it is called, whenever either changes: the host titles its tab with it, and
 * keeps the address so the tab comes back on the same page after the editor restarts. The address is kept with the page
 * too, which is where the editor reads it from when it restores the tab before the host has spoken.
 */
function useLocated(title: string, flowKind: string | undefined): void {
  const location = useLocation();
  const how = useNavigationType();
  const path = `${location.pathname}${location.search}`;
  useEffect(() => {
    recordPlace(location.key, how);
  }, [location.key, how]);
  useEffect(() => {
    const host = pageHost();
    host.keep({ path });
    host.send({ type: "located", path, title, flowKind });
  }, [path, title, flowKind]);
}

/** Shows the page the host asks for, in this tab. */
function useHostNavigation(): void {
  const navigate = useNavigate();
  useEffect(() => pageHost().listen((message) => {
    if (message.type === "navigate") {
      navigate(message.path);
    }
  }), [navigate]);
}

/** A window page's link to its host: it has no tab strip of its own to take a title from, so the menu's name stands. */
export function HostedWindowLink() {
  const { pathname } = useLocation();
  useLocated(routeTitle(pathname).title, undefined);
  useHostNavigation();
  return null;
}

// ---- The strip over a page ------------------------------------------------------------------------------------------

function BarButton({ label, disabled, onClick, children }: {
  label: string;
  disabled?: boolean;
  onClick: () => void;
  children: ReactNode;
}) {
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <button
          type="button"
          aria-label={label}
          disabled={disabled}
          onClick={onClick}
          className="flex size-6 shrink-0 items-center justify-center rounded-md text-title-bar-foreground/80 hover:bg-title-bar-foreground/10 hover:text-title-bar-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring disabled:pointer-events-none disabled:opacity-35 [&_svg]:size-3.5"
        >
          {children}
        </button>
      </TooltipTrigger>
      <TooltipContent side="bottom">{label}</TooltipContent>
    </Tooltip>
  );
}

/**
 * The strip over a hosted page, where the workbench has its title bar: back and forward through what this tab has
 * shown, read everything again, and on the right what the title bar carries that a page needs wherever it is shown,
 * the trace indicator and the controls the registered modules add (the scope every page of a module is read in), with
 * the way to the same page in a browser when the host knows the GUI's address. The editor's own tab names the page.
 */
function HostedBar() {
  const navigate = useNavigate();
  const queries = useQueryClient();
  const location = useLocation();
  const { guiBaseUrl } = useHostStart();
  const place = useSyncExternalStore(subscribeTrail, readTrail);

  return (
    <header
      data-testid="hosted-bar"
      className="flex h-8 shrink-0 select-none items-center gap-1 bg-title-bar px-2 text-title-bar-foreground shadow-[inset_0_-1px_0_var(--chrome-border)]"
    >
      <BarButton label="Back" disabled={place.index <= 0} onClick={() => navigate(-1)}><ArrowLeft /></BarButton>
      <BarButton label="Forward" disabled={place.index >= place.keys.length - 1} onClick={() => navigate(1)}><ArrowRight /></BarButton>
      <BarButton label="Read everything on this page again" onClick={() => void queries.invalidateQueries()}><RotateCw /></BarButton>
      <div className="flex-1" />
      <TraceIndicator />
      {moduleTitleBarItems().map((control) => <control.component key={control.id} />)}
      {guiBaseUrl !== null && (
        <BarButton
          label="Open this page in the browser"
          onClick={() => pageHost().send({ type: "external", url: `${guiBaseUrl}${location.pathname}${location.search}` })}
        >
          <ExternalLink />
        </BarButton>
      )}
    </header>
  );
}

// ---- The frame ------------------------------------------------------------------------------------------------------

/** Links the frame's page to the host, from inside the tabs the page reports its title to. */
function HostedPageLink() {
  const { tabs, activePath } = useWorkbenchTabs();
  const tab = tabs.find((candidate) => candidate.path === activePath);
  useLocated(tab?.title ?? routeTitle(activePath).title, tab?.flowKind);
  useHostNavigation();
  return null;
}

function Workbench() {
  const { content: panelContent } = usePanel();
  const verticalLayout = usePersistentLayout("sqlflow.hosted.layout.v");
  // A hosted page has no side bar of the workbench's beside it to fold: the host's own is the person's to fold.
  const room = useMemo((): SideBarRoom => ({ fold: () => () => undefined }), []);

  return (
    <div className="flex h-screen flex-col overflow-hidden bg-background text-foreground" data-testid="hosted-frame">
      <HostedPageLink />
      <HostedBar />
      <ResizablePanelGroup
        orientation="vertical"
        defaultLayout={verticalLayout.defaultLayout}
        onLayoutChanged={verticalLayout.onLayoutChanged}
        className="min-h-0 flex-1"
      >
        <ResizablePanel id="editor-content" minSize="0">
          <div className="relative h-full">
            <main className="h-full overflow-y-auto bg-background" data-explain-scope>
              <div className="mx-auto max-w-[1600px] p-4 md:p-6">
                <SideBarRoomContext.Provider value={room}>
                  <Outlet />
                </SideBarRoomContext.Provider>
              </div>
            </main>
            <RepoFileDrawer maximizedClassName="inset-0" />
            <ExplainDrawer maximizedClassName="inset-0" />
            {panelContent !== null && (
              <div
                aria-hidden
                className="pointer-events-none absolute inset-x-0 bottom-0 h-4 bg-gradient-to-t from-foreground/10 to-transparent dark:from-black/45"
              />
            )}
          </div>
        </ResizablePanel>
        {panelContent !== null && (
          <>
            <ResizableHandle />
            <ResizablePanel id="bottom-panel" defaultSize="35" minSize="15">
              <PanelHost />
            </ResizablePanel>
          </>
        )}
      </ResizablePanelGroup>
      <RunDock />
    </div>
  );
}

/**
 * The frame of a page an editor shows in a tab of its own (embed/): the workbench's page area and nothing of its
 * chrome. The editor already has a title bar, an activity bar, a side bar, tabs and a status bar, so the frame keeps
 * what belongs to the page, the bottom panel a page raises (a run's trace), the drawers that slide over it (a
 * repository file, an explanation) and the run dock, under the providers every workbench page reads, in the order
 * AppShell nests them.
 */
export default function HostedFrame() {
  return (
    <TabsProvider>
      <PanelProvider>
        <ExplainProvider>
          <RepoFileWindowProvider>
            <RunDockProvider>
              <TraceTrayProvider>
                <Workbench />
              </TraceTrayProvider>
            </RunDockProvider>
          </RepoFileWindowProvider>
        </ExplainProvider>
      </PanelProvider>
    </TabsProvider>
  );
}
