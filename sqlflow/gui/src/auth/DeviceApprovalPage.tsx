import { useState, type ChangeEvent, type FormEvent, type ReactNode, type SyntheticEvent } from "react";
import { useSearchParams } from "react-router-dom";
import { useMutation } from "@tanstack/react-query";
import { Check, CircleAlert, Loader2, LockKeyhole, ShieldAlert, ShieldCheck, SquareTerminal, X } from "lucide-react";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { cn } from "@/lib/utils";
import { isApiError } from "../api/client";
import { authApi } from "../api/endpoints";
import { CorrelationError } from "../components/CorrelationError";
import { branding } from "../modules/branding";
import { useAuth } from "./AuthContext";
import { BrandDots, Lockup } from "./BrandPanel";
import {
  DEVICE_CODE_GROUP,
  DEVICE_CODE_LENGTH,
  deviceCodeBoxAt,
  deviceCodeCharacters,
  formatDeviceCode,
  isWholeDeviceCode,
} from "./deviceCode";
import { BRAND_FIELD, LOGIN_ACCENT } from "./loginStreamLanes";

/** Where the client's sign-in stands: waiting on the person, being answered, or answered either way. */
type LinkState = "waiting" | "answering" | "approved" | "denied";

// ---- The link between the client and the product ---------------------------------------------------------------------

/**
 * One of the login picture's dotted lanes, run between the client and the product: flowing toward the product while the
 * sign-in waits (faster while it is answered), solid once it is approved, still and faint once it is denied. The flow
 * runs under motion-safe only, so a system that asks for reduced motion sees the dots hold still.
 */
function Lane({ state }: { state: LinkState }) {
  const approved = state === "approved";
  return (
    <svg
      className={cn(
        "h-2 min-w-0 flex-1 overflow-visible transition-colors duration-500",
        approved ? "text-success" : state === "denied" ? "text-white/15" : "text-white/45",
      )}
      aria-hidden="true"
    >
      <line
        x1="0"
        y1="4"
        x2="100%"
        y2="4"
        stroke="currentColor"
        strokeWidth={approved ? 2 : 2.5}
        strokeLinecap="round"
        strokeDasharray={approved ? undefined : "0 8"}
        className={cn(
          state === "waiting" && "motion-safe:animate-[device-lane_1.6s_linear_infinite]",
          state === "answering" && "motion-safe:animate-[device-lane_0.45s_linear_infinite]",
        )}
      />
    </svg>
  );
}

/** The lock between the two: closed while the person decides, turning while they are answered, then the answer. */
function LinkMark({ state }: { state: LinkState }) {
  if (state === "approved") {
    return (
      <span
        className="flex size-9 shrink-0 items-center justify-center rounded-full bg-success text-success-foreground shadow-[0_0_0_6px_color-mix(in_srgb,var(--success)_20%,transparent)] motion-safe:animate-in motion-safe:zoom-in-50 motion-safe:duration-300"
        data-testid="device-link-approved"
      >
        <Check className="size-5" strokeWidth={3} />
      </span>
    );
  }

  return (
    <span className="flex size-9 shrink-0 items-center justify-center rounded-full bg-white/[0.06] text-white/75 ring-1 ring-white/15">
      {state === "denied" && <X className="size-4" />}
      {state === "answering" && <Loader2 className="size-4 animate-spin" />}
      {state === "waiting" && <LockKeyhole className="size-4" />}
    </span>
  );
}

/** One end of the link: its tile, named under it. */
function End({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="relative shrink-0">
      {children}
      <span className="absolute top-full left-1/2 mt-2 -translate-x-1/2 text-[0.6875rem] whitespace-nowrap text-white/55">{label}</span>
    </div>
  );
}

const TILE = "flex size-14 items-center justify-center rounded-2xl ring-1";

/**
 * What a device sign-in is, drawn: the client that asks on one side, the product it signs in to on the other, and the
 * lane between them that the person's answer opens or closes. The heading under it says the same in words, so the
 * picture is hidden from assistive tech.
 */
function Connection({ state }: { state: LinkState }) {
  const brand = branding();
  return (
    <div className="flex items-center gap-3 px-5 pb-7 sm:px-7" aria-hidden="true" data-testid="device-link" data-state={state}>
      <End label="Editor or terminal">
        <div className={cn(TILE, "bg-white/[0.06] text-white/85 ring-white/15")}>
          <SquareTerminal className="size-6" strokeWidth={1.75} />
        </div>
      </End>
      <Lane state={state} />
      <LinkMark state={state} />
      <Lane state={state} />
      <End label={brand.productName}>
        <img
          src={brand.logos.icon}
          alt=""
          className={cn(TILE, "object-cover shadow-lg shadow-black/30 transition-opacity duration-500 ring-white/20", state === "denied" && "opacity-50")}
        />
      </End>
    </div>
  );
}

