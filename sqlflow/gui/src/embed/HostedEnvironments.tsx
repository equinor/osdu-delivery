import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { ArrowLeft, Check, ChevronDown, CircleAlert, KeyRound, Loader2, MoreHorizontal, Pencil, Plus, Settings2, Trash2 } from "lucide-react";
import { toast } from "sonner";
import { ConfirmDialog } from "@/components/ConfirmDialog";
import { Button } from "@/components/ui/button";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { useDebouncedValue } from "@/hooks/useDebouncedValue";
import { cn } from "@/lib/utils";
import { controlPlaneAddress } from "./controlPlaneAddress";
import {
  addressProblem,
  checkDraft,
  controlPlaneKey,
  ENVIRONMENT_STAGES,
  guessStage,
  MAX_NAME_LENGTH,
  sameControlPlane,
  savedAddress,
  STAGE_WORDS,
  type DraftProblems,
  type EnvironmentStage,
} from "./environments";
import { askHost, useEnvironmentsShown, useHostStart, type EnvironmentsView } from "./hostState";
import type { HostEnvironment, HostSession, HostStart } from "./protocol";

// ---- A stage, as every surface shows it -----------------------------------------------------------------------------

/** The classes a stage is drawn with: written out whole, so the stylesheet's build finds each one. */
const TONE: Readonly<Record<EnvironmentStage, { dot: string; text: string; line: string; tint: string; edge: string }>> = {
  development: {
    dot: "bg-environment-development",
    text: "text-environment-development",
    line: "bg-environment-development",
    tint: "bg-environment-development/10",
    edge: "border-environment-development",
  },
  test: {
    dot: "bg-environment-test",
    text: "text-environment-test",
    line: "bg-environment-test",
    tint: "bg-environment-test/10",
    edge: "border-environment-test",
  },
  production: {
    dot: "bg-environment-production",
    text: "text-environment-production",
    line: "bg-environment-production",
    tint: "bg-environment-production/15",
    edge: "border-environment-production",
  },
};

/** What each stage is for, as the form offers it. */
const STAGE_MEANING: Readonly<Record<EnvironmentStage, string>> = {
  development: "Where flows and mappings are built and tried.",
  test: "Where a change is checked before it is released.",
  production: "What the business runs on. Switching to it asks first.",
};

/** A stage's colour, as a dot; hollow for a control plane that is not one of the saved environments. */
export function StageDot({ stage, className }: { stage: EnvironmentStage | null; className?: string }) {
  return (
    <span
      aria-hidden="true"
      className={cn(
        "inline-block size-2 shrink-0 rounded-full",
        stage === null ? "border border-muted-foreground/80" : TONE[stage].dot,
        className,
      )}
    />
  );
}

/** The stage's word, in its colour; "Not saved" for a control plane that is none of the saved environments. */
export function StageWord({ stage }: { stage: EnvironmentStage | null }) {
  return stage === null
    ? <span className="text-[11px] text-muted-foreground">Not saved</span>
    : <span className={cn("text-[11px] font-medium", TONE[stage].text)}>{STAGE_WORDS[stage]}</span>;
}

/** A line in the stage's colour, across what it heads; the border's colour for a control plane that is not saved. */
export function StageLine({ stage, className }: { stage: EnvironmentStage | null; className?: string }) {
  return <div aria-hidden="true" className={cn("h-[3px] shrink-0", stage === null ? "bg-border" : TONE[stage].line, className)} />;
}

/** An address as a person reads it: host and path, without the scheme or a trailing slash. */
export function shownAddress(url: string): string {
  try {
    const parsed = new URL(url);
    return `${parsed.host}${parsed.pathname.replace(/\/+$/, "")}`;
  } catch {
    return url;
  }
}

/** What the control plane in use is called: its saved name, or else the name its address gives, or null for none. */
function inUseName(start: HostStart): string | null {
  if (start.environment !== null) {
    return start.environment.name;
  }

  return start.apiBaseUrl === null ? null : controlPlaneAddress(start.apiBaseUrl).name;
}

