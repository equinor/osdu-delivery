// What a record's page may do with what OSDU holds of it (osdu/docs/reference/concepts/record-lifecycle.md): read it back,
// verify it, or set a render beside it, and what its OSDU tab says before a read.

/** What a record says of its OSDU id, as the ledger answers it (DeliveryRecord). */
interface RecordTarget {
  targetId: string | null;
  status: string;
  claimed?: boolean;
}

/**
 * Whether OSDU may hold what the record's flow wrote of it, so there is something of the flow's to read back, verify or
 * set a render beside: the record has an OSDU id, claimed it (it queued a document once, or took over the record OSDU held
 * there), and was not removed. A record that was only ever held names the id its key makes, but its flow wrote nothing
 * there, and the control plane reads back only an id the record claimed. A control plane that does not say whether the
 * record claimed its id is taken at its id.
 */
export function wroteToOsdu(record: RecordTarget): boolean {
  return record.targetId !== null && record.status !== "deleted" && record.claimed !== false;
}

/** What the record's OSDU tab says before a read: what a read would show, or why there is nothing of its flow's to read. */
export function osduEmptyText(record: RecordTarget): { title: string; description: string } {
  if (wroteToOsdu(record)) {
    return {
      title: "Not read yet",
      description: "Read shows the record as OSDU holds it now, through its flow's route and credentials. Nothing is written.",
    };
  }

  if (record.status === "deleted") {
    return { title: "Removed from OSDU", description: "The record was removed, so there is nothing of this flow's to read." };
  }

  return record.targetId === null
    ? { title: "No OSDU id yet", description: "The record has not been planned for delivery, so there is nothing to read." }
    : { title: "Not sent yet", description: "The record has never queued a document for OSDU, so its flow wrote nothing there to read." };
}
