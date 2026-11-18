import type { ReactNode } from "react";
import { useQuery } from "@tanstack/react-query";
import { BookOpen, ExternalLink, FileCheck, KeyRound, LogIn, PlugZap } from "lucide-react";
import { Button } from "@/components/ui/button";
import { cn } from "@/lib/utils";
import { authApi } from "../api/endpoints";
import { branding } from "../modules/branding";
import { controlPlaneAddress } from "./controlPlaneAddress";
import { pageHost } from "./hostBridge";
import type { HostAction, HostSignIn, HostStart } from "./protocol";

function act(action: HostAction): void {
  pageHost().send({ type: "act", action });
}

/** The one button a sign-in leads with, in the editor's own button colours, as its welcome views have it. */
const LEAD_BUTTON =
  "h-8 w-full gap-2 bg-[var(--host-button)] text-[13px] text-[var(--host-button-foreground)] hover:bg-[var(--host-button-hover)]";

/** A link-like action of the host's: no box, the link colour, underlined on hover. */
function HostLink({ onClick, children, testId }: { onClick: () => void; children: ReactNode; testId: string }) {
  return (
    <button
      type="button"
      onClick={onClick}
      className="inline-flex items-center gap-1.5 rounded-sm text-[12px] text-primary hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
      data-testid={testId}
    >
      {children}
    </button>
  );
}

// ---- The product, as a person first meets it -------------------------------------------------------------------------

/** The mark, lit softly from behind, as the login page's field is. `busy` rings it while a sign-in waits. */
function Mark({ busy = false }: { busy?: boolean }) {
  const brand = branding();
  return (
    <div className="relative flex size-16 items-center justify-center">
      <div
        className="pointer-events-none absolute -inset-8 rounded-full"
        style={{ background: "radial-gradient(circle, color-mix(in srgb, var(--primary) 16%, transparent) 0%, transparent 68%)" }}
        aria-hidden="true"
      />
      {busy && (
        <div
          className="absolute inset-0 rounded-full border-2 border-[color-mix(in_srgb,var(--primary)_22%,transparent)] border-t-primary motion-safe:animate-spin"
          style={{ animationDuration: "1.4s" }}
          aria-hidden="true"
        />
      )}
      <img src={brand.logos.icon} alt="" className={cn("relative rounded-[10px] shadow-md ring-1 ring-black/10", busy ? "size-10" : "size-12")} />
    </div>
  );
}

/** The product's name and attribution, and its pitch: the login page's, in the width of a side bar. */
function Introduction() {
  const brand = branding();
  return (
    <div className="flex flex-col items-center text-center">
      <Mark />
      <div className="mt-3 text-[15px] font-semibold tracking-[-0.01em] text-foreground" data-testid="hosted-sign-in-product">{brand.productName}</div>
      {brand.attribution !== null && <div className="text-[11px] text-muted-foreground">{brand.attribution}</div>}
      <p className="mt-5 text-[13px] leading-snug font-medium text-foreground">
        <span className="block">{brand.login.headline}</span>
        <span className="block text-primary">{brand.login.headlineAccent}</span>
      </p>
      <p className="mt-2 max-w-[20rem] text-[12px] leading-relaxed text-balance text-muted-foreground">{brand.login.tagline}</p>
    </div>
  );
}

// ---- Where the person signs in ---------------------------------------------------------------------------------------

type Reach = "checking" | "answering" | "silent";

const REACH_WORDS: Record<Reach, string> = {
  checking: "Asking the control plane",
  answering: "The control plane answers",
  silent: "The control plane does not answer",
};

/**
 * Whether the control plane answers, asked of its sign-in methods, which it tells anyone: so a wrong address or a stopped
 * control plane shows before the person signs in, not after their browser opens on nothing. Asked again while it is
 * silent, so the mark turns when it comes up.
 */
function useReach(apiBaseUrl: string | null): Reach {
  const answer = useQuery({
    queryKey: ["hosted", "control-plane-answers", apiBaseUrl],
    queryFn: authApi.providers,
    enabled: apiBaseUrl !== null,
    retry: false,
    staleTime: 30_000,
    refetchInterval: (query) => (query.state.status === "error" ? 15_000 : false),
  });
  return answer.isSuccess ? "answering" : answer.isError ? "silent" : "checking";
}

