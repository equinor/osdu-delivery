import { KeyRound, LogIn, PlugZap } from "lucide-react";
import { Button } from "@/components/ui/button";
import { branding } from "../modules/branding";
import { pageHost } from "./hostBridge";
import type { HostStart } from "./protocol";

/**
 * What a hosted page shows while nobody is signed in: who to sign in to, and the host's ways of doing it. The host
 * signs the person in (their browser opens on the control plane, which is where single sign-on is), and tells every
 * page of its own when it has, so this view is replaced by the page that was asked for without the person coming back
 * to press anything. `compact` is the same in the width of a side bar.
 */
export function HostedSignIn({ start, compact = false }: { start: HostStart; compact?: boolean }) {
  const brand = branding();
  const act = (action: "signIn" | "signInWithToken" | "setControlPlane") => pageHost().send({ type: "act", action });
  const hasControlPlane = start.apiBaseUrl !== null;

  const problem = start.sessionProblem !== null && (
    <p className="rounded-md border border-border bg-muted px-2.5 py-2 text-[12px] text-foreground" data-testid="hosted-sign-in-problem">
      {start.sessionProblem}
    </p>
  );
  const ways = (
    <div className="flex flex-col gap-2">
      {hasControlPlane && (
        <Button size={compact ? "sm" : "default"} onClick={() => act("signIn")} data-testid="hosted-sign-in-browser">
          <LogIn />
          Sign in with the browser
        </Button>
      )}
      <Button
        size={compact ? "sm" : "default"}
        variant={hasControlPlane ? "outline" : "default"}
        onClick={() => act("setControlPlane")}
        data-testid="hosted-set-control-plane"
      >
        <PlugZap />
        {hasControlPlane ? "Change the control plane" : "Set the control plane"}
      </Button>
      {hasControlPlane && (
        <Button size={compact ? "sm" : "default"} variant="ghost" onClick={() => act("signInWithToken")} data-testid="hosted-sign-in-token">
          <KeyRound />
          Use an access token
        </Button>
      )}
    </div>
  );
  const address = hasControlPlane && (
    <p className="truncate font-mono text-[11px] text-muted-foreground" title={start.apiBaseUrl ?? undefined} data-testid="hosted-control-plane">
      {start.apiBaseUrl}
    </p>
  );

  // In a side bar the view's own title already names the product, so the ways in come first and the words stay short.
  if (compact) {
    return (
      <div className="flex flex-col gap-2 px-3 py-1" data-testid="hosted-sign-in">
        <p className="text-[12px] text-muted-foreground">
          {hasControlPlane ? "Sign in to open the estate's pages." : "Say where the control plane is to open the estate's pages."}
        </p>
        {problem}
        {ways}
        {address}
      </div>
    );
  }

  return (
    <div className="flex min-h-screen items-center justify-center bg-background p-6" data-testid="hosted-sign-in">
      <div className="flex w-full max-w-sm flex-col gap-4 rounded-lg border border-border bg-card p-6">
        <div className="flex items-center gap-2.5">
          <img src={brand.logos.icon} alt="" className="size-9 rounded-md" />
          <div className="min-w-0">
            <div className="truncate text-[15px] font-semibold">{brand.productName}</div>
            {brand.attribution !== null && <div className="truncate text-[11px] text-muted-foreground">{brand.attribution}</div>}
          </div>
        </div>
        <p className="text-[13px] text-muted-foreground">
          {hasControlPlane
            ? "Sign in to read the estate. Your browser opens the sign-in you always use, and this editor, its assistant and the command line share it."
            : "Say where the control plane is first. Flows, mappings and templates are checked without one; the estate, its runs and the pages here need it."}
        </p>
        {problem}
        {ways}
        {address}
      </div>
    </div>
  );
}