/** Whether a sign-in is kept for an environment, and whose: the session's own for the one in use, the file's for the rest. */
function signInOf(environment: HostEnvironment, inUse: boolean, session: HostSession | null): { held: boolean; who: string | null } {
  if (inUse) {
    return { held: session !== null, who: session?.subject ?? null };
  }

  return { held: environment.signedIn, who: environment.account };
}

// ---- Switching ------------------------------------------------------------------------------------------------------

/**
 * Switches the control plane the editor works against to a saved environment: production only after the person says so,
 * in words that name what moves with it. The host moves the editor, its assistants and the command line together, each
 * with the sign-in kept for that control plane, and closes the pages open from the one before, whose addresses name its
 * objects. Returns the switch, the environment being switched to, and the confirmation to render.
 */
function useSwitchEnvironment(start: HostStart, switched: () => void) {
  const [confirming, setConfirming] = useState<HostEnvironment | null>(null);
  const [switching, setSwitching] = useState<string | null>(null);

  const use = async (environment: HostEnvironment) => {
    setSwitching(environment.url);
    try {
      const answer = await askHost({ kind: "use", url: environment.url });
      if (answer.ok) {
        switched();
      } else {
        toast.error(answer.problem);
      }
    } finally {
      setSwitching(null);
      setConfirming(null);
    }
  };

  const switchTo = (environment: HostEnvironment) => {
    if (switching !== null || (start.environment !== null && sameControlPlane(start.environment.url, environment.url))) {
      return;
    }

    if (environment.stage === "production") {
      // Asked after the menu that offered it has closed, so the confirmation takes the focus the menu gives back.
      window.setTimeout(() => setConfirming(environment), 0);
      return;
    }

    void use(environment);
  };

  const from = inUseName(start);
  const message = confirming === null
    ? ""
    : `${confirming.name} is production. The pages here, your assistants and the sqlflow command line act on it until you `
      + `switch back${from === null ? "" : `, and the pages open from ${from} close`}. `
      + (confirming.signedIn
        ? `You are signed in to it${confirming.account === null ? "" : ` as ${confirming.account}`}.`
        : "You sign in to it next.");

  const dialog = (
    <ConfirmDialog
      open={confirming !== null}
      title={confirming === null ? "" : `Work against ${confirming.name}?`}
      message={message}
      confirmLabel={confirming === null ? "Switch" : `Switch to ${confirming.name}`}
      danger
      busy={switching !== null}
      onConfirm={() => {
        if (confirming !== null) {
          void use(confirming);
        }
      }}
      onClose={() => setConfirming(null)}
    />
  );

  return { switchTo, switching, dialog };
}

// ---- The side bar's environment bar ---------------------------------------------------------------------------------

/**
 * The environment the side bar works against, at its top while someone is signed in: a line in the stage's colour across
 * the side bar, the environment's name and stage, and a menu of the person's other environments to switch to, with the
 * ways to add and manage them. A control plane that is none of the saved environments shows hollow and offers to be
 * saved, so a person who has not named their environments yet sees where to.
 */