// ---- The code ---------------------------------------------------------------------------------------------------------

/**
 * The code as the client shows it: a box per character in its two groups, over one field that takes what is typed or
 * pasted. The boxes are for the eye; the field under them, labelled "Code", is what a keyboard, a screen reader and a
 * phone's keyboard meet. Its own text is invisible, so a click puts the caret where the next character goes, and the
 * box it types into carries the caret (a selection, every box it holds). `invalid` marks every box, for a code the
 * control plane does not hold.
 */
function CodeField({
  value,
  onChange,
  autoFocus,
  readOnly,
  invalid,
  describedBy,
}: {
  value: string;
  onChange: (code: string) => void;
  autoFocus: boolean;
  readOnly: boolean;
  invalid: boolean;
  describedBy: string;
}) {
  const [focused, setFocused] = useState(false);
  const [selection, setSelection] = useState({ start: value.length, end: value.length });
  const characters = deviceCodeCharacters(value);

  function track(event: SyntheticEvent<HTMLInputElement>) {
    const field = event.currentTarget;
    setSelection({ start: field.selectionStart ?? field.value.length, end: field.selectionEnd ?? field.value.length });
  }

  function toEnd(event: SyntheticEvent<HTMLInputElement>) {
    const field = event.currentTarget;
    const end = field.value.length;
    field.setSelectionRange(end, end);
    setSelection({ start: end, end });
  }

  // A reformatted value (upper case, the dash put back) replaces the field's text, which sends the caret to its end;
  // an unchanged one leaves the caret where it was typed.
  function change(event: ChangeEvent<HTMLInputElement>) {
    const field = event.currentTarget;
    const next = formatDeviceCode(field.value);
    const caret = next === field.value ? (field.selectionStart ?? next.length) : next.length;
    setSelection({ start: caret, end: caret });
    onChange(next);
  }

  const ranged = selection.end > selection.start;
  const caretBox = Math.min(deviceCodeBoxAt(selection.start), DEVICE_CODE_LENGTH - 1);
  const selectedFrom = deviceCodeBoxAt(selection.start);
  const selectedTo = deviceCodeBoxAt(selection.end);

  function box(index: number) {
    const character = characters[index] ?? "";
    const caret = focused && !ranged && index === caretBox;
    const selected = focused && ranged && index >= selectedFrom && index < selectedTo;
    return (
      <span
        key={index}
        className={cn(
          "flex h-11 min-w-0 flex-1 items-center justify-center rounded-lg border font-mono text-xl font-semibold transition-[border-color,box-shadow,background-color] duration-150 sm:h-12 sm:text-2xl",
          character === "" ? "border-white/10 bg-white/[0.03]" : "border-white/20 bg-white/[0.07]",
          invalid && "border-destructive/70",
          caret && "border-ring ring-[3px] ring-ring/30",
          selected && "border-ring/60 bg-ring/25",
        )}
      >
        {character}
        {caret && character === "" && (
          <span className="h-6 w-0.5 rounded-full bg-foreground motion-safe:animate-[device-caret_1.2s_ease-out_infinite]" />
        )}
      </span>
    );
  }

  const indexes = Array.from({ length: DEVICE_CODE_LENGTH }, (_, index) => index);
  return (
    <div className="relative" data-testid="device-code-boxes">
      <div className="flex items-center gap-1.5 sm:gap-2" aria-hidden="true">
        <div className="flex flex-1 gap-1.5 sm:gap-2">{indexes.slice(0, DEVICE_CODE_GROUP).map(box)}</div>
        <span className="h-0.5 w-2.5 shrink-0 rounded-full bg-white/35" />
        <div className="flex flex-1 gap-1.5 sm:gap-2">{indexes.slice(DEVICE_CODE_GROUP).map(box)}</div>
      </div>
      <input
        id="device-code"
        aria-label="Code"
        aria-describedby={describedBy}
        aria-invalid={invalid || undefined}
        value={value}
        onChange={change}
        onSelect={track}
        onFocus={(event) => {
          setFocused(true);
          toEnd(event);
        }}
        onClick={toEnd}
        onBlur={() => setFocused(false)}
        readOnly={readOnly}
        autoFocus={autoFocus}
        autoComplete="off"
        autoCapitalize="characters"
        autoCorrect="off"
        spellCheck={false}
        className="absolute inset-0 size-full cursor-text rounded-lg text-base opacity-0 read-only:cursor-default"
        data-testid="device-code"
      />
    </div>
  );
}

