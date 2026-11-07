import type { MouseEvent, ReactNode } from "react";
import { Link, useNavigate } from "react-router-dom";
import { format, formatDistanceToNow } from "date-fns";
import {
  AlertTriangle, CheckCircle2, CircleDashed, Hourglass, Loader2, Lock, PauseCircle, PowerOff, Puzzle, Undo2, XCircle, type LucideIcon,
} from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Card } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { ExplainTip } from "@/components/ExplainTip";
import { RelativeTime } from "@/components/RelativeTime";
import { RichTooltip } from "@/components/RichTooltip";
import { parseUtc } from "@/lib/time";
import { cn } from "@/lib/utils";
import type { PipelineSummary } from "@/api/types";
import type { DeliveryFlowStats } from "../../api/delivery";
import { deliversTo } from "./deliveryTotals";
import {
  deliveredPercent, flowVerdict, percentOf, ringArcs, statusShares, type StatusKey, type StatusShare, type Verdict, type VerdictTarget,
} from "./flowHealth";
import { STATUS_LOOK, TONE_INK } from "./statusLook";

/** The glyph each verdict leads with, in its tone. */
const VERDICT_GLYPH: Record<Verdict["kind"], LucideIcon> = {
  "submission-failed": XCircle,
  failed: XCircle,
  held: PauseCircle,
  drifted: AlertTriangle,
  delivering: Loader2,
  waiting: Hourglass,
  reverted: Undo2,
  empty: CircleDashed,
  delivered: CheckCircle2,
};

/** The counts every card shows, in one place on each, so a column of cards reads down. */
const FIGURES: { key: StatusKey; label: string; alarm: boolean }[] = [
  { key: "delivered", label: "Delivered", alarm: false },
  { key: "pending", label: "Pending", alarm: false },
  { key: "held", label: "Held", alarm: true },
  { key: "failed", label: "Failed", alarm: true },
];

const SUBMISSION_GLYPH: Record<string, { icon: LucideIcon; ink: string; spin?: boolean }> = {
  completed: { icon: CheckCircle2, ink: "text-success" },
  failed: { icon: XCircle, ink: "text-destructive" },
  running: { icon: Loader2, ink: "text-info", spin: true },
  planned: { icon: CircleDashed, ink: "text-info" },
  received: { icon: CircleDashed, ink: "text-info" },
};

/** A flow's page, on one of its tabs, read in the partition its counts are; narrowed as the tab's address allows. */
function flowHref(pipelineId: string, partition: string | null, tab: string, narrow: Record<string, string> = {}): string {
  const params = new URLSearchParams({ tab, ...narrow });
  if (partition !== null) {
    params.set("partition", partition);
  }

  return `/pipelines/${pipelineId}?${params.toString()}`;
}

function targetHref(target: VerdictTarget, pipelineId: string, partition: string | null): string {
  if ("submissionId" in target) {
    return `/delivery/submissions/${target.submissionId}`;
  }

  const narrow: Record<string, string> = {};
  if (target.status !== undefined) {
    narrow.status = target.status;
  }

  if (target.drifted === true) {
    narrow.drifted = "true";
  }

  return flowHref(pipelineId, partition, target.tab, narrow);
}

const RING = 48;
const STROKE = 4;
const RADIUS = (RING - STROKE) / 2;
const CIRCUMFERENCE = 2 * Math.PI * RADIUS;

/**
 * A flow's records by where they stand, as a ring: one arc per status holding records, in its tone, with the share
 * delivered in the middle. It replaces a bar across the card, which drew the eye along its whole width for the least
 * news (a flow all delivered); the ring keeps the shares to the card's corner, where the eye starts reading it. Its hover
 * lists every status with its count and share. A flow with no records, or none here, draws a dashed ring.
 */
