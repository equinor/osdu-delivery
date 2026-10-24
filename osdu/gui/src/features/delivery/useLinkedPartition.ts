import { useEffect } from "react";
import { useSearchParams } from "react-router-dom";
import { isPartitionId, useActivePartition } from "./activePartition";

/**
 * A link that names a partition (`?partition=`) makes it the title bar's, so the page says which partition it is about
 * and reads that one: the explorer, the Records page and the audit trail are opened so, by another page or by an
 * assistant's answer about something in a partition other than the one last worked in. The link has then done its work,
 * and the title bar decides from there on.
 */
export function useLinkedPartition(): void {
  const [params, setParams] = useSearchParams();
  const [active, setActive] = useActivePartition();
  const linked = params.get("partition");
  useEffect(() => {
    if (linked === null) {
      return;
    }

    if (isPartitionId(linked) && linked !== active) {
      setActive(linked);
    }

    setParams((current) => {
      const next = new URLSearchParams(current);
      next.delete("partition");
      return next;
    }, { replace: true });
  }, [linked, active, setActive, setParams]);
}
