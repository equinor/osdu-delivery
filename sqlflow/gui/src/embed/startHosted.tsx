import type { ReactNode } from "react";
import { createRoot } from "react-dom/client";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { MemoryRouter } from "react-router-dom";
import { isApiError, setApiTransport } from "../api/client";
import { HostedAuthProvider } from "../auth/AuthContext";
import { Toaster } from "../components/ui/sonner";
import { TooltipProvider } from "../components/ui/tooltip";
import { setRuntimeConfig } from "../config/runtime";
import { withoutLanguageEngine } from "../lib/lsp/sqlflowLsp";
import { applyRuntimeBranding, type BrandLogos } from "../modules/branding";
import { registerModules, type GuiModule } from "../modules/registry";
import { HostedThemeProvider } from "../theme/ThemeModeContext";
import { applyAppearance } from "../theme/appearance";
import { HOST_DESIGN } from "../theme/designs";
import { pageHost } from "./hostBridge";
import { connectHost, hostThemeNow, onHostStart, useHostStart, useHostTheme } from "./hostState";
import { hostTransport } from "./hostTransport";
import { installPageDoors } from "./pageDoors";
import { HOSTED_TOKEN, type HostStart } from "./protocol";

// What every hosted entry shares (embed/): how a surface an editor shows starts, and the providers it renders under.
// The pages' entry (renderHosted.tsx) and the side bar's (HostedSideBar.tsx) both build on this, and it imports no
// page, so the side bar loads without the pages' code.

/** Where a hosted page's calls go until the host names a control plane: nowhere, since a page makes none before it does. */
const NO_CONTROL_PLANE = "http://control-plane.invalid";

/** The surface a hosted entry draws, which the host design reads to stand on the right ground (host-design.css). */
export type HostedSurface = "page" | "side-bar";

function createQueryClient(): QueryClient {
  return new QueryClient({
    defaultOptions: {
      queries: {
        // As in a browser tab (bootstrap.tsx): a 4xx answer is the answer, anything else gets two more tries.
        retry: (failureCount, error) => {
          if (isApiError(error) && error.status >= 400 && error.status < 500) {
            return false;
          }

          return failureCount < 2;
        },
        staleTime: 5_000,
      },
    },
  });
}

/** What a hosted entry gives besides its modules. */
export interface HostedOptions {
  /**
   * The build's brand images, as the bundler addressed them (the entry imports them). The paths the branding names
   * (`/brand/...`) are where a deployment serves them, and lead nowhere in a folder an editor serves.
   */
  logos: BrandLogos;
}

/** Who and where a start is for: what the page's own answers were read as, and so may no longer be shown under. */
function readAs(start: HostStart): string {
  return `${start.apiBaseUrl ?? ""}\n${start.session?.subject ?? ""}`;
}

/** Why a hosted entry could not start, on the page itself: there is no other place a person would see it. */
function showFailure(container: HTMLElement, error: unknown): void {
  const message = document.createElement("p");
  message.setAttribute("role", "alert");
  message.style.cssText = "margin:16px;font:13px var(--vscode-font-family,sans-serif);color:var(--vscode-errorForeground,#c41b27)";
  message.textContent = error instanceof Error ? error.message : String(error);
  container.replaceChildren(message);
}

/**
 * What every hosted entry does before it renders: the modules registered, the host's design on, calls carried by the
 * host, the doors a page leaves by handed to it, the build's own images found where the host serves them, and the
 * host's start waited for. `render` then draws the surface; what the host says afterwards re-renders it.
 */
export function startHosted(
  modules: readonly GuiModule[],
  options: HostedOptions,
  surface: HostedSurface,
  render: (context: { queryClient: QueryClient; start: HostStart; container: HTMLElement }) => ReactNode,
): void {
  const container = document.getElementById("root");
  if (container === null) {
    throw new Error("The page has no #root element to render into.");
  }

  registerModules(modules);
  withoutLanguageEngine();
  document.documentElement.setAttribute("data-sf-surface", surface);
  applyAppearance({ design: HOST_DESIGN.id, mode: hostThemeNow().mode });

  const host = pageHost();
  setApiTransport(hostTransport(host));
  installPageDoors(host);

  applyRuntimeBranding({ logos: options.logos });

  const queryClient = createQueryClient();
  connectHost().then((start) => {
    setRuntimeConfig({ apiBaseUrl: start.apiBaseUrl ?? NO_CONTROL_PLANE });
    // Another person, or another control plane: nothing read as the last one may be shown as this one's.
    let shownAs = readAs(start);
    onHostStart((next) => {
      setRuntimeConfig({ apiBaseUrl: next.apiBaseUrl ?? NO_CONTROL_PLANE });
      if (readAs(next) !== shownAs) {
        shownAs = readAs(next);
        queryClient.clear();
      }
    });
    createRoot(container).render(render({ queryClient, start, container }));
  }, (error: unknown) => showFailure(container, error));
}

/** The providers every hosted surface renders under, in the order a browser tab nests them (bootstrap.tsx). */
export function HostedProviders({ queryClient, initialPath, children }: {
  queryClient: QueryClient;
  initialPath: string;
  children: ReactNode;
}) {
  const start = useHostStart();
  const theme = useHostTheme();
  return (
    <QueryClientProvider client={queryClient}>
      <HostedThemeProvider mode={theme.mode} revision={theme.revision}>
        <TooltipProvider delayDuration={300}>
          <MemoryRouter initialEntries={[initialPath]}>
            <HostedAuthProvider identity={start.session} token={HOSTED_TOKEN} onSignOut={signOut}>
              {children}
            </HostedAuthProvider>
          </MemoryRouter>
          <Toaster position="bottom-right" closeButton />
        </TooltipProvider>
      </HostedThemeProvider>
    </QueryClientProvider>
  );
}

function signOut(): void {
  pageHost().send({ type: "act", action: "signOut" });
}
