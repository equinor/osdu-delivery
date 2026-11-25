import { useEffect, useState } from "react";

/**
 * The height an element takes, followed as it changes, and zero while it is not there. A callback ref rather than a
 * `useRef`, so the observer follows the node when the element is taken away and brought back.
 */
export function useElementHeight(): [(node: HTMLElement | null) => void, number] {
  const [node, setNode] = useState<HTMLElement | null>(null);
  const [height, setHeight] = useState(0);

  useEffect(() => {
    if (node === null) {
      return;
    }

    const measure = () => setHeight(Math.ceil(node.getBoundingClientRect().height));
    measure();

    const observer = new ResizeObserver(measure);
    observer.observe(node);
    return () => observer.disconnect();
  }, [node]);

  return [setNode, node === null ? 0 : height];
}