function ReachMark({ reach }: { reach: Reach }) {
  return (
    <span className="relative mt-[5px] flex size-2 shrink-0" title={REACH_WORDS[reach]} data-testid="hosted-control-plane-reach" data-reach={reach}>
      {reach === "checking" && <span className="absolute inset-0 rounded-full bg-muted-foreground/60 motion-safe:animate-pulse" />}
      {reach === "answering" && <span className="absolute inset-0 rounded-full bg-success" />}
      {reach === "silent" && <span className="absolute inset-0 rounded-full bg-destructive" />}
      <span className="sr-only">{REACH_WORDS[reach]}</span>
    </span>
  );
}

/** The control plane the person signs in to: its name, the rest of its address, whether it answers, and the change. */
function ControlPlane({ start }: { start: HostStart }) {
  const reach = useReach(start.apiBaseUrl);
  const address = start.apiBaseUrl === null ? null : controlPlaneAddress(start.apiBaseUrl);
  const problem = start.sessionProblem ?? (reach === "silent" ? "It does not answer. Check the address, and that it is running." : null);

  return (
    <section className="rounded-md border border-border bg-card text-left" aria-label="Control plane" data-testid="hosted-control-plane">
      <div className="flex items-center justify-between gap-2 px-3 pt-2">
        <span className="text-[11px] font-medium tracking-[0.06em] text-muted-foreground uppercase">Control plane</span>
        <HostLink onClick={() => act("setControlPlane")} testId="hosted-set-control-plane">{address === null ? "Set" : "Change"}</HostLink>
      </div>
      {address === null
        ? <p className="px-3 pt-0.5 pb-2.5 text-[12px] text-muted-foreground">None is set yet. Name the one your team runs to sign in to it.</p>
        : (
          <div className="flex items-start gap-2 px-3 pt-0.5 pb-2.5" title={start.apiBaseUrl ?? undefined}>
            <ReachMark reach={reach} />
            <div className="min-w-0">
              <div className="truncate text-[13px] font-medium text-foreground" data-testid="hosted-control-plane-name">{address.name}</div>
              {address.rest !== null && <div className="truncate font-mono text-[11px] text-muted-foreground">{address.rest}</div>}
            </div>
          </div>
        )}
      {problem !== null && (
        <p className="border-t border-border px-3 py-2 text-[12px] leading-snug text-foreground" data-testid="hosted-sign-in-problem">{problem}</p>
      )}
    </section>
  );
}

/** The ways in: the browser first, a pasted token for a place no browser reaches. With no control plane, naming one. */
function WaysIn({ start }: { start: HostStart }) {
  if (start.apiBaseUrl === null) {
    return (
      <Button className={LEAD_BUTTON} onClick={() => act("setControlPlane")} data-testid="hosted-set-control-plane-lead">
        <PlugZap />
        Set the control plane
      </Button>
    );
  }

  return (
    <div className="flex flex-col items-center gap-2.5">
      <Button className={LEAD_BUTTON} onClick={() => act("signIn")} data-testid="hosted-sign-in-browser">
        <LogIn />
        Sign in
      </Button>
      <p className="text-center text-[11px] leading-snug text-balance text-muted-foreground">
        Your browser opens to sign you in. This editor, its assistants and the <span className="font-mono">sqlflow</span> command
        line then share the sign-in.
      </p>
      <HostLink onClick={() => act("signInWithToken")} testId="hosted-sign-in-token">
        <KeyRound className="size-3.5" />
        Use an access token instead
      </HostLink>
    </div>
  );
}

/** What works before signing in, and where to read more: kept at the foot, so the way in stays the first thing read. */
function Foot() {
  return (
    <div className="flex flex-col gap-2 text-[12px] text-muted-foreground">
      <div className="flex items-start gap-2">
        <FileCheck className="mt-px size-3.5 shrink-0" aria-hidden="true" />
        <span>Documents in this workspace are checked as you type, signed in or not.</span>
      </div>
      <button
        type="button"
        onClick={() => act("searchDocs")}
        className="flex items-center gap-2 rounded-sm text-left hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
        data-testid="hosted-sign-in-docs"
      >
        <BookOpen className="size-3.5 shrink-0" aria-hidden="true" />
        Search the documentation
      </button>
    </div>
  );
}

