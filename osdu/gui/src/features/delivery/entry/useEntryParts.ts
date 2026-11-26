// The parts an entry of the entry editor's form holds (the alternatives of a coalesce, the items of a list, the properties of
// an item of a list of objects) and the part a nested form over the entry's own edits, with what its Save puts back.

import { useMemo, useState, type Dispatch, type SetStateAction } from "react";
import type { DeliveryTemplateVariable, MappingDraftEntry } from "../../../api/delivery";
import { putEntry } from "../mappingDraft";
import type { EditedPart } from "./entryFormModel";

export interface EntryParts {
  alternatives: MappingDraftEntry[];
  setAlternatives: Dispatch<SetStateAction<MappingDraftEntry[]>>;
  items: MappingDraftEntry[];
  setItems: Dispatch<SetStateAction<MappingDraftEntry[]>>;
  properties: MappingDraftEntry[];
  setProperties: Dispatch<SetStateAction<MappingDraftEntry[]>>;
  /** The part the nested form edits; null while none is open. */
  editing: EditedPart | null;
  /** Opens an alternative or an item at its place in its list, the end for a new one. */
  openPart: (list: "alternatives" | "items", index: number, part: MappingDraftEntry | null) => void;
  /** Opens a property of the item, filled or not. */
  openProperty: (property: DeliveryTemplateVariable, part: MappingDraftEntry | null) => void;
  /** Puts what the nested form saved for the part it edits into the part's list, and closes it. */
  savePart: (edited: EditedPart, path: string, value: MappingDraftEntry | null) => void;
  closePart: () => void;
}

/** The parts `entry` holds, a property kept in the order of the template's `variables`. */
export function useEntryParts(entry: MappingDraftEntry | null, variables: DeliveryTemplateVariable[]): EntryParts {
  const variableOrder = useMemo(() => new Map(variables.map((candidate, index) => [candidate.path, index])), [variables]);
  const [alternatives, setAlternatives] = useState<MappingDraftEntry[]>(entry?.alternatives ?? []);
  const [items, setItems] = useState<MappingDraftEntry[]>(entry?.items ?? []);
  const [properties, setProperties] = useState<MappingDraftEntry[]>(entry?.properties ?? []);
  const [editing, setEditing] = useState<EditedPart | null>(null);
  const [openings, setOpenings] = useState(0);

  const openPart = (list: "alternatives" | "items", index: number, part: MappingDraftEntry | null) => {
    const session = openings + 1;
    setOpenings(session);
    setEditing({ list, index, entry: part, variable: null, session });
  };

  // A property of the item is edited as an entry of its own for the variable inside the list's items it fills.
  const openProperty = (property: DeliveryTemplateVariable, part: MappingDraftEntry | null) => {
    const session = openings + 1;
    setOpenings(session);
    setEditing({ list: "properties", index: -1, entry: part, variable: property, session });
  };

  const savePart = (edited: EditedPart, path: string, value: MappingDraftEntry | null) => {
    if (edited.list === "properties") {
      // A property is kept in template order, and one left unfilled is taken out of the item.
      setProperties((current) => (value === null
        ? current.filter((candidate) => candidate.target !== path)
        : putEntry(current, value, variableOrder)));
    } else if (value !== null) {
      const at = edited.index;
      const put = (current: MappingDraftEntry[]) => (at < current.length ? current.map((old, i) => (i === at ? value : old)) : [...current, value]);
      if (edited.list === "items") {
        setItems(put);
      } else {
        setAlternatives(put);
      }
    }

    setEditing(null);
  };

  const closePart = () => setEditing(null);

  return { alternatives, setAlternatives, items, setItems, properties, setProperties, editing, openPart, openProperty, savePart, closePart };
}
