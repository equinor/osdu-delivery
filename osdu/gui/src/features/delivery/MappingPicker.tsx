import { useState } from "react";
import { useNavigate } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { Check, ChevronsUpDown } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Command, CommandEmpty, CommandInput, CommandItem, CommandList } from "@/components/ui/command";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { deliveryApi } from "../../api/delivery";
import { KindText } from "./KindText";
import { mappingRoute, mappingSearchText } from "./mappingSearch";

/**
 * The mapping's name as the way to another: a click opens a searchable list of every mapping the repositories hold, read when
 * it is opened, and picking one goes to its page. Nobody has to go back to the list to compare two mappings.
 */
export function MappingPicker({ current, name }: { current: string; name: string }) {
  const navigate = useNavigate();
  const [open, setOpen] = useState(false);
  const mappings = useQuery({
    queryKey: ["delivery", "mappings", undefined],
    queryFn: () => deliveryApi.mappings(undefined),
    enabled: open,
  });

  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <Button
          variant="ghost"
          size="sm"
          role="combobox"
          aria-expanded={open}
          aria-label="Open another mapping"
          className="h-8 min-w-0 shrink gap-1.5 px-1.5 font-mono text-[15px] font-semibold"
          data-testid="delivery-mapping-picker"
        >
          <span className="min-w-0 truncate" data-testid="delivery-mapping-name">{name}</span>
          <ChevronsUpDown className="text-muted-foreground" />
        </Button>
      </PopoverTrigger>
      <PopoverContent align="start" className="w-[34rem] max-w-[calc(100vw-2rem)] p-0" data-testid="delivery-mapping-picker-list">
        <Command>
          <CommandInput placeholder="Search by name, kind, source or path" />
          <CommandList>
            <CommandEmpty>{mappings.isPending ? "Reading the mappings." : mappings.isError ? "The mappings could not be read." : "No mapping matches."}</CommandEmpty>
            {(mappings.data ?? []).map((mapping) => (
              <CommandItem
                key={mapping.id}
                value={`${mappingSearchText(mapping)} ${mapping.id}`}
                onSelect={() => {
                  setOpen(false);
                  navigate(mappingRoute(mapping.id));
                }}
                data-testid="delivery-mapping-picker-option"
              >
                <div className="flex min-w-0 flex-1 items-baseline gap-3">
                  <span className="shrink-0 font-mono text-[12px]">{mapping.reference}</span>
                  <span className="min-w-0 flex-1 text-muted-foreground"><KindText kind={mapping.kind} /></span>
                </div>
                {mapping.id === current && <Check className="shrink-0" />}
              </CommandItem>
            ))}
          </CommandList>
        </Command>
      </PopoverContent>
    </Popover>
  );
}
