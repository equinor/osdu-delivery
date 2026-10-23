import { useState, type FormEvent } from "react";
import { useNavigate } from "react-router-dom";
import { PackageSearch } from "lucide-react";
import { Button } from "@/components/ui/button";
import { SearchInput } from "@/components/SearchInput";
import { cn } from "@/lib/utils";

/**
 * The way from the estate's front door to one record: a term typed here opens the Records page looked up for it. Ids and
 * test ids are `<testId>`, `<testId>-term` and `<testId>-go`.
 */
export function FindRecordForm({ testId = "delivery-find-record", className }: { testId?: string; className?: string }) {
  const navigate = useNavigate();
  const [term, setTerm] = useState("");
  const submit = (event: FormEvent) => {
    event.preventDefault();
    const trimmed = term.trim();
    void navigate(trimmed === "" ? "/delivery/records" : `/delivery/records?q=${encodeURIComponent(trimmed)}`);
  };

  return (
    <form className={cn("flex flex-wrap items-center gap-2", className)} onSubmit={submit} data-testid={testId}>
      <SearchInput
        value={term}
        onChange={setTerm}
        placeholder="Find a record: source key, OSDU id, key or file"
        label="Find a record"
        testId={`${testId}-term`}
        className="sm:w-80"
      />
      <Button type="submit" variant="outline" size="sm" data-testid={`${testId}-go`}>
        <PackageSearch />
        Look up
      </Button>
    </form>
  );
}