export function EnvironmentBar({ start }: { start: HostStart }) {
  const [, show] = useEnvironmentsShown();
  const { switchTo, switching, dialog } = useSwitchEnvironment(start, () => undefined);
  const current = start.environment;
  const stage = current?.stage ?? null;
  const name = inUseName(start) ?? "No control plane";

  return (
    <div className="shrink-0" data-testid="side-bar-environment" data-stage={stage ?? "none"}>
      <StageLine stage={stage} />
      <DropdownMenu>
        <DropdownMenuTrigger
          className={cn(
            "flex w-full items-center gap-2 px-3 py-1.5 text-left hover:bg-sidebar-accent focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring",
            stage === "production" && TONE.production.tint,
          )}
          aria-label={`Environment: ${name}${stage === null ? ", not saved" : `, ${STAGE_WORDS[stage]}`}. Switch environment`}
          data-testid="side-bar-environment-switch"
        >
          {switching !== null ? <Loader2 className="size-3 shrink-0 animate-spin text-muted-foreground" /> : <StageDot stage={stage} />}
          <span className="flex min-w-0 flex-1 items-baseline gap-2">
            <span className="truncate font-mono text-[13px] font-semibold text-foreground" data-testid="side-bar-environment-name">{name}</span>
            <StageWord stage={stage} />
          </span>
          <ChevronDown className="size-3.5 shrink-0 text-muted-foreground" />
        </DropdownMenuTrigger>
        <DropdownMenuContent align="start" className="w-[var(--radix-dropdown-menu-trigger-width)] min-w-64">
          <DropdownMenuLabel className="text-[11px] font-normal text-muted-foreground">Work against</DropdownMenuLabel>
          {start.environments.map((environment) => {
            const inUse = current !== null && sameControlPlane(current.url, environment.url);
            const signIn = signInOf(environment, inUse, start.session);
            return (
              <DropdownMenuItem
                key={environment.url}
                onSelect={() => switchTo(environment)}
                className="items-start gap-2.5 py-1.5"
                data-testid={`side-bar-environment-option-${environment.name}`}
              >
                <StageDot stage={environment.stage} className="mt-[5px]" />
                <span className="flex min-w-0 flex-1 flex-col">
                  <span className="flex items-baseline gap-2">
                    <span className="truncate font-mono text-[13px] font-medium">{environment.name}</span>
                    <StageWord stage={environment.stage} />
                  </span>
                  <span className="truncate text-[11px] text-muted-foreground">{shownAddress(environment.url)}</span>
                </span>
                {inUse
                  ? <Check className="mt-0.5 size-3.5 shrink-0" aria-label="In use" />
                  : signIn.held && <KeyRound className="mt-0.5 size-3.5 shrink-0 text-muted-foreground" aria-label="Signed in" />}
              </DropdownMenuItem>
            );
          })}
          {start.environments.length === 0 && (
            <p className="px-2 py-1.5 text-[12px] leading-snug text-muted-foreground">
              No environment is saved yet. Save each control plane you work against, with its stage, to switch between them here.
            </p>
          )}
          <DropdownMenuSeparator />
          {current === null && start.apiBaseUrl !== null && (
            <DropdownMenuItem
              onSelect={() => show({ mode: "add", url: start.apiBaseUrl ?? "", guiUrl: start.guiBaseUrl ?? "" })}
              data-testid="side-bar-environment-save-current"
            >
              <Check />
              {`Save ${name} as an environment`}
            </DropdownMenuItem>
          )}
          <DropdownMenuItem onSelect={() => show({ mode: "add", url: "", guiUrl: "" })} data-testid="side-bar-environment-add">
            <Plus />
            Add an environment
          </DropdownMenuItem>
          <DropdownMenuItem onSelect={() => show({ mode: "list" })} data-testid="side-bar-environment-manage">
            <Settings2 />
            Manage environments
          </DropdownMenuItem>
        </DropdownMenuContent>
      </DropdownMenu>
      {dialog}
    </div>
  );
}

// ---- The list -------------------------------------------------------------------------------------------------------

/** The head of a view of the environments: the way back, and what the view is. */
function PanelHead({ title, back, backLabel }: { title: string; back: () => void; backLabel: string }) {
  return (
    <div className="flex items-center gap-1 px-1.5 pt-2 pb-1">
      <button
        type="button"
        onClick={back}
        aria-label={backLabel}
        title={backLabel}
        className="flex size-7 shrink-0 items-center justify-center rounded-md text-muted-foreground hover:bg-sidebar-accent hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
        data-testid="environments-back"
      >
        <ArrowLeft className="size-4" />
      </button>
      <h2 className="truncate text-[13px] font-semibold text-foreground">{title}</h2>
    </div>
  );
}

