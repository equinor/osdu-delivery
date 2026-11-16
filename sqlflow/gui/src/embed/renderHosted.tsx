import { Suspense } from "react";
import { Route, Routes } from "react-router-dom";
import { windowPages, workbenchPages } from "../App";
import { TopProgressBar } from "../components/TopProgressBar";
import WindowFrame from "../layout/WindowFrame";
import type { GuiModule } from "../modules/registry";
import { pageHost } from "./hostBridge";
import { useHostStart } from "./hostState";
import HostedFrame, { HostedWindowLink } from "./HostedFrame";
import { HostedSignIn } from "./HostedSignIn";
import { isGuiPath } from "./protocol";
import { HostedProviders, startHosted, type HostedOptions } from "./startHosted";

/** The pages, once somebody is signed in; until then, the way to sign in, with the page asked for kept behind it. */
function HostedPages() {
  const start = useHostStart();
  if (start.session === null) {
    return <HostedSignIn start={start} />;
  }

  return (
    <Suspense fallback={<TopProgressBar />}>
      <Routes>
        <Route element={<><HostedWindowLink /><WindowFrame /></>}>
          {windowPages()}
        </Route>
        <Route element={<HostedFrame />}>
          {workbenchPages()}
        </Route>
      </Routes>
    </Suspense>
  );
}

/**
 * Renders the GUI's pages into #root for a host that shows one page per tab of its own (an editor's webview), with the
 * given modules installed. The page shown first is the one the host kept for this tab the last time it was open, else
 * the one the host wrote on #root (`data-path`), else the home page; the host may ask for another at any time.
 */
export function renderHosted(modules: readonly GuiModule[], options: HostedOptions): void {
  startHosted(modules, options, "page", ({ queryClient, container }) => {
    const kept = pageHost().kept<{ path?: unknown }>()?.path;
    const asked = container.dataset.path;
    const initialPath = isGuiPath(kept) ? kept : isGuiPath(asked) ? asked : "/";
    return (
      <HostedProviders queryClient={queryClient} initialPath={initialPath}>
        <HostedPages />
      </HostedProviders>
    );
  });
}
