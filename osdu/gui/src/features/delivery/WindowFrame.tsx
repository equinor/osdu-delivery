import { useRef, type ReactNode } from "react";
import { useWindowFit } from "./useWindowFit";

/** The least height a frame keeps, so a short window still shows a few rows. */
const MIN_HEIGHT = 420;

/** What stays under a frame: the page's bottom padding and the workbench's status bar. */
const BELOW = 46;

/**
 * A frame as tall as the window leaves under where it starts, whatever it holds, so what is in it scrolls inside it and the
 * page does not. It fits itself when it mounts, and again when it is shown after being hidden.
 */
export function WindowFrame({ hidden = false, children, testId }: { hidden?: boolean; children: ReactNode; testId: string }) {
  const frame = useRef<HTMLDivElement>(null);
  useWindowFit(frame, BELOW, MIN_HEIGHT, undefined, "height");
  return <div ref={frame} hidden={hidden} className="flex min-h-0 flex-col" data-testid={testId}>{children}</div>;
}