function HealthRing({ shares, total, percent, testId }: { shares: StatusShare[]; total: number; percent: string | null; testId: string }) {
  const shown = shares.filter((share) => share.count > 0);
  const arcs = ringArcs(shares, CIRCUMFERENCE, 2.5, 3);
  const label = total === 0 ? "No records" : shown.map((share) => `${share.count.toLocaleString()} ${share.label}`).join(", ");
  const ring = (
    <div className="relative size-12 shrink-0" role="img" aria-label={label} data-testid={testId}>
      <svg viewBox={`0 0 ${RING} ${RING}`} className="size-12 -rotate-90" aria-hidden>
        <circle
          cx={RING / 2}
          cy={RING / 2}
          r={RADIUS}
          fill="none"
          strokeWidth={STROKE}
          className={arcs.length === 0 ? "stroke-muted-foreground/40" : "stroke-muted"}
          strokeDasharray={arcs.length === 0 ? "3 3" : undefined}
        />
        {arcs.map((arc) => (
          <circle
            key={arc.key}
            cx={RING / 2}
            cy={RING / 2}
            r={RADIUS}
            fill="none"
            strokeWidth={STROKE}
            className={STATUS_LOOK[arc.key].stroke}
            strokeDasharray={`${arc.length} ${CIRCUMFERENCE - arc.length}`}
            strokeDashoffset={-arc.start}
            data-testid={`${testId}-${arc.key}`}
          />
        ))}
      </svg>
      {percent !== null && (
        <span className="absolute inset-0 flex items-center justify-center font-mono text-[11px] font-semibold tabular-nums text-foreground">
          {percent}
        </span>
      )}
    </div>
  );

  if (shown.length === 0) {
    return ring;
  }

  return (
    <Tooltip delayDuration={250} disableHoverableContent>
      <TooltipTrigger asChild>{ring}</TooltipTrigger>
      <TooltipContent variant="panel" side="right" align="start" className="pointer-events-none">
        <span className="mb-1 block text-[11px] font-medium uppercase tracking-wider text-muted-foreground">
          {`${total.toLocaleString()} ${total === 1 ? "record" : "records"}`}
        </span>
        <span className="grid grid-cols-[auto_auto_1fr_auto] items-center gap-x-2 gap-y-0.5">
          {shown.map((share) => {
            const Icon = STATUS_LOOK[share.key].icon;
            return (
              <span key={share.key} className="contents">
                <Icon className={cn("size-3.5", STATUS_LOOK[share.key].ink)} aria-hidden />
                <span className="text-right font-mono tabular-nums">{share.count.toLocaleString()}</span>
                <span>{share.label}</span>
                <span className="text-right font-mono tabular-nums text-muted-foreground">{percentOf(share.count, total)}</span>
              </span>
            );
          })}
        </span>
      </TooltipContent>
    </Tooltip>
  );
}

/** The verdict under the flow's name: its glyph in its tone, its words, a link to where it is acted on, and why on hover. */
function VerdictLine({ verdict, href, testId }: { verdict: Verdict; href: string | null; testId: string }) {
  const Icon = VERDICT_GLYPH[verdict.kind];
  // What asks nothing of anyone reads quietly, so the cards that need someone are the ones that stand out.
  const calm = verdict.tone === "success" || verdict.tone === "muted";
  const words = (
    <span className={cn("min-w-0 text-[13px] leading-5", calm ? "text-muted-foreground" : "font-medium text-foreground")}>
      {verdict.headline}
    </span>
  );
  return (
    <div className="flex min-w-0 items-start gap-1.5" data-testid={testId} data-verdict={verdict.kind}>
      <Icon
        className={cn("mt-[3px] size-3.5 shrink-0", TONE_INK[verdict.tone], verdict.moving && verdict.kind === "delivering" && "animate-spin")}
        aria-hidden
      />
      <ExplainTip title={verdict.headline} text={verdict.explanation} testId={`${testId}-why`} side="bottom">
        {href === null ? (
          <span tabIndex={0} className="min-w-0 rounded-sm outline-none focus-visible:ring-2 focus-visible:ring-ring/60">{words}</span>
        ) : (
          <Link
            to={href}
            className="min-w-0 rounded-sm underline-offset-2 outline-none hover:underline focus-visible:ring-2 focus-visible:ring-ring/60"
            data-testid={`${testId}-act`}
          >
            {words}
          </Link>
        )}
      </ExplainTip>
    </div>
  );
}

