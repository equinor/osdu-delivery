import { useLayoutEffect, useRef, useState, type ReactNode } from "react";
import { FileJson, FolderGit2, Tag, type LucideIcon } from "lucide-react";
import { Button } from "@/components/ui/button";
import { SheetDescription, SheetHeader, SheetTitle } from "@/components/ui/sheet";
import { cn } from "@/lib/utils";
import type { DeliveryTemplateDetail } from "../../api/delivery";
import { CopyButton } from "@/components/CopyButton";
import { entityName } from "./templateFormat";

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
  /** Where a schema of the OSDU data definitions is read from; a repository's template names its own file. */
  source: ReactNode | null;
  /** Download or compare. */
  actions?: ReactNode;
}

/**
 * A template's header: its name and the actions on it beside that, the kind it is, OSDU's description, and one row of facts
 * (the version, and where it is read from: the repository and the file of its template folder, or the release of the OSDU
 * data definitions).
 */
export function TemplateHeader({ kind, detail, source, actions }: TemplateHeaderProps) {
  const file = detail?.file ?? null;
  return (
    <SheetHeader className="gap-3 border-b" data-testid="templates-view-header">
      <div className="flex min-w-0 flex-col gap-1 pr-8">
        <div className="flex min-w-0 flex-wrap items-center gap-2">
          <SheetTitle className="text-lg leading-7">{detail?.title ?? entityName(kind)}</SheetTitle>
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
          {file !== null && (
            <>
              <Fact icon={FolderGit2} label="Repository" testId="templates-view-repository">
                <span className="min-w-0 break-all">{file.repository}</span>
              </Fact>
              <Fact icon={FileJson} label="File" testId="templates-view-file">
                <span className="min-w-0 break-all font-mono">{file.path}</span>
                <CopyButton iconOnly label="Copy the file's path" text={file.path} testId="templates-view-file-copy" />
              </Fact>
            </>
          )}
          {file === null && source !== null && <Fact icon={FileJson} label="Source">{source}</Fact>}
        </dl>
      )}
    </SheetHeader>
  );
}
