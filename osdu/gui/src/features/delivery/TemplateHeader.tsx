import { useLayoutEffect, useRef, useState, type ReactNode } from "react";
import { Clock3, FileJson, Save, Tag, TriangleAlert, type LucideIcon } from "lucide-react";
import { Button } from "@/components/ui/button";
import { SheetDescription, SheetHeader, SheetTitle } from "@/components/ui/sheet";
import { cn } from "@/lib/utils";
import type { DeliveryTemplateDetail } from "../../api/delivery";
import { CopyButton } from "@/components/CopyButton";
import { RelativeTime } from "@/components/RelativeTime";
import { StatePill } from "@/components/StatusBadge";
import { entityName, originText } from "./templateFormat";

/** One fact about the template: a glyph, the value, and its name for assistive tech. */
function Fact({ icon: Icon, label, children, testId }: { icon: LucideIcon; label: string; children: ReactNode; testId?: string }) {
  return (
    <div className="flex min-w-0 items-center gap-1.5" data-testid={testId}>
      <dt className="sr-only">{label}</dt>
      <Icon className="size-3.5 shrink-0 text-muted-foreground" aria-hidden />
      <dd className="flex min-w-0 items-center gap-1">{children}</dd>
    </div>
  );
}

/** OSDU's description of the kind, held to two lines until the reader asks for the rest. */
function Description({ text }: { text: string }) {
  const ref = useRef<HTMLParagraphElement>(null);
  const [whole, setWhole] = useState(false);
  const [clipped, setClipped] = useState(false);

  // Whether two lines hold the text depends on the width the sheet gives it, so it is measured rather than guessed.
  useLayoutEffect(() => {
    const element = ref.current;
    if (element === null || whole) {
      return;
    }

    const measure = () => setClipped(element.scrollHeight > element.clientHeight + 1);
    measure();
    const observer = new ResizeObserver(measure);
    observer.observe(element);
    return () => observer.disconnect();
  }, [text, whole]);

  return (
    <div className="flex max-w-[90ch] flex-col items-start gap-0.5">
      <p ref={ref} className={cn("text-[13px] leading-5 text-muted-foreground", !whole && "line-clamp-2")} data-testid="templates-view-description">
        {text}
      </p>
      {(clipped || whole) && (
        <Button variant="link" size="xs" className="h-auto px-0 text-xs" onClick={() => setWhole(!whole)} data-testid="templates-view-description-toggle">
          {whole ? "Show less" : "Show more"}
        </Button>
      )}
    </div>
  );
}

interface TemplateHeaderProps {
  /** The kind the sheet is about, known before the template is laid out. */
  kind: string;
  /** The template once it is laid out. */
  detail: DeliveryTemplateDetail | undefined;
  /** Where a template not yet saved is read from; a saved version names its own origin. */
  source: ReactNode | null;
  /** Save, compare or delete. */
  actions?: ReactNode;
}

/**
 * A template's header: its name, whether it is saved and the actions on it beside that, the kind it is, OSDU's description, and one
 * row of facts (the version, where it comes from, when and by whom it was saved, which repositories' template folders hold
 * it, and how many synced mappings fill it).
 */
export function TemplateHeader({ kind, detail, source, actions }: TemplateHeaderProps) {
  const saved = detail?.saved ?? null;
  const origin = source ?? (saved === null ? null : <span className="min-w-0 break-all">{originText(saved.origin)}</span>);
  return (
    <SheetHeader className="gap-3 border-b" data-testid="templates-view-header">
      <div className="flex min-w-0 flex-col gap-1 pr-8">
        <div className="flex min-w-0 flex-wrap items-center gap-2">
          <SheetTitle className="text-lg leading-7">{detail?.title ?? entityName(kind)}</SheetTitle>
          {detail !== undefined && (saved !== null
            ? <StatePill tone="success" label="saved" icon={Save} testId="templates-view-state" />
            : <StatePill tone="warning" label="not saved" icon={TriangleAlert} testId="templates-view-state" />)}
          {actions !== undefined && <div className="flex flex-wrap items-center gap-2" data-testid="templates-view-actions">{actions}</div>}
        </div>
        <SheetDescription asChild>
          <div className="flex min-w-0 items-center gap-1 font-mono text-[12px]">
            <span className="min-w-0 break-all" data-testid="templates-view-kind">{kind}</span>
            <CopyButton iconOnly label="Copy the kind" text={kind} testId="templates-view-kind-copy" />
          </div>
        </SheetDescription>
      </div>

      {detail !== undefined && detail.description !== null && <Description text={detail.description} />}

      {detail !== undefined && (
        <dl className="flex min-w-0 flex-wrap items-center gap-x-5 gap-y-1.5 text-xs" data-testid="templates-view-saved">
          <Fact icon={Tag} label="Version">
            <span className="font-mono" data-testid="templates-view-version">{detail.version}</span>
            <CopyButton iconOnly label="Copy the version" text={detail.version} testId="templates-view-version-copy" />
          </Fact>
          {origin !== null && <Fact icon={FileJson} label="Source">{origin}</Fact>}
          {saved !== null && (
            <Fact icon={Clock3} label="Saved">
              <span>Saved <RelativeTime value={saved.capturedUtc} /> by {saved.capturedBy}</span>
            </Fact>
          )}
        </dl>
      )}
    </SheetHeader>
  );
}