// ---- While the browser sign-in waits ---------------------------------------------------------------------------------

/**
 * A browser sign-in in progress: the code the browser's page shows too, so the person approves the one they started, and
 * the ways on: that page again, when it was closed or opened behind, or stopping. It turns into the signed-in view by
 * itself once they approve.
 */
function Waiting({ signIn }: { signIn: HostSignIn }) {
  return (
    <div className="flex flex-col items-center gap-5 text-center" data-testid="hosted-sign-in-waiting">
      <Mark busy />
      <div>
        <h2 className="text-[14px] font-semibold text-foreground">Continue in your browser</h2>
        <p className="mt-1 text-[12px] leading-snug text-balance text-muted-foreground">Approve the sign-in on the page that opened, if it shows this code.</p>
      </div>
      <div className="w-full">
        <div
          className="rounded-md border border-border bg-card py-3 font-mono text-[20px] font-semibold tracking-[0.2em] text-foreground select-all"
          aria-label="Sign-in code"
          data-testid="hosted-sign-in-code"
        >
          {signIn.code}
        </div>
        <p className="mt-1.5 text-[11px] text-muted-foreground">Copied to your clipboard</p>
      </div>
      {/* The ring round the mark is what moves while this waits; the words say what for. */}
      <p className="text-[12px] text-muted-foreground" role="status">Waiting for your approval</p>
      <div className="flex w-full flex-col gap-1.5">
        <Button variant="outline" className="h-8 w-full gap-2 text-[13px]" onClick={() => pageHost().send({ type: "external", url: signIn.url })} data-testid="hosted-sign-in-reopen">
          <ExternalLink />
          Open the page again
        </Button>
        <Button variant="ghost" className="h-8 w-full text-[13px]" onClick={() => act("cancelSignIn")} data-testid="hosted-sign-in-cancel">
          Cancel
        </Button>
      </div>
    </div>
  );
}

// ---- The view --------------------------------------------------------------------------------------------------------

/**
 * What a hosted page shows while nobody is signed in, the first thing a person meets of the product in their editor: the
 * product and what it is for, the control plane they sign in to and whether it answers, and the way in. The host signs
 * the person in (their browser opens on the control plane, which is where single sign-on is) and says so to every page of
 * its own, so this view is replaced by the page that was asked for without the person coming back to press anything;
 * while the browser waits for them it shows the code to approve. `compact` is the same in the height and width of a side
 * bar: the way in centred in it, and what works before signing in at its foot.
 */
export function HostedSignIn({ start, compact = false }: { start: HostStart; compact?: boolean }) {
  const body = start.signingIn !== null
    ? <Waiting signIn={start.signingIn} />
    : (
      <div className="flex flex-col gap-6">
        <Introduction />
        <ControlPlane start={start} />
        <WaysIn start={start} />
      </div>
    );

  if (compact) {
    return (
      <div className="flex min-h-full flex-col" data-testid="hosted-sign-in">
        <div className="flex flex-1 flex-col justify-center px-4 pt-8 pb-6 motion-safe:animate-in motion-safe:fade-in-0 motion-safe:duration-500">
          {body}
        </div>
        {start.signingIn === null && (
          <div className="border-t border-border px-4 py-3">
            <Foot />
          </div>
        )}
      </div>
    );
  }

  return (
    <div className="flex min-h-screen items-center justify-center bg-background p-6" data-testid="hosted-sign-in">
      <div className="flex w-full max-w-[22rem] flex-col gap-8 motion-safe:animate-in motion-safe:fade-in-0 motion-safe:slide-in-from-bottom-2 motion-safe:duration-500">
        {body}
        {start.signingIn === null && (
          <div className="border-t border-border pt-4">
            <Foot />
          </div>
        )}
      </div>
    </div>
  );
}
