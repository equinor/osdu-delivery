import { useState } from "react";
import { Database, FileCode, FileText, Network, Search, SlidersHorizontal, Table2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import {
  DropdownMenu, DropdownMenuContent, DropdownMenuLabel, DropdownMenuSeparator, DropdownMenuSub, DropdownMenuSubContent, DropdownMenuSubTrigger, DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { LineageJumpButton, LineageJumpSubmenu } from "@/components/LineageJumpButton";
import type { PartMark, PartTarget } from "./partTargets";

/** The mark of a source (a table, the flow's parameters, the cache, a search, another record), the mapping, or its record. */
export function SourceGlyph({ kind }: { kind: PartMark | undefined }) {
  switch (kind) {
    case "mapping":
      return <FileCode className="size-3.5" aria-hidden />;
    case "parameters":
      return <SlidersHorizontal className="size-3.5" aria-hidden />;
    case "cache":
      return <Database className="size-3.5" aria-hidden />;
    case "search":
      return <Search className="size-3.5" aria-hidden />;
    case "type":
    case "parent":
    case "childRecord":
    case "base":
    case "link":
      return <FileText className="size-3.5" aria-hidden />;
    default:
      return <Table2 className="size-3.5" aria-hidden />;
  }
}

/**
 * A part's lineage on its panel's header: the jump to the one node it is or comes from, or, for a property whose value comes
 * from several, a menu of each, every one opening its own picker. Nothing for a part lineage holds nothing of.
 */
export function PartLineageButton({ targets, named }: { targets: readonly PartTarget[]; named: string }) {
  const [open, setOpen] = useState(false);
  if (targets.length === 0) {
    return null;
  }

  if (targets.length === 1) {
    return <LineageJumpButton target={targets[0].target} iconOnly />;
  }

  return (
    <DropdownMenu open={open} onOpenChange={setOpen}>
      <Tooltip>
        <TooltipTrigger asChild>
          <DropdownMenuTrigger asChild>
            <Button variant="ghost" size="icon-xs" aria-label="View in lineage graph" data-testid="mapping-overview-lineage-sources" className="shrink-0">
              <Network />
            </Button>
          </DropdownMenuTrigger>
        </TooltipTrigger>
        <TooltipContent>View in lineage graph</TooltipContent>
      </Tooltip>
      <DropdownMenuContent align="end" className="w-64">
        <DropdownMenuLabel className="truncate text-[12px] font-normal text-muted-foreground" title={named}>{`What ${named} comes from`}</DropdownMenuLabel>
        <DropdownMenuSeparator />
        {targets.map((source) => (
          <LineageJumpSubmenu
            key={source.id}
            target={source.target}
            label={source.title}
            glyph={<SourceGlyph kind={source.mark} />}
            onClose={() => setOpen(false)}
            testId={`mapping-overview-lineage-source-${source.id}`}
          />
        ))}
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

/**
 * A part's lineage in its right-click menu: **Open in lineage**, holding the picker for the one node it is or comes from, or,
 * for a property whose value comes from several, each of them, every one opening its own picker.
 */
export function PartLineageEntry({ targets, onClose }: { targets: readonly PartTarget[]; onClose: () => void }) {
  if (targets.length === 0) {
    return null;
  }

  if (targets.length === 1) {
    return <LineageJumpSubmenu target={targets[0].target} onClose={onClose} testId="mapping-overview-menu-lineage" />;
  }

  return (
    <DropdownMenuSub>
      <DropdownMenuSubTrigger data-testid="mapping-overview-menu-lineage">
        <Network />
        Open in lineage
      </DropdownMenuSubTrigger>
      <DropdownMenuSubContent className="w-64">
        <DropdownMenuLabel className="text-[12px] font-normal text-muted-foreground">What it comes from</DropdownMenuLabel>
        <DropdownMenuSeparator />
        {targets.map((source) => (
          <LineageJumpSubmenu
            key={source.id}
            target={source.target}
            label={source.title}
            glyph={<SourceGlyph kind={source.mark} />}
            onClose={onClose}
            testId={`mapping-overview-menu-lineage-${source.id}`}
          />
        ))}
      </DropdownMenuSubContent>
    </DropdownMenuSub>
  );
}