/** One saved environment in the list: picked to switch to it, with its changes in a menu of its own. */
function EnvironmentRow({
  environment,
  inUse,
  session,
  busy,
  onSwitch,
  onEdit,
  onRemove,
}: {
  environment: HostEnvironment;
  inUse: boolean;
  session: HostSession | null;
  busy: boolean;
  onSwitch: () => void;
  onEdit: () => void;
  onRemove: () => void;
}) {
  const signIn = signInOf(environment, inUse, session);
  return (
    <li
      className={cn("flex items-start rounded-md", inUse ? "bg-sidebar-accent" : "hover:bg-sidebar-accent/60")}
      data-testid={`environment-${environment.name}`}
      data-stage={environment.stage}
      data-in-use={inUse}
    >
      <button
        type="button"
        onClick={onSwitch}
        disabled={inUse}
        aria-current={inUse ? "true" : undefined}
        className="flex min-w-0 flex-1 items-start gap-2.5 rounded-md px-2.5 py-2 text-left focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring disabled:cursor-default"
        title={inUse ? "The environment in use" : `Work against ${environment.name}`}
      >
        {busy ? <Loader2 className="mt-0.5 size-3 shrink-0 animate-spin text-muted-foreground" /> : <StageDot stage={environment.stage} className="mt-[5px]" />}
        <span className="flex min-w-0 flex-1 flex-col gap-0.5">
          <span className="flex items-baseline gap-2">
            <span className="truncate font-mono text-[13px] font-semibold text-foreground">{environment.name}</span>
            <StageWord stage={environment.stage} />
            {inUse && <span className="ml-auto shrink-0 text-[11px] text-muted-foreground">In use</span>}
          </span>
          <span className="truncate text-[11px] text-muted-foreground" title={environment.url}>{shownAddress(environment.url)}</span>
          <span className="flex min-w-0 items-center gap-1 text-[11px] text-muted-foreground">
            {signIn.held
              ? (
                <>
                  <KeyRound className="size-3 shrink-0" aria-hidden="true" />
                  <span className="truncate">{signIn.who === null ? "Signed in" : `Signed in as ${signIn.who}`}</span>
                </>
              )
              : <span>Sign in when you switch</span>}
          </span>
        </span>
      </button>
      <DropdownMenu>
        <DropdownMenuTrigger
          aria-label={`Change ${environment.name}`}
          className="mt-1.5 mr-1 flex size-7 shrink-0 items-center justify-center rounded-md text-muted-foreground hover:bg-sidebar-accent hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
          data-testid={`environment-${environment.name}-menu`}
        >
          <MoreHorizontal className="size-4" />
        </DropdownMenuTrigger>
        <DropdownMenuContent align="end">
          <DropdownMenuItem onSelect={onEdit} data-testid={`environment-${environment.name}-edit`}><Pencil />Edit</DropdownMenuItem>
          <DropdownMenuItem onSelect={onRemove} variant="destructive" data-testid={`environment-${environment.name}-remove`}><Trash2 />Remove</DropdownMenuItem>
        </DropdownMenuContent>
      </DropdownMenu>
    </li>
  );
}

/**
 * The person's environments, in the side bar in place of its menu or its sign-in: each with its stage, its address and
 * whether a sign-in is kept for it, picked to switch to it; the control plane in use when it is none of them, with the
 * way to save it; and the way to add one.
 */
