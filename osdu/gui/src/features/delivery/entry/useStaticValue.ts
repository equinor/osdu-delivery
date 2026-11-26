// The static value of the entry editor's form: what each of its editors holds, the editor in use, the JSON it gives or why it
// gives none, and switching between the typed editor and writing the value as JSON.

import { useState, type Dispatch, type SetStateAction } from "react";
import type { DeliveryTemplateVariable } from "../../../api/delivery";
import { staticModeFor, type StaticMode } from "../mappingDraft";
import { seedStatic, staticJsonOf, type Choice, type StaticState } from "./entryFormModel";

export interface StaticValue {
  /** The editor the variable's type takes, json when none of the typed editors holds it. */
  typedMode: StaticMode;
  /** Whether the value is written as JSON. */
  jsonMode: boolean;
  /** The editor in use. */
  mode: StaticMode;
  state: StaticState;
  setState: Dispatch<SetStateAction<StaticState>>;
  /** The value as JSON from the editor in use, or why it cannot be; nothing while the input is not a static value. */
  result: { json: string | null; error: string | null };
  /** Writes the value as JSON, or goes back to the typed editor when it can show what the JSON holds. */
  toggleJson: (on: boolean) => void;
}

/** The static value of a variable (`valueVariable`), seeded from the entry's static JSON (`initialStatic`). */
export function useStaticValue(valueVariable: DeliveryTemplateVariable, initialStatic: string | null, choice: Choice): StaticValue {
  const typedMode = staticModeFor(valueVariable, null);
  const [jsonMode, setJsonMode] = useState(() => typedMode === "json" || staticModeFor(valueVariable, initialStatic) === "json");
  const [staticState, setStaticState] = useState<StaticState>(() => seedStatic(valueVariable, initialStatic));

  const mode: StaticMode = jsonMode ? "json" : typedMode;
  const staticResult = choice === "Static" ? staticJsonOf(mode, staticState, valueVariable) : { json: null, error: null };

  const toggleJson = (on: boolean) => {
    if (on) {
      const typed = staticJsonOf(typedMode, staticState, valueVariable);
      setStaticState((current) => ({ ...current, json: typed.json ?? current.json }));
      setJsonMode(true);
      return;
    }

    // Back to the typed editor only when it can show what the JSON holds.
    if (staticModeFor(valueVariable, staticState.json) !== "json") {
      setStaticState(seedStatic(valueVariable, staticState.json));
      setJsonMode(false);
    }
  };

  return { typedMode, jsonMode, mode, state: staticState, setState: setStaticState, result: staticResult, toggleJson };
}
