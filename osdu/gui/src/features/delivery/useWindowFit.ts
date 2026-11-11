import { useEffect, type RefObject } from "react";

/**
 * Keeps a scrolling element as tall as the window leaves below where it starts, less `below` (what stays under it: a
 * footer, the page's padding, the workbench's status bar), and never shorter than `min`, so its rows scroll inside it and
 * the page itself does not scroll for them. The height is set on the element, so rows never render at a height the next
 * frame changes.
 *
 * `target` picks the element to size inside the one `ref` holds (a table's own scrolling container); a module-level
 * function keeps the fit from being set up again on every render.
 *
 * `property` is what is set: the most the element may grow to (`maxHeight`, the default, for a list as tall as its rows up
 * to the room there is), or its height (`height`, for a frame that fills the room whatever it holds, such as panes side by
 * side). A frame given its height is watched itself as well, so one shown again after it was hidden is fitted to where it
 * now starts.
 */
export function useWindowFit(
  ref: RefObject<HTMLElement | null>,
  below: number,
  min: number,
  target?: (element: HTMLElement) => HTMLElement | null,
  property: "maxHeight" | "height" = "maxHeight",
): void {
  useEffect(() => {
    const element = ref.current;
    if (element === null) {
      return undefined;
    }

    const fit = () => {
      const fitted = target === undefined ? element : target(element);
      if (fitted !== null) {
        // A hidden element starts nowhere; it is fitted when it is shown, which the observer below sees.
        if (fitted.getClientRects().length === 0) {
          return;
        }

        const room = window.innerHeight - fitted.getBoundingClientRect().top - below;
        const height = `${Math.max(min, Math.floor(room))}px`;
        if (fitted.style[property] !== height) {
          fitted.style[property] = height;
        }
      }
    };

    // Where the element starts moves when the window does, or when something laid out before it changes height (a strip
    // that wraps, a filter bar that grows, a selection bar a table's toolbar shows): those are the elements before the one
    // sized and before each of its ancestors, inside `ref`'s element as well as outside it. None of them is sized by the
    // element, so fitting it never sets the observer off again; the fit waits for the next frame all the same, so a burst
    // of changes is fitted once.
    let frame = 0;
    const schedule = () => {
      window.cancelAnimationFrame(frame);
      frame = window.requestAnimationFrame(fit);
    };

    fit();
    const observer = new ResizeObserver(schedule);
    observer.observe(document.body);
    if (property === "height") {
      // Setting its height changes its size once; the fit that follows finds the height already set, and stops there.
      observer.observe(element);
    }

    const sized = (target === undefined ? element : target(element)) ?? element;
    const observeBefore = () => {
      for (let node: Element | null = sized; node !== null && node !== document.body; node = node.parentElement) {
        for (let before = node.previousElementSibling; before !== null; before = before.previousElementSibling) {
          observer.observe(before);
        }
      }
    };
    observeBefore();

    // An element put in before it later moves it too (a grid's toolbar, shown once its rows arrive say a problem), and is
    // watched from then on: the elements from the one sized up to `ref`'s are watched for children coming and going.
    // What happens inside the sized element itself, its rows, is not.
    const added = new MutationObserver(() => {
      observeBefore();
      schedule();
    });
    for (let node = sized === element ? null : sized.parentElement; node !== null && element.contains(node); node = node.parentElement) {
      added.observe(node, { childList: true });
    }

    window.addEventListener("resize", schedule);
    return () => {
      window.cancelAnimationFrame(frame);
      observer.disconnect();
      added.disconnect();
      window.removeEventListener("resize", schedule);
    };
  }, [ref, below, min, target, property]);
}