// ---- Who signs in, and what the client may do -------------------------------------------------------------------------

/**
 * Who the client will act as: the account this browser is signed in with. Another account signs out here, and the
 * sign-in page brings the person back to this code once they have signed in with it.
 */
function Account({ subject, onSwitch }: { subject: string; onSwitch: () => void }) {
  return (
    <div className="flex items-center gap-3 rounded-xl border border-white/10 bg-white/[0.03] px-3.5 py-3" data-testid="device-account">
      <span
        className="flex size-8 shrink-0 items-center justify-center rounded-full bg-primary text-[0.8125rem] font-semibold text-primary-foreground"
        aria-hidden="true"
      >
        {subject.slice(0, 1).toUpperCase()}
      </span>
      <div className="min-w-0 flex-1">
        <div className="text-xs text-muted-foreground">Signing in as</div>
        <div className="truncate text-sm font-medium">{subject}</div>
      </div>
      <button
        type="button"
        onClick={onSwitch}
        className="shrink-0 rounded-sm text-xs text-primary hover:underline focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
        data-testid="device-switch-account"
      >
        Use another account
      </button>
    </div>
  );
}

/**
 * What an approved client may do. Every policy but `admin` lets a signed-in caller in, and a device is never granted
 * `admin` (AuthEndpoints.ApproveDeviceAsync), so it does what the person may do, as them, except administer accounts.
 */
function Reach() {
  return (
    <ul className="flex flex-col gap-2 text-[0.8125rem] text-muted-foreground" aria-label="What the client may do">
      <li className="flex items-start gap-2.5">
        <Check className="mt-0.5 size-4 shrink-0 text-success" aria-hidden="true" />
        Read the estate, start runs and propose changes as you
      </li>
      <li className="flex items-start gap-2.5">
        <ShieldCheck className="mt-0.5 size-4 shrink-0 text-success" aria-hidden="true" />
        Never more than your account may do
      </li>
      <li className="flex items-start gap-2.5">
        <ShieldCheck className="mt-0.5 size-4 shrink-0 text-success" aria-hidden="true" />
        Never manage users or roles
      </li>
    </ul>
  );
}

/** The heading every state of the page carries: what kind of page this is, what is asked or what happened, and why. */
function Heading({ title, describedById, children }: { title: string; describedById?: string; children: ReactNode }) {
  return (
    <div className="flex flex-col items-center text-center">
      <span className="text-[0.6875rem] font-semibold tracking-[0.14em] uppercase" style={{ color: LOGIN_ACCENT }}>Device sign-in</span>
      <h1 className="mt-2 text-2xl leading-tight font-semibold tracking-[-0.02em] sm:text-[1.75rem]">{title}</h1>
      <p id={describedById} className="mt-2 self-stretch text-sm leading-relaxed text-balance text-muted-foreground">{children}</p>
    </div>
  );
}

// ---- The page --------------------------------------------------------------------------------------------------------

/**
 * Where a person finishes a client's browser sign-in: an editor, an MCP server or anything else that signs in with the
 * device grant sends them here with its code. The page sits behind the GUI's sign-in, so a person who signs in with
 * Microsoft reaches it with that sign-in, which the control plane's own /device page cannot offer.
 *
 * It is a device sign-in, not a second login form: one card on the brand's navy field, under the lockup, showing the
 * client and the product with the link between them, the code in the boxes the client's own code is drawn in, the
 * account the client will act as and what it may do. Approving always takes a click on a code the person can compare
 * with the client's: a link that approved by itself would let anyone who sends one sign in as whoever opens it, which
 * the foot of the page says in a line.
 */
