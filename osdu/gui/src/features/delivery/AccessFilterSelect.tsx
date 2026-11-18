import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { ACCESS_FILTERS, accessChoiceLabel, type AccessFilter, type RestrictedFigure } from "./recordAccess";

/**
 * The Access filter of the Records tab and the Records page, beside the status filter as the Access column stands beside
 * the Status column: any access, restricted either way, restricted until complete, or restricted by an operator. It narrows
 * whatever the status, so the two combine. With the flow's counts, each choice says how many records it lists.
 */
export function AccessFilterSelect({ value, onChange, figure, testId }: {
  value: AccessFilter;
  onChange: (next: AccessFilter) => void;
  figure: RestrictedFigure | null;
  testId: string;
}) {
  const chosen = ACCESS_FILTERS.find((choice) => choice.value === value) ?? ACCESS_FILTERS[0];
  return (
    <Select value={value} onValueChange={(next) => onChange(next as AccessFilter)}>
      <SelectTrigger size="sm" className="h-8 w-60" title={chosen.meaning} data-testid={testId}>
        <SelectValue />
      </SelectTrigger>
      <SelectContent>
        {ACCESS_FILTERS.map((choice) => (
          <SelectItem key={choice.value} value={choice.value} title={choice.meaning} data-testid={`${testId}-${choice.value}`}>
            {accessChoiceLabel(choice, figure)}
          </SelectItem>
        ))}
      </SelectContent>
    </Select>
  );
}