/** One count in the card's row: a dot in the status's tone keys it to the ring, and a count of some opens those records. */
function Figure({ share, label, alarm, href, testId }: { share: StatusShare; label: string; alarm: boolean; href: string; testId: string }) {
  const some = share.count > 0;
  const look = STATUS_LOOK[share.key];
  const value = (
    <span
      className={cn(
        "block font-mono text-[15px] font-medium leading-6 tabular-nums",
        !some ? "text-muted-foreground/70" : alarm ? look.ink : "text-foreground",
      )}
      data-testid={`${testId}-value`}
    >
      {share.count.toLocaleString()}
    </span>
  );
  const caption = (
    <span className="flex items-center gap-1.5 text-[12px] leading-4 text-muted-foreground">
      <span className={cn("size-1.5 shrink-0 rounded-full", some ? look.fill : "bg-border")} aria-hidden />
      <span className="truncate">{label}</span>
    </span>
  );
  return (
    <RichTooltip title={label} body={some ? `${share.meaning} Click to list them.` : share.meaning} side="bottom">
      {some ? (
        <Link
          to={href}
          className="group min-w-0 rounded-md px-1.5 py-1 outline-none transition-colors hover:bg-accent/60 focus-visible:ring-2 focus-visible:ring-ring/60"
          data-testid={testId}
        >
          {caption}
          {value}
        </Link>
      ) : (
        <span className="min-w-0 px-1.5 py-1" data-testid={testId}>
          {caption}
          {value}
        </span>
      )}
    </RichTooltip>
  );
}

/** A note about some of a flow's records that its counts do not show: a neutral chip, its glyph in the tone. */
function Flag({ icon: Icon, ink, children, explanation, href, testId }: {
  icon: LucideIcon;
  ink: string;
  children: ReactNode;
  explanation: string;
  href: string | null;
  testId: string;
}) {
  const body = (
    <>
      <Icon className={ink} aria-hidden />
      {children}
    </>
  );
  return (
    <RichTooltip body={explanation} side="bottom">
      {href === null ? (
        <Badge variant="outline" className="rounded-md font-normal text-muted-foreground" data-testid={testId}>{body}</Badge>
      ) : (
        <Badge asChild variant="outline" className="rounded-md font-normal">
          <Link to={href} data-testid={testId}>{body}</Link>
        </Badge>
      )}
    </RichTooltip>
  );
}

/** One fact of the card's last line: a quiet label and its value. */
function Fact({ label, children, testId }: { label: string; children: ReactNode; testId: string }) {
  return (
    <span className="inline-flex items-center gap-1 whitespace-nowrap" data-testid={testId}>
      <span>{label}</span>
      <span className="text-foreground/85">{children}</span>
    </span>
  );
}

/**
 * One delivery flow on the Delivery page, read top to bottom as a reader asks of it: which flow, is it all right (the
 * ring and the verdict, the one thing that most needs someone, with where to act on it), how many records stand where
 * (four counts in the same place on every card, each opening its records), what else to know of some of them (drift,
 * restricted access, a held step), and when it last delivered, was verified and took a submission. Everything that
 * explains rather than counts is on hover. The card opens the flow's Delivery tab; its links open what they name.
 *
 * In the workbench's partition: a flow that delivers there is counted there; a flow that delivers only to other partitions
 * says where, and shows no counts, since none of its records are the workbench partition's.
 */
