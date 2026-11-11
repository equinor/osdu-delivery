import { RichTooltip } from "@/components/RichTooltip";
import { useClipped } from "@/components/useClipped";
import { cn } from "@/lib/utils";

interface HeadClippedTextProps {
  /** What leads to the part that matters (osdu:wks:master-data--, osdu.data.Curves[].); gives way first. */
  head: string;
  /** The part that tells values apart (Wellbore, CurveUnit); always in view. */
  body: string;
  /** What follows it and stays in view too (:1.3.0); optional. */
  tail?: string;
  /** Caption for the hover panel naming what the value is. */
  title: string;
  /** Classes for the head, which reads muted by default. */
  headClassName?: string;
  /** Classes for the line (font, size, color). */
  className?: string;
}

/**
 * A code-like value on one line in whatever width it is given, where the end tells values apart and the start repeats
 * down a column: an OSDU kind, a variable path. The head clips from the left first; only when the body and tail alone
 * are wider than the room does the body end in an ellipsis, so it never stops mid-letter at the edge. The tail stays in
 * view. The text is the value verbatim, so it selects and copies whole, and the whole value is on hover whenever any of
 * it is hidden.
 */
export function HeadClippedText({ head, body, tail = "", title, headClassName = "text-muted-foreground", className }: HeadClippedTextProps) {
  const full = `${head}${body}${tail}`;
  const [lineRef, lineClipped] = useClipped(full);
  const [headRef, headClipped] = useClipped(full);
  const [bodyRef, bodyClipped] = useClipped(full);

  const line = (
    <span ref={lineRef} className={cn("flex min-w-0 max-w-full items-baseline overflow-hidden", className)}>
      {/* Right-to-left direction puts the ellipsis at the start; the bdi keeps the head itself reading left to right. */}
      <span ref={headRef} className={cn("min-w-0 overflow-hidden text-ellipsis whitespace-nowrap [direction:rtl]", headClassName)}>
        <bdi>{head}</bdi>
      </span>
      {/* The body and tail do not shrink, so the head takes the whole squeeze until it is gone; they are never wider than
          the line, so only then does the body end in an ellipsis, the tail still in view after it. */}
      <span className="flex max-w-full shrink-0 items-baseline overflow-hidden">
        <span ref={bodyRef} className="min-w-0 overflow-hidden text-ellipsis whitespace-nowrap font-medium">{body}</span>
        {tail !== "" && <span className="shrink-0 text-muted-foreground">{tail}</span>}
      </span>
    </span>
  );

  return lineClipped || headClipped || bodyClipped ? <RichTooltip body={full} title={title} mono>{line}</RichTooltip> : line;
}
