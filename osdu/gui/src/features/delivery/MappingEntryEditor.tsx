import { useState } from "react";
import { Sheet, SheetContent } from "@/components/ui/sheet";
import type { DeliveryCachedType, DeliveryTemplateVariable, MappingDraft, MappingDraftEntry, MappingDraftIssue } from "../../api/delivery";
import { EntryForm } from "./entry/EntryForm";

/** What the entry editor edits: the variable, its entry, and the holder when the target is a new key of an object with free keys. */
export interface EntryEditorTarget {
  variable: DeliveryTemplateVariable;
  entry: MappingDraftEntry | null;
  /** Set for a new key under an object with free keys (tags): the editor asks for the key's name. */
  keyHolder: DeliveryTemplateVariable | null;
  /** Why no mapping may fill the target, when the template does not let one. */
  outside: string | null;
  /** A number the page gives each opening, so every opening starts from the draft as it is. */
  session: number;
}

interface MappingEntryEditorProps {
  /** What to edit; null closes the editor. */
  target: EntryEditorTarget | null;
  draft: MappingDraft;
  /** The template's variables, of which an item of a list of objects fills those inside the list's items. */
  variables: DeliveryTemplateVariable[];
  /** The types of the cache the mapping reads, offered to a cache entry; empty when no cache is picked. */
  cacheTypes: DeliveryCachedType[];
  /** The kind the template is linked to as its parent, whose sections a parent input takes; null for a template linked to none. */
  parent: string | null;
  /** The kinds of the templates linked to this one, whose records a child input links; empty when none is. */
  childKinds: string[];
  /** Every issue of the last check; the editor shows the ones about its target. */
  issues: MappingDraftIssue[];
  /** Puts the entry for a target into the draft, or removes the target's entry when the entry is null. */
  onSave: (target: string, entry: MappingDraftEntry | null) => void;
  onClose: () => void;
}

/**
 * The entry editor: where one variable's value comes from (a dataset column, a child dataset's rows, a cached record, a
 * lookup's record, a search, a section of the record's parent, the link to one of its children, a static value, the first of
 * several alternatives, a list of values or a list of objects), with its modifiers,
 * condition, requiredness, assertions and description. It edits a copy, and Save puts the entry into the draft, so Cancel
 * leaves the draft as it was.
 */
export function MappingEntryEditor({ target, draft, variables, cacheTypes, parent, childKinds, issues, onSave, onClose }: MappingEntryEditorProps) {
  // The sheet slides out showing what it showed, so the last target stays rendered while it closes.
  const [shown, setShown] = useState<EntryEditorTarget | null>(target);
  if (target !== null && target !== shown) {
    setShown(target);
  }

  return (
    <Sheet open={target !== null} onOpenChange={(open) => { if (!open) { onClose(); } }}>
      <SheetContent className="w-full gap-0 sm:max-w-2xl" data-testid="mapping-builder-entry-editor">
        {shown !== null && (
          <EntryForm
            key={shown.session}
            target={shown}
            draft={draft}
            variables={variables}
            cacheTypes={cacheTypes}
            parent={parent}
            childKinds={childKinds}
            issues={shown.keyHolder === null ? issues.filter((issue) => issue.target === shown.variable.path) : []}
            onSave={onSave}
            onClose={onClose}
          />
        )}
      </SheetContent>
    </Sheet>
  );
}
