import { useState, type FormEvent, type ReactNode } from "react";
import { useSearchParams } from "react-router-dom";
import { useMutation } from "@tanstack/react-query";
import { Check, CircleAlert, Loader2, ShieldCheck, X } from "lucide-react";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { isApiError } from "../api/client";
import { authApi } from "../api/endpoints";
import { CorrelationError } from "../components/CorrelationError";
import { branding } from "../modules/branding";
import { useAuth } from "./AuthContext";
import { BrandPanel, Lockup } from "./BrandPanel";
import { BRAND_FIELD } from "./loginStreamLanes";

/** The heading and the sentence under it, as the login page sets its own. */
function Heading({ title, children }: { title: string; children: ReactNode }) {
  return (
    <div>
      <h1 className="text-[1.75rem] leading-tight font-semibold tracking-[-0.02em]">{title}</h1>
      <p className="mt-2 text-sm leading-relaxed text-muted-foreground">{children}</p>
    </div>
  );
}

/** Who the client will act as: the account this browser is signed in with. */
function Account({ subject }: { subject: string }) {
  return (
    <div className="flex items-center gap-3 rounded-lg border border-border bg-card/60 px-3.5 py-3" data-testid="device-account">
      <span className="flex size-8 shrink-0 items-center justify-center rounded-full bg-primary text-[0.8125rem] font-semibold text-primary-foreground" aria-hidden="true">
        {subject.slice(0, 1).toUpperCase()}
      </span>
      <div className="min-w-0">
        <div className="text-xs text-muted-foreground">Signed in as</div>
        <div className="truncate text-sm font-medium">{subject}</div>
      </div>
    </div>
  );
}

/** How the answer ended: the mark, what happened, and where the person goes now. */
function Outcome({ approved, subject }: { approved: boolean; subject: string | null }) {
  return (
    <div className="flex flex-col gap-6" data-testid={approved ? "device-approved" : "device-denied"}>
      <span
        className={approved
          ? "flex size-12 items-center justify-center rounded-full bg-success/15 text-success ring-1 ring-success/30"
          : "flex size-12 items-center justify-center rounded-full bg-muted text-muted-foreground ring-1 ring-border"}
        aria-hidden="true"
      >
        {approved ? <Check className="size-6" /> : <X className="size-6" />}
      </span>
      {approved
        ? (
          <Heading title="You are signed in">
            {`Go back to your editor or terminal: it is signed in${subject === null ? "" : ` as ${subject}`} now. You can close this tab.`}
          </Heading>
        )
        : <Heading title="Sign-in denied">The client stays signed out. You can close this tab.</Heading>}
    </div>
  );
}

/**
 * Where a person finishes a client's browser sign-in: an editor, an MCP server or anything else that signs in with the
 * device grant sends them here with its code. The page sits behind the GUI's sign-in, so a person who signs in with
 * Microsoft reaches it with that sign-in, which the control plane's own /device page cannot offer. It is the login page's
 * second step, so it stands on the same brand field with the same panel, and says who the client will act as and what it
 * may do. Approving always takes a click on a code the person can compare with the client's: a link that approved by
 * itself would let anyone who sends one sign in as whoever opens it.
 */
export default function DeviceApprovalPage() {
  const [params] = useSearchParams();
  const { session } = useAuth();
  const brand = branding();
  const [code, setCode] = useState(params.get("code")?.trim() ?? "");

  const approve = useMutation({ mutationFn: (userCode: string) => authApi.approveDevice(userCode) });
  const deny = useMutation({ mutationFn: (userCode: string) => authApi.denyDevice(userCode) });
  const pending = approve.isPending || deny.isPending;
  const failure = approve.error ?? deny.error;
  const subject = session?.subject ?? null;

  function submit(event: FormEvent) {
    event.preventDefault();
    if (code.trim() !== "") {
      approve.mutate(code.trim());
    }
  }

  const ask = (
    <>
      <Heading title="Approve the sign-in">
        {`An editor, an assistant or the command line is asking to sign in to ${brand.productName} as you.`}
      </Heading>
      {subject !== null && <Account subject={subject} />}
      <form className="flex flex-col gap-5" onSubmit={submit}>
        <div className="flex flex-col gap-2">
          <Label htmlFor="device-code">Code</Label>
          <Input
            id="device-code"
            value={code}
            onChange={(event) => setCode(event.target.value)}
            autoComplete="off"
            spellCheck={false}
            autoFocus={code === ""}
            className="h-12 text-center font-mono text-xl font-semibold tracking-[0.25em] md:text-xl"
            data-testid="device-code"
          />
          <p className="text-xs text-muted-foreground">Approve only if this is the code your editor or terminal shows.</p>
        </div>
        <ul className="flex flex-col gap-2 text-[0.8125rem] text-muted-foreground" aria-label="What the client may do">
          <li className="flex items-start gap-2.5">
            <Check className="mt-0.5 size-4 shrink-0 text-success" aria-hidden="true" />
            Read the estate and start runs as you
          </li>
          <li className="flex items-start gap-2.5">
            <ShieldCheck className="mt-0.5 size-4 shrink-0 text-success" aria-hidden="true" />
            Never more than your account may do
          </li>
        </ul>
        {failure !== null && (
          isApiError(failure)
            ? <CorrelationError error={failure} data-testid="device-error" />
            : (
              <Alert variant="destructive" data-testid="device-error">
                <CircleAlert />
                <AlertTitle>The answer did not reach the control plane</AlertTitle>
                <AlertDescription>{failure.message}</AlertDescription>
              </Alert>
            )
        )}
        <div className="flex flex-col gap-2.5">
          <Button type="submit" size="lg" className="w-full text-sm" disabled={pending || code.trim() === ""} data-testid="device-approve">
            {approve.isPending && <Loader2 className="animate-spin" />}
            Approve
          </Button>
          <Button
            type="button"
            variant="ghost"
            size="lg"
            className="w-full text-sm"
            disabled={pending || code.trim() === ""}
            onClick={() => deny.mutate(code.trim())}
            data-testid="device-deny"
          >
            {deny.isPending && <Loader2 className="animate-spin" />}
            Deny
          </Button>
        </div>
      </form>
    </>
  );

  // Set on the brand's navy in both modes, as the login page is: the dark tokens apply to everything on it.
  return (
    <div
      className="dark relative flex flex-1 flex-col overflow-hidden text-[0.8125rem] text-foreground lg:flex-row"
      style={{ background: BRAND_FIELD }}
      data-testid="device-approval"
    >
      <BrandPanel />
      <header className="px-6 pt-8 sm:px-10 lg:hidden">
        <Lockup logoClassName="h-8" />
      </header>
      <main className="relative flex min-w-0 flex-1 flex-col">
        <div
          className="pointer-events-none absolute inset-0"
          style={{ background: "radial-gradient(40rem 32rem at 50% 45%, color-mix(in srgb, var(--brand-blue) 9%, transparent) 0%, transparent 70%)" }}
          aria-hidden="true"
        />
        <div className="relative flex flex-1 items-center justify-center px-6 py-12 sm:px-10">
          <div
            className="flex w-full max-w-[23rem] flex-col gap-7 motion-safe:animate-in motion-safe:fade-in-0 motion-safe:slide-in-from-bottom-2 motion-safe:duration-500"
            data-testid="device-card"
          >
            {approve.isSuccess || deny.isSuccess ? <Outcome approved={approve.isSuccess} subject={subject} /> : ask}
          </div>
        </div>
      </main>
    </div>
  );
}
