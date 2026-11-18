import { useQuery } from "@tanstack/react-query";
import { Check, ChevronDown, Server } from "lucide-react";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { environmentApi } from "../../api/endpoints";
import { environmentMenu, type EnvironmentEntry } from "./environmentMenu";

const chip = "flex h-6 shrink-0 items-center gap-1.5 rounded-md bg-title-bar-foreground/10 px-2 text-title-bar-foreground/80";

/** An environment's name, what it is for and where it is, as the menu lists it. */
function EntryText({ entry }: { entry: EnvironmentEntry }) {
  return (
    <span className="flex min-w-0 flex-col gap-0.5">
      <span className="font-mono text-[13px] font-medium">{entry.name}</span>
      {entry.description !== null && (
        <span className="truncate text-[11px] text-foreground/80" title={entry.description}>{entry.description}</span>
      )}
      {entry.address !== null && (
        <span className="truncate text-[11px] text-muted-foreground" title={entry.url ?? undefined}>{entry.address}</span>
      )}
    </span>
  );
}

/**
 * The environment this deployment is, in the title bar (ControlPlane:Environments): an estate that runs dev, test and prod
 * as separate deployments lists them in each one's configuration, and the menu opens another one's GUI at its address,
 * where it signs a person in itself. With no other environment to open it only names this one, and with none configured
 * it shows nothing.
 */
export function EnvironmentSwitcher() {
  const { data } = useQuery({
    queryKey: ["environments"],
    queryFn: environmentApi.get,
    // The list changes only when the control plane starts with other settings.
    staleTime: Infinity,
    refetchOnWindowFocus: false,
  });
  const menu = environmentMenu(data);
  if (menu === null) {
    return null;
  }

  const label = (
    <>
      <Server className="size-3.5" />
      <span className="hidden text-[11px] text-title-bar-foreground/55 sm:inline">Environment</span>
      <span className="font-mono text-[12px] leading-none text-title-bar-foreground" data-testid="environment-current">
        {menu.current.name}
      </span>
    </>
  );

  if (!menu.canSwitch) {
    return (
      <span
        data-testid="environment-switcher"
        aria-label={`Environment: ${menu.current.name}`}
        title={menu.current.description ?? "The environment this deployment is"}
        className={chip}
      >
        {label}
      </span>
    );
  }

  return (
    <DropdownMenu>
      <DropdownMenuTrigger
        data-testid="environment-switcher"
        aria-label={`Environment: ${menu.current.name}`}
        title="The environment this deployment is, and the others to open"
        className={`${chip} transition-colors hover:bg-title-bar-foreground/15 hover:text-title-bar-foreground`}
      >
        {label}
        <ChevronDown className="size-3.5 text-title-bar-foreground/55" />
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end" className="w-80">
        <DropdownMenuLabel className="text-[12px] font-normal text-muted-foreground">
          Open another environment
        </DropdownMenuLabel>
        <DropdownMenuSeparator />
        {menu.entries.map((entry) => entry.current || entry.url === null
          ? (
            <DropdownMenuItem key={entry.name} inset className="items-start py-1.5" data-testid={`environment-option-${entry.name}`}>
              <Check className="absolute left-2 top-2 size-3.5 text-foreground" aria-label="This environment" />
              <EntryText entry={entry} />
            </DropdownMenuItem>
          )
          : (
            <DropdownMenuItem key={entry.name} inset asChild className="items-start py-1.5">
              <a href={entry.url} data-testid={`environment-option-${entry.name}`}>
                <EntryText entry={entry} />
              </a>
            </DropdownMenuItem>
          ))}
      </DropdownMenuContent>
    </DropdownMenu>
  );
}