export function FlowCard({ pipeline, stats, active, elsewhere }: {
  pipeline: PipelineSummary;
  stats: DeliveryFlowStats | undefined;
  active: string | null;
  elsewhere: boolean;
}) {
  const navigate = useNavigate();
  const testId = `delivery-flow-${pipeline.id}`;
  const partitions = deliversTo(stats) ?? [];
  const counted = stats?.partition ?? null;
  const open = flowHref(pipeline.id, counted, "delivery");
  // The whole card opens the flow, as its name does; a click on one of its own links or controls does what that names.
  // A hover card's content is portalled out of the card, yet React still bubbles its clicks here: only a click on the card
  // itself counts.
  const onCard = (event: MouseEvent<HTMLDivElement>) => {
    const target = event.target;
    if (target instanceof Node && event.currentTarget.contains(target)
      && !(target instanceof Element && target.closest("a, button, [role='button']") !== null)) {
      void navigate(open);
    }
  };

  const batch = pipeline.batch !== null && pipeline.batch !== "default" ? pipeline.batch : null;
  const shares = stats === undefined ? [] : statusShares(stats);
  const verdict = stats === undefined || elsewhere ? null : flowVerdict(stats);

  return (
    <Card
      className={cn(
        "@container/flow cursor-pointer gap-3 rounded-lg p-4 shadow-none transition-colors hover:bg-accent/40",
        elsewhere && "opacity-70",
      )}
      onClick={onCard}
      data-testid={testId}
      data-elsewhere={elsewhere ? "true" : undefined}
      data-verdict={verdict?.kind}
    >
      <div className="flex items-center gap-3">
        {stats === undefined
          ? <Skeleton className="size-12 shrink-0 rounded-full" />
          : <HealthRing shares={elsewhere ? [] : shares} total={elsewhere ? 0 : stats.total} percent={elsewhere ? null : deliveredPercent(stats)} testId={`${testId}-ring`} />}
        <div className="flex min-w-0 flex-1 flex-col">
          {/* What the flow belongs to and how much it holds: quiet, above its name, in the corner the eye passes on the way in. */}
          <div className="flex min-h-4 min-w-0 items-center gap-2 text-[12px] leading-4 text-muted-foreground">
            {batch !== null && (
              <RichTooltip title="Batch" body={`${batch}: the source system this flow is grouped under, whose flows run together.`} side="top">
                <span className="min-w-0 truncate font-mono" data-testid={`${testId}-batch`}>{batch}</span>
              </RichTooltip>
            )}
            {stats !== undefined && !elsewhere && (
              <Link
                to={flowHref(pipeline.id, counted, "records")}
                className="ml-auto shrink-0 rounded-sm font-mono tabular-nums outline-none hover:text-foreground hover:underline focus-visible:ring-2 focus-visible:ring-ring/60"
                title="Every record the ledger holds for this flow"
                data-testid={`${testId}-total`}
              >
                {stats.total === 0 ? "no records" : `${stats.total.toLocaleString()} ${stats.total === 1 ? "record" : "records"}`}
              </Link>
            )}
          </div>
          <Link
            to={open}
            title={pipeline.name}
            className="mt-0.5 min-w-0 truncate rounded-sm font-mono text-[14px] font-semibold leading-5 text-foreground outline-none hover:underline focus-visible:ring-2 focus-visible:ring-ring/60"
            data-testid={`${testId}-open`}
          >
            {pipeline.name}
          </Link>
          {stats === undefined ? (
            <Skeleton className="mt-1 h-4 w-48" />
          ) : elsewhere ? (
            <ExplainTip
              title="Delivers elsewhere"
              text={`This flow delivers to ${partitions.join(", ")}, not ${active}, so none of its records are counted here. Pick one of those partitions in the title bar for its counts.`}
              testId={`${testId}-elsewhere-why`}
              side="bottom"
            >
              <span tabIndex={0} className="flex items-center gap-1.5 text-[13px] leading-5 text-muted-foreground outline-none" data-testid={`${testId}-elsewhere`}>
                <CircleDashed className="size-3.5 shrink-0" aria-hidden />
                {`Delivers to ${partitions.join(", ")}, not ${active}`}
              </span>
            </ExplainTip>
          ) : (
            verdict !== null && (
              <VerdictLine
                verdict={verdict}
                href={verdict.target === null ? null : targetHref(verdict.target, pipeline.id, counted)}
                testId={`${testId}-verdict`}
              />
            )
          )}
        </div>
      </div>

      {stats === undefined ? (
        <Skeleton className="h-11 w-full" />
      ) : !elsewhere && (
        <>
          <div className="-mx-1.5 grid grid-cols-4 gap-x-1" data-testid={`${testId}-figures`}>
            {FIGURES.map((figure) => {
              const share = shares.find((s) => s.key === figure.key)!;
              return (
                <Figure
                  key={figure.key}
                  share={share}
                  label={figure.label}
                  alarm={figure.alarm}
                  href={flowHref(pipeline.id, counted, "records", { status: figure.key })}
                  testId={`${testId}-${figure.key}`}
                />
              );
            })}
          </div>
          <Flags pipeline={pipeline} stats={stats} verdict={verdict} counted={counted} testId={testId} />
        </>
      )}

      {stats !== undefined && !elsewhere && (
        <div
          className="mt-auto flex flex-wrap items-center gap-x-4 gap-y-1 border-t pt-3 text-[12px] text-muted-foreground"
          data-testid={`${testId}-activity`}
        >
          <Fact label="Last delivery" testId={`${testId}-last-delivery`}>
            {stats.lastDeliveredUtc === null ? "never" : <RelativeTime value={stats.lastDeliveredUtc} absolute={false} />}
            {stats.deliveredLast24h > 0 && <span className="text-muted-foreground">{`, ${stats.deliveredLast24h.toLocaleString()} in 24h`}</span>}
          </Fact>
          <Fact label="Last verify" testId={`${testId}-last-verify`}>
            {stats.lastVerifiedUtc === null ? "never" : <RelativeTime value={stats.lastVerifiedUtc} absolute={false} />}
          </Fact>
          {stats.lastSubmission && <SubmissionFact submission={stats.lastSubmission} testId={`${testId}-submission`} />}
          <PartitionNote partitions={partitions} counted={counted} active={active} testId={`${testId}-partitions`} />
        </div>
      )}
    </Card>
  );
}