function EnvironmentList({ start }: { start: HostStart }) {
  const [, show] = useEnvironmentsShown();
  const { switchTo, switching, dialog } = useSwitchEnvironment(start, () => show(null));
  const [removing, setRemoving] = useState<HostEnvironment | null>(null);
  const [removeBusy, setRemoveBusy] = useState(false);
  const current = start.environment;
  const unsaved = current === null ? start.apiBaseUrl : null;

  const remove = async (environment: HostEnvironment) => {
    setRemoveBusy(true);
    try {
      const answer = await askHost({ kind: "remove", url: environment.url });
      if (!answer.ok) {
        toast.error(answer.problem);
      }
    } finally {
      setRemoveBusy(false);
      setRemoving(null);
    }
  };

  const removingInUse = removing !== null && current !== null && sameControlPlane(removing.url, current.url);

  return (
    <div className="flex flex-col pb-4" data-testid="environments-list">
      <PanelHead title="Environments" back={() => show(null)} backLabel="Back" />
      <p className="px-3 pb-3 text-[12px] leading-snug text-muted-foreground">
        Pick the one to work against. Each keeps its own sign-in, and this editor, its assistants and the{" "}
        <span className="font-mono">sqlflow</span> command line switch together.
      </p>
      <ul className="flex flex-col gap-0.5 px-1.5" aria-label="Environments">
        {unsaved !== null && (
          <li className="flex items-start gap-2.5 rounded-md bg-sidebar-accent px-2.5 py-2" data-testid="environment-unsaved">
            <StageDot stage={null} className="mt-[5px]" />
            <span className="flex min-w-0 flex-1 flex-col gap-0.5">
              <span className="flex items-baseline gap-2">
                <span className="truncate font-mono text-[13px] font-semibold text-foreground">{controlPlaneAddress(unsaved).name}</span>
                <StageWord stage={null} />
                <span className="ml-auto shrink-0 text-[11px] text-muted-foreground">In use</span>
              </span>
              {/* A local address is its own name: said once, above. */}
              {controlPlaneAddress(unsaved).rest !== null && (
                <span className="truncate text-[11px] text-muted-foreground" title={unsaved}>{shownAddress(unsaved)}</span>
              )}
              <button
                type="button"
                onClick={() => show({ mode: "add", url: unsaved, guiUrl: start.guiBaseUrl ?? "" })}
                className="mt-0.5 self-start rounded-sm text-[12px] text-primary hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
                data-testid="environment-unsaved-save"
              >
                Save it with its stage
              </button>
            </span>
          </li>
        )}
        {start.environments.map((environment) => (
          <EnvironmentRow
            key={environment.url}
            environment={environment}
            inUse={current !== null && sameControlPlane(current.url, environment.url)}
            session={start.session}
            busy={switching !== null && sameControlPlane(switching, environment.url)}
            onSwitch={() => switchTo(environment)}
            onEdit={() => show({ mode: "edit", url: environment.url })}
            onRemove={() => window.setTimeout(() => setRemoving(environment), 0)}
          />
        ))}
      </ul>
      {start.environments.length === 0 && (
        <p className="px-3 pt-2 text-[12px] leading-snug text-muted-foreground" data-testid="environments-none">
          No environment is saved yet. Add the control planes you work against, such as dev, test and prod.
        </p>
      )}
      <div className="px-3 pt-3">
        <Button
          variant="outline"
          className="h-8 w-full gap-2 text-[13px]"
          onClick={() => show({ mode: "add", url: "", guiUrl: "" })}
          data-testid="environments-add"
        >
          <Plus />
          Add an environment
        </Button>
      </div>
      {dialog}
      <ConfirmDialog
        open={removing !== null}
        title={removing === null ? "" : `Remove ${removing.name}?`}
        message={removing === null
          ? ""
          : `${removing.name} leaves your environments. Its control plane is not changed, and a sign-in kept for it stays until it ends.`
            + (removingInUse ? " You keep working against it until you switch, shown as not saved." : "")}
        confirmLabel="Remove"
        danger
        busy={removeBusy}
        onConfirm={() => {
          if (removing !== null) {
            void remove(removing);
          }
        }}
        onClose={() => setRemoving(null)}
      />
    </div>
  );
}

// ---- The form -------------------------------------------------------------------------------------------------------

/** The name an address suggests, made a name: its first label, or a local address whole, with what a name cannot hold as '-'. */
function suggestedName(url: string): string {
  if (addressProblem(url) !== null) {
    return "";
  }

  return controlPlaneAddress(savedAddress(url)).name
    .replace(/[^A-Za-z0-9._-]+/g, "-")
    .replace(/^[^A-Za-z0-9]+/, "")
    .slice(0, MAX_NAME_LENGTH);
}

/**
 * Whether the address typed answers as a control plane, asked once typing has settled: so a mistyped address, a VPN that
 * is off or a control plane that is down shows while the person is still at the form, rather than after a browser opens
 * on nothing. It never stops a save: a control plane may be added while it is down.
 */
function Reach({ url }: { url: string }) {
  const typed = savedAddress(url);
  const settled = useDebouncedValue(typed, 600);
  const ready = settled === typed && addressProblem(settled) === null;
  const probe = useQuery({
    queryKey: ["hosted", "environment-probe", controlPlaneKey(settled)],
    queryFn: async () => {
      const answer = await askHost({ kind: "probe", url: settled });
      if (!answer.ok) {
        throw new Error(answer.problem);
      }

      if (answer.reach === null) {
        throw new Error("The editor did not say whether the address answers.");
      }

      return answer.reach;
    },
    enabled: ready,
    retry: false,
    staleTime: 15_000,
  });

  if (!ready) {
    return null;
  }

  if (probe.isPending || probe.isFetching) {
    return (
      <p className="flex items-center gap-1.5 text-[11px] text-muted-foreground" role="status" data-testid="environment-form-reach" data-reach="checking">
        <Loader2 className="size-3 animate-spin" aria-hidden="true" />
        Asking the address
      </p>
    );
  }

  if (probe.isError) {
    return <p className="text-[11px] text-muted-foreground" data-testid="environment-form-reach" data-reach="unknown">{probe.error.message}</p>;
  }

  return (
    <p
      className="flex items-start gap-1.5 text-[11px] leading-snug text-muted-foreground"
      role="status"
      data-testid="environment-form-reach"
      data-reach={probe.data.answers ? "answers" : "silent"}
    >
      <span className={cn("mt-[3px] size-2 shrink-0 rounded-full", probe.data.answers ? "bg-success" : "bg-destructive")} aria-hidden="true" />
      <span>{probe.data.detail}</span>
    </p>
  );
}

