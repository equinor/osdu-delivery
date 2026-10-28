// The keys a grid whose row opens in the workbench's bottom panel listens for while the panel shows a row: Up and Down
// step through the rows, Escape closes it, unless the key went to something that keeps it.

/** Whether a key went to a field, which keeps every key it is given. */
export function typedInField(target: EventTarget | null): boolean {
  return target instanceof HTMLElement && (target.isContentEditable || ["INPUT", "TEXTAREA", "SELECT"].includes(target.tagName));
}

/** Whether an arrow key went to something that moves by arrows of its own: a field, a menu, a tree, a handle, the panel. */
export function arrowsKeptByTarget(target: EventTarget | null): boolean {
  return typedInField(target)
    || (target instanceof HTMLElement
      && target.closest("[role=menu],[role=listbox],[role=tree],[role=dialog],[role=separator],[data-testid=bottom-panel]") !== null);
}