/** What some of a flow's records carry that its counts leave out; nothing at all when none do. */
function Flags({ pipeline, stats, verdict, counted, testId }: {
  pipeline: PipelineSummary;
  stats: DeliveryFlowStats;
  verdict: Verdict | null;
  counted: string | null;
  testId: string;
}) {
  const restricted = stats.restricted ?? 0;
  const stepsHeld = stats.stepsHeld ?? 0;
  const drifted = verdict?.kind === "drifted" ? 0 : stats.drifted;
  if (pipeline.active && restricted === 0 && stepsHeld === 0 && drifted === 0) {
    return null;
  }

  return (
    <div className="flex flex-wrap items-center gap-1.5" data-testid={`${testId}-flags`}>
      {!pipeline.active && (
        <Flag
          icon={PowerOff}
          ink="text-muted-foreground"
          explanation="The flow is inactive: no schedule or group run runs it until its document makes it active again."
          href={null}
          testId={`${testId}-inactive`}
        >
          inactive
        </Flag>
      )}
      {drifted > 0 && (
        <Flag
          icon={AlertTriangle}
          ink="text-warning"
          explanation="Records the last verify found OSDU holding another version, other content, or nothing, and removed records OSDU still holds. Click to list them."
          href={flowHref(pipeline.id, counted, "records", { drifted: "true" })}
          testId={`${testId}-drifted`}
        >
          {`${drifted.toLocaleString()} drifted`}
        </Flag>
      )}
      {restricted > 0 && (
        <Flag
          icon={Lock}
          ink="text-muted-foreground"
          explanation="Records whose access in OSDU is restricted under a registered access restriction: no run delivers them until the restriction is lifted. Click to list them."
          href={flowHref(pipeline.id, counted, "records", { restricted: "true" })}
          testId={`${testId}-restricted`}
        >
          {`${restricted.toLocaleString()} restricted`}
        </Flag>
      )}
      {stepsHeld > 0 && (
        <Flag
          icon={Puzzle}
          ink="text-warning"
          explanation="Records an enrichment held in and was set aside on: each went ahead with what the enrichment added to its last delivery. Click to list them."
          href={flowHref(pipeline.id, counted, "records", { stepHeld: "true" })}
          testId={`${testId}-steps-held`}
        >
          {`${stepsHeld.toLocaleString()} with a step held`}
        </Flag>
      )}
    </div>
  );
}