/** A field's problem, under it. */
function FieldProblem({ id, text }: { id: string; text: string | undefined }) {
  return text === undefined ? null : <p id={id} className="text-[11px] leading-snug text-destructive">{text}</p>;
}

/**
 * Adds an environment, or changes one: its control plane's address (asked whether it answers as it is typed), its name
 * (the address's first label until the person types one), its stage (the one the name says until the person picks one,
 * and none when the name says none, so production is never shown green because nobody picked), and the address of its
 * GUI, which "open in the browser" goes to. Saving the first environment, or the control plane in use, also works
 * against it, and the button says so.
 */
function EnvironmentForm({ start, view }: { start: HostStart; view: Exclude<EnvironmentsView, { mode: "list" }> }) {
  const [, show] = useEnvironmentsShown();
  const editing = view.mode === "edit"
    ? start.environments.find((environment) => sameControlPlane(environment.url, view.url)) ?? null
    : null;
  const [url, setUrl] = useState(editing?.url ?? (view.mode === "add" ? view.url : ""));
  const [name, setName] = useState<string | null>(editing?.name ?? null);
  const [stage, setStage] = useState<EnvironmentStage | null>(editing?.stage ?? null);
  const [guiUrl, setGuiUrl] = useState(editing?.guiUrl ?? (view.mode === "add" ? view.guiUrl : ""));
  const [guiShown, setGuiShown] = useState(guiUrl !== "");
  const [touched, setTouched] = useState<ReadonlySet<keyof DraftProblems>>(new Set());
  const [tried, setTried] = useState(false);
  const [saving, setSaving] = useState(false);
  const [failed, setFailed] = useState<string | null>(null);

  const shownName = name ?? suggestedName(url);
  const guessed = guessStage(shownName, url);
  const shownStage = stage ?? guessed;
  const others = start.environments.filter((environment) => editing === null || !sameControlPlane(environment.url, editing.url));
  const checked = checkDraft({ name: shownName, url, stage: shownStage, guiUrl }, others);
  const problems: DraftProblems = checked.problems ?? {};
  const said = (field: keyof DraftProblems) => (tried || touched.has(field) ? problems[field] : undefined);
  const touch = (field: keyof DraftProblems) => setTouched((before) => new Set(before).add(field));

  // The control plane in use moves with the save: the first one a person saves while working against none of theirs,
  // and the one in use when it is the one changed.
  const inUse = editing !== null && start.environment !== null && sameControlPlane(editing.url, start.environment.url);
  const savingCurrent = editing === null && start.apiBaseUrl !== null && start.environment === null
    && addressProblem(url) === null && sameControlPlane(url, start.apiBaseUrl);
  const uses = editing === null ? start.environment === null : inUse;
  const usedLabel = savingCurrent || inUse ? "Save" : uses ? "Save and work against it" : "Save";

  const save = async () => {
    setTried(true);
    setFailed(null);
    if (checked.environment === null) {
      return;
    }

    setSaving(true);
    try {
      const answer = await askHost({
        kind: "save",
        replaces: editing?.url ?? null,
        environment: checked.environment,
        use: uses && !savingCurrent,
      });
      if (!answer.ok) {
        setFailed(answer.problem);
        return;
      }

      // Saving the control plane in use, or moving to the one saved, goes back to what the person was doing: the menu,
      // or the sign-in to it. Adding one beside, or changing one, goes back to the list it is in.
      show(savingCurrent || (uses && !inUse) ? null : { mode: "list" });
    } finally {
      setSaving(false);
    }
  };

  const title = editing !== null ? `Edit ${editing.name}` : savingCurrent ? "Save the control plane in use" : "Add an environment";

  return (
    <form
      className="flex flex-col pb-4"
      data-testid="environment-form"
      noValidate
      onSubmit={(event) => {
        event.preventDefault();
        void save();
      }}
    >
      <PanelHead title={title} back={() => show({ mode: "list" })} backLabel="Back to the environments" />
      <div className="flex flex-col gap-4 px-3 pt-2">
        <div className="flex flex-col gap-1.5">
          <Label htmlFor="environment-url" className="text-[12px]">Control plane address</Label>
          <Input
            id="environment-url"
            value={url}
            onChange={(event) => setUrl(event.target.value)}
            onBlur={() => touch("url")}
            placeholder="https://sqlflow-dev.example.com"
            spellCheck={false}
            autoComplete="off"
            autoFocus={editing === null && url === ""}
            aria-invalid={said("url") !== undefined}
            aria-describedby={said("url") === undefined ? undefined : "environment-url-problem"}
            className="h-8 font-mono text-[12px] md:text-[12px]"
            data-testid="environment-form-url"
          />
          <FieldProblem id="environment-url-problem" text={said("url")} />
          {said("url") === undefined && <Reach url={url} />}
        </div>

        <div className="flex flex-col gap-1.5">
          <Label htmlFor="environment-name" className="text-[12px]">Name</Label>
          <Input
            id="environment-name"
            value={shownName}
            onChange={(event) => setName(event.target.value)}
            onBlur={() => touch("name")}
            placeholder="dev"
            spellCheck={false}
            autoComplete="off"
            maxLength={MAX_NAME_LENGTH}
            aria-invalid={said("name") !== undefined}
            aria-describedby={said("name") === undefined ? undefined : "environment-name-problem"}
            className="h-8 font-mono text-[12px] md:text-[12px]"
            data-testid="environment-form-name"
          />
          <FieldProblem id="environment-name-problem" text={said("name")} />
        </div>

        <fieldset className="flex flex-col gap-1.5">
          <legend className="mb-1.5 text-[12px] font-medium text-foreground">Stage</legend>
          <div role="radiogroup" aria-label="Stage" className="flex flex-col gap-1.5" data-testid="environment-form-stage">
            {ENVIRONMENT_STAGES.map((option) => {
              const picked = shownStage === option;
              return (
                <button
                  key={option}
                  type="button"
                  role="radio"
                  aria-checked={picked}
                  onClick={() => setStage(option)}
                  className={cn(
                    "flex items-start gap-2.5 rounded-md border px-2.5 py-2 text-left transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
                    picked ? cn(TONE[option].edge, TONE[option].tint) : "border-border hover:bg-sidebar-accent/60",
                  )}
                  data-testid={`environment-form-stage-${option}`}
                >
                  <StageDot stage={option} className="mt-[5px]" />
                  <span className="flex min-w-0 flex-1 flex-col">
                    <span className="text-[12px] font-medium text-foreground">{STAGE_WORDS[option]}</span>
                    <span className="text-[11px] leading-snug text-muted-foreground">{STAGE_MEANING[option]}</span>
                  </span>
                  {picked && <Check className="mt-0.5 size-3.5 shrink-0 text-foreground" aria-hidden="true" />}
                </button>
              );
            })}
          </div>
          {stage === null && guessed !== null && (
            <p className="text-[11px] text-muted-foreground">{`Picked because the name says ${STAGE_WORDS[guessed].toLowerCase()}. Pick another if it is not.`}</p>
          )}
          <FieldProblem id="environment-stage-problem" text={tried ? problems.stage : undefined} />
        </fieldset>

        {guiShown
          ? (
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="environment-gui" className="text-[12px]">GUI address <span className="font-normal text-muted-foreground">(optional)</span></Label>
              <Input
                id="environment-gui"
                value={guiUrl}
                onChange={(event) => setGuiUrl(event.target.value)}
                onBlur={() => touch("guiUrl")}
                placeholder="https://sqlflow-dev-gui.example.com"
                spellCheck={false}
                autoComplete="off"
                aria-invalid={said("guiUrl") !== undefined}
                className="h-8 font-mono text-[12px] md:text-[12px]"
                data-testid="environment-form-gui"
              />
              <p className="text-[11px] leading-snug text-muted-foreground">Where Open in the browser goes from a page of this environment.</p>
              <FieldProblem id="environment-gui-problem" text={said("guiUrl")} />
            </div>
          )
          : (
            <button
              type="button"
              onClick={() => setGuiShown(true)}
              className="self-start rounded-sm text-[12px] text-primary hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
              data-testid="environment-form-gui-add"
            >
              Add the GUI's address
            </button>
          )}

        {failed !== null && (
          <p className="flex items-start gap-1.5 rounded-md border border-destructive/40 px-2.5 py-2 text-[12px] leading-snug text-foreground" role="alert" data-testid="environment-form-failed">
            <CircleAlert className="mt-px size-3.5 shrink-0 text-destructive" aria-hidden="true" />
            {failed}
          </p>
        )}

        <div className="flex flex-col gap-1.5">
          <Button
            type="submit"
            disabled={saving}
            className="h-8 w-full gap-2 bg-[var(--host-button)] text-[13px] text-[var(--host-button-foreground)] hover:bg-[var(--host-button-hover)]"
            data-testid="environment-form-save"
          >
            {saving && <Loader2 className="animate-spin" />}
            {usedLabel}
          </Button>
          <Button type="button" variant="ghost" className="h-8 w-full text-[13px]" onClick={() => show({ mode: "list" })} data-testid="environment-form-cancel">
            Cancel
          </Button>
        </div>
      </div>
    </form>
  );
}

