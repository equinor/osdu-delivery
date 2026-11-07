import { CheckCircle2, CircleDashed, Hourglass, PauseCircle, Trash2, Undo2, XCircle, type LucideIcon } from "lucide-react";
import type { HealthTone, StatusKey } from "./flowHealth";

/**
 * How each status a record can be in is drawn wherever a flow's records are shown by status (a flow's status bar, the
 * Delivery page's ring and counts): its glyph, its fill and its ink, the tones the record badges give it (DESIGN.md 3.2).
 * `stroke` is the fill for a line drawn in SVG.
 */
export const STATUS_LOOK: Record<StatusKey, { icon: LucideIcon; fill: string; ink: string; stroke: string }> = {
  delivered: { icon: CheckCircle2, fill: "bg-success", ink: "text-success", stroke: "stroke-success" },
  pending: { icon: CircleDashed, fill: "bg-info", ink: "text-info", stroke: "stroke-info" },
  waiting: { icon: Hourglass, fill: "bg-info/45", ink: "text-info", stroke: "stroke-info/45" },
  held: { icon: PauseCircle, fill: "bg-warning", ink: "text-warning", stroke: "stroke-warning" },
  failed: { icon: XCircle, fill: "bg-destructive", ink: "text-destructive", stroke: "stroke-destructive" },
  deleted: { icon: Trash2, fill: "bg-muted-foreground/50", ink: "text-muted-foreground", stroke: "stroke-muted-foreground/50" },
  reverted: { icon: Undo2, fill: "bg-muted-foreground/30", ink: "text-muted-foreground", stroke: "stroke-muted-foreground/30" },
};

/** The ink of each tone a verdict takes. */
export const TONE_INK: Record<HealthTone, string> = {
  success: "text-success",
  warning: "text-warning",
  destructive: "text-destructive",
  info: "text-info",
  muted: "text-muted-foreground",
};
