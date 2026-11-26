// The find all of the entry editor's form: the field every matching row of a cached type is found by, what it compares the
// field with (a dataset column, a fixed text or a field of a lookup's record), the fields that narrow the rows, and the find
// all Save writes from them.

import { useState, type Dispatch, type SetStateAction } from "react";
import type { MappingDraftFindAll, MappingDraftFindAllContains, MappingDraftLookup } from "../../../api/delivery";
import { bareColumn } from "../mappingDraft";
import type { FindAllMode } from "./entryFormModel";

export interface FindAllState {
  /** The field of the cached rows compared. */
  field: string;
  setField: (field: string) => void;
  mode: FindAllMode;
  setMode: (mode: FindAllMode) => void;
  /** The dataset column or the fixed text the field is compared with. */
  value: string;
  setValue: (value: string) => void;
  /** The lookup whose record's field the field is compared with. */
  lookup: string;
  setLookup: (lookup: string) => void;
  /** That field's path in the lookup's record. */
  path: string;
  setPath: (path: string) => void;
  /** The fields a row read holds nothing under. */
  empty: string[];
  setEmpty: Dispatch<SetStateAction<string[]>>;
  /** The fields whose text holds the text of another of the same row. */
  contains: MappingDraftFindAllContains[];
  setContains: Dispatch<SetStateAction<MappingDraftFindAllContains[]>>;
  /** The find all as Save writes it. */
  build: () => MappingDraftFindAll;
}

/** The find all an entry holds (`initialAll`, null for none), with a new one comparing a lookup's field when the mapping declares a lookup. */
export function useFindAll(initialAll: MappingDraftFindAll | null, lookups: readonly MappingDraftLookup[]): FindAllState {
  const initialAllLookup = (initialAll?.lookup ?? "").trim();
  const [allField, setAllField] = useState(initialAll?.field ?? "");
  const [allMode, setAllMode] = useState<FindAllMode>(initialAll === null
    ? (lookups.length > 0 ? "lookup" : "column")
    : initialAllLookup !== "" ? "lookup" : (initialAll.literal ?? "") !== "" ? "text" : "column");
  const [allValue, setAllValue] = useState(initialAll === null ? "" : (initialAll.literal ?? "") !== "" ? initialAll.literal ?? "" : initialAll.column ?? "");
  const [allLookup, setAllLookup] = useState(initialAllLookup === "" ? lookups[0]?.name ?? "" : initialAllLookup.split(".")[0]);
  const [allPath, setAllPath] = useState(initialAllLookup.includes(".") ? initialAllLookup.slice(initialAllLookup.indexOf(".") + 1) : "");
  const [allEmpty, setAllEmpty] = useState<string[]>(initialAll?.empty ?? []);
  const [allContains, setAllContains] = useState<MappingDraftFindAllContains[]>(initialAll?.contains ?? []);

  const findAllOf = (): MappingDraftFindAll => ({
    field: allField.trim(),
    column: allMode === "column" ? bareColumn(allValue) : null,
    literal: allMode === "text" ? allValue : null,
    lookup: allMode === "lookup" ? `${allLookup.trim()}.${allPath.trim()}` : null,
    empty: allEmpty.map((field) => field.trim()).filter((field) => field !== ""),
    contains: allContains
      .map((line) => ({ field: line.field.trim(), part: line.part.trim() }))
      .filter((line) => line.field !== "" || line.part !== ""),
  });

  return {
    field: allField,
    setField: setAllField,
    mode: allMode,
    setMode: setAllMode,
    value: allValue,
    setValue: setAllValue,
    lookup: allLookup,
    setLookup: setAllLookup,
    path: allPath,
    setPath: setAllPath,
    empty: allEmpty,
    setEmpty: setAllEmpty,
    contains: allContains,
    setContains: setAllContains,
    build: findAllOf,
  };
}