/**
 * The person's environments in the side bar, in the view the side bar was asked for: the list, or the form that adds or
 * changes one. Nothing when none is asked for. The form starts again from what it is given whenever another environment
 * or another address is asked for.
 */
export function EnvironmentsPanel({ start }: { start: HostStart }) {
  const [view] = useEnvironmentsShown();
  if (view === null) {
    return null;
  }

  return (
    <div className="min-h-full motion-safe:animate-in motion-safe:fade-in-0 motion-safe:duration-200" data-testid="side-bar-environments">
      {view.mode === "list"
        ? <EnvironmentList start={start} />
        : <EnvironmentForm key={`${view.mode}:${view.url}`} start={start} view={view} />}
    </div>
  );
}

// ---- A page's tab ---------------------------------------------------------------------------------------------------

/**
 * The environment a page's tab works against, in its bar: the stage's dot and the environment's name, and on production
 * its word on a red ground, so a page about to act on production says so where its actions are. A person who has saved no
 * environment sees nothing new.
 */
export function PageEnvironment() {
  const start = useHostStart();
  const environment = start.environment;
  if (environment === null && start.environments.length === 0) {
    return null;
  }

  const name = inUseName(start) ?? "No control plane";
  const stage = environment?.stage ?? null;
  const where = start.apiBaseUrl === null ? "" : ` at ${shownAddress(start.apiBaseUrl)}`;
  return (
    <span
      className={cn(
        "flex h-6 shrink-0 items-center gap-1.5 rounded-md px-2 text-[12px]",
        stage === "production" ? "bg-environment-production/15 text-title-bar-foreground" : "text-title-bar-foreground/80",
      )}
      title={stage === null
        ? `Working against ${name}${where}, which is not one of your saved environments`
        : `Working against ${name} (${STAGE_WORDS[stage].toLowerCase()})${where}`}
      data-testid="hosted-environment"
      data-stage={stage ?? "none"}
    >
      <StageDot stage={stage} />
      <span className="font-mono leading-none">{name}</span>
      {stage === "production" && <span className={cn("font-medium leading-none", TONE.production.text)}>Production</span>}
    </span>
  );
}

/** The line across the top of a page's tab in its environment's stage colour; none for a person with no saved environment. */
export function PageEnvironmentLine() {
  const start = useHostStart();
  const stage = start.environment?.stage ?? null;
  if (stage === null) {
    return null;
  }

  return <div aria-hidden="true" className={cn("h-0.5 shrink-0", TONE[stage].line)} data-testid="hosted-environment-line" data-stage={stage} />;
}