export default function DeviceApprovalPage() {
  const [params] = useSearchParams();
  const { session, logout } = useAuth();
  const brand = branding();
  const [arrivedWithCode] = useState(() => (params.get("code") ?? "").trim() !== "");
  const [code, setCode] = useState(() => formatDeviceCode(params.get("code") ?? ""));

  const approve = useMutation({ mutationFn: (userCode: string) => authApi.approveDevice(userCode) });
  const deny = useMutation({ mutationFn: (userCode: string) => authApi.denyDevice(userCode) });
  const pending = approve.isPending || deny.isPending;
  const failure = approve.error ?? deny.error;
  const subject = session?.subject ?? null;
  const whole = isWholeDeviceCode(code);
  const state: LinkState = approve.isSuccess
    ? "approved"
    : deny.isSuccess ? "denied" : pending ? "answering" : "waiting";

  function edit(next: string) {
    setCode(next);
    // A failure was about the code as it stood; a changed code has not been asked about yet.
    approve.reset();
    deny.reset();
  }

  function submit(event: FormEvent) {
    event.preventDefault();
    if (whole && !pending) {
      approve.mutate(code);
    }
  }

  const ask = (
    <>
      <Heading title={arrivedWithCode ? "Approve the sign-in" : "Enter your code"} describedById="device-ask">
        {arrivedWithCode
          ? `Your editor, an assistant or the command line is signing in to ${brand.productName} as you. Approve only if it shows this code.`
          : `Type the code your editor, an assistant or the command line shows, to sign it in to ${brand.productName} as you.`}
      </Heading>
      <form className="flex flex-col gap-6" onSubmit={submit}>
        <CodeField
          value={code}
          onChange={edit}
          autoFocus={!arrivedWithCode}
          readOnly={pending}
          invalid={isApiError(failure) && failure.status === 404}
          describedBy="device-ask"
        />
        <div className="h-px bg-white/10" aria-hidden="true" />
        <div className="flex flex-col gap-4">
          {subject !== null && <Account subject={subject} onSwitch={logout} />}
          <Reach />
        </div>
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
        <div className="grid grid-cols-2 gap-3">
          <Button
            type="button"
            variant="outline"
            size="lg"
            className="text-sm dark:border-white/15 dark:bg-white/[0.03] dark:hover:bg-white/[0.08]"
            disabled={pending || !whole}
            onClick={() => deny.mutate(code)}
            data-testid="device-deny"
          >
            {deny.isPending && <Loader2 className="animate-spin" />}
            Deny
          </Button>
          <Button type="submit" size="lg" className="text-sm" disabled={pending || !whole} data-testid="device-approve">
            {approve.isPending && <Loader2 className="animate-spin" />}
            Approve
          </Button>
        </div>
      </form>
    </>
  );

  const outcome = state === "approved"
    ? (
      <Heading title="Signed in">
        {`Your editor or terminal is signed in${subject === null ? "" : ` as ${subject}`}. Go back to it: you can close this tab.`}
      </Heading>
    )
    : <Heading title="Sign-in denied">Your editor or terminal stays signed out. You can close this tab.</Heading>;

  const answered = state === "approved" || state === "denied";

  // Set on the brand's navy in both modes, as the login page is: the dark tokens apply to everything on it.
  return (
    <div
      className="dark relative flex flex-1 flex-col overflow-hidden text-[0.8125rem] text-foreground"
      style={{ background: BRAND_FIELD }}
      data-testid="device-approval"
    >
      <BrandDots mask="radial-gradient(ellipse 55% 60% at 50% 48%, black 0%, transparent 75%)" />
      <div
        className="pointer-events-none absolute inset-0"
        style={{ background: "radial-gradient(42rem 34rem at 50% 45%, color-mix(in srgb, var(--brand-blue) 16%, transparent) 0%, transparent 70%)" }}
        aria-hidden="true"
      />

      <header className="relative px-6 pt-6 sm:px-10 sm:pt-8">
        <Lockup logoClassName="h-7 sm:h-8" />
      </header>

      <main className="relative flex flex-1 flex-col items-center justify-center px-4 py-10 sm:px-6">
        <div className="w-full max-w-[28rem] motion-safe:animate-in motion-safe:fade-in-0 motion-safe:slide-in-from-bottom-2 motion-safe:duration-500">
          <section
            className="flex flex-col gap-6 rounded-2xl border border-white/10 bg-[color-mix(in_srgb,var(--brand-navy-deep)_55%,transparent)] p-6 shadow-2xl shadow-black/40 backdrop-blur-md sm:p-8"
            data-testid="device-card"
            data-outcome={answered ? state : undefined}
          >
            <Connection state={state} />
            {answered ? outcome : ask}
          </section>

          {!answered && (
            <p className="mt-5 px-2 text-center text-xs leading-relaxed text-balance text-white/50" data-testid="device-warning">
              <ShieldAlert className="mr-1.5 inline size-3.5 align-[-0.2em]" aria-hidden="true" />
              Approve only a sign-in you started. Whoever holds the code you approve is signed in as you.
            </p>
          )}
        </div>
      </main>
    </div>
  );
}