/** The flow's last submission: how it ended, as a glyph, and when it came in; its page on a click, its counts on hover. */
function SubmissionFact({ submission, testId }: { submission: NonNullable<DeliveryFlowStats["lastSubmission"]>; testId: string }) {
  const glyph = SUBMISSION_GLYPH[submission.status] ?? { icon: CircleDashed, ink: "text-muted-foreground" };
  const Icon = glyph.icon;
  const counts = [
    `${submission.recordCount.toLocaleString()} rows`,
    submission.delivered > 0 ? `${submission.delivered.toLocaleString()} delivered` : null,
    submission.skippedUnchanged > 0 ? `${submission.skippedUnchanged.toLocaleString()} unchanged` : null,
    submission.held > 0 ? `${submission.held.toLocaleString()} held` : null,
    submission.failed > 0 ? `${submission.failed.toLocaleString()} failed` : null,
  ].filter((part): part is string => part !== null).join(", ");
  const received = parseUtc(submission.receivedUtc);
  const when = `Received ${format(received, "MMM d, HH:mm:ss")}: ${counts}.`;
  return (
    <RichTooltip title={`Last submission: ${submission.status}`} body={submission.error ? `${when}\n${submission.error}` : when} side="bottom">
      <Link
        to={`/delivery/submissions/${submission.submissionId}`}
        className="inline-flex items-center gap-1 whitespace-nowrap rounded-sm outline-none hover:text-foreground focus-visible:ring-2 focus-visible:ring-ring/60"
        data-testid={testId}
        data-status={submission.status}
      >
        <span>Last submission</span>
        <Icon className={cn("size-3.5", glyph.ink, glyph.spin && "animate-spin")} aria-label={submission.status} />
        <span className="text-foreground/85">{formatDistanceToNow(received, { addSuffix: true })}</span>
      </Link>
    </RichTooltip>
  );
}

/**
 * Which partitions a flow's counts are of, where that is news: a flow that delivers to several (the counts are the title
 * bar's partition's), or any flow while no partition is picked. A flow that delivers to the picked partition alone says
 * nothing, since the page's heading already names it.
 */
function PartitionNote({ partitions, counted, active, testId }: { partitions: string[]; counted: string | null; active: string | null; testId: string }) {
  if (partitions.length === 0 || (partitions.length === 1 && partitions[0] === active)) {
    return null;
  }

  const body = counted !== null
    ? `This flow delivers to ${partitions.join(", ")}, each keeping a ledger of its own; the counts are ${counted}'s.`
    : `This flow delivers to ${partitions.join(", ")}; the counts add every partition's ledger up.`;
  return (
    <RichTooltip title="Partitions" body={body} side="bottom">
      <span className="inline-flex items-center gap-1 whitespace-nowrap" data-testid={testId}>
        <span>{counted !== null ? "In" : "Across"}</span>
        <span className="font-mono text-foreground/85">{counted ?? partitions.join(", ")}</span>
        {counted !== null && partitions.length > 1 && <span>{`of ${partitions.length} partitions`}</span>}
      </span>
    </RichTooltip>
  );
}
