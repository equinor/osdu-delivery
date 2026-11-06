// What a page says of an access restriction (osdu/docs/reference/concepts/access-restriction.md) and checks of one before it
// is registered: the groups typed one per line, each checked as storage checks an ACL entry, and a restriction's groups in
// one line. Pure, so the unit tests run it without a browser.

/** The most groups a restriction names as its viewers, or as its owners (AccessRestrictionRegistry.MaxGroups). */
export const MAX_RESTRICTION_GROUPS = 20;

/** The longest name a restriction keeps (DeliveryAccessRestriction.MaxNameLength). */
export const MAX_RESTRICTION_NAME = 100;

/** The widest description a restriction keeps (DeliveryAccessRestriction.MaxDescriptionLength). */
export const MAX_RESTRICTION_DESCRIPTION = 400;

// A data group's email, as storage's ACL validator writes it (osdu/specs/core/INTEGRATION.md, 2.2: acl).
const DATA_GROUP = /^data\.[a-zA-Z0-9_+&*-]+(?:\.[a-zA-Z0-9_+&*-]+)*@(?:[a-zA-Z](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?\.)+[a-zA-Z](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?$/;

// A restriction's name, as the registry takes it: a letter or digit, then letters, digits, spaces, '.', '_' or '-'.
const NAME = /^[A-Za-z0-9][A-Za-z0-9 ._-]{0,99}$/;

/** Whether `group` is a data group's email storage accepts in an ACL. */
export function isDataGroup(group: string): boolean {
  return DATA_GROUP.test(group);
}

/** The groups typed one per line (commas and semicolons also separate them), trimmed, blank lines left out, each once. */
export function groupsOf(text: string): string[] {
  const seen = new Set<string>();
  const groups: string[] = [];
  for (const part of text.split(/[\n,;]/)) {
    const group = part.trim();
    if (group !== "" && !seen.has(group.toLowerCase())) {
      seen.add(group.toLowerCase());
      groups.push(group);
    }
  }

  return groups;
}

/** What is wrong with a list of groups as a restriction's `list` (viewers or owners), or null when it can be registered. */
export function groupsProblem(groups: readonly string[], list: "viewers" | "owners"): string | null {
  if (groups.length === 0) {
    return `Name at least one group as its ${list}: storage keeps no record with an empty ACL list.`;
  }

  if (groups.length > MAX_RESTRICTION_GROUPS) {
    return `At most ${MAX_RESTRICTION_GROUPS} groups as its ${list}; this names ${groups.length}.`;
  }

  const invalid = groups.filter((group) => !isDataGroup(group));
  if (invalid.length > 0) {
    return `${invalid.join(", ")} ${invalid.length === 1 ? "is not a data group" : "are not data groups"} storage accepts in an ACL: data.<name>@<domain>.`;
  }

  return null;
}

/** What is wrong with a restriction's name, or null when it can be registered. */
export function nameProblem(name: string): string | null {
  const trimmed = name.trim();
  if (trimmed === "") {
    return "Name it: operators pick it by name.";
  }

  return NAME.test(trimmed)
    ? null
    : `A letter or digit, then letters, digits, spaces, '.', '_' or '-', at most ${MAX_RESTRICTION_NAME} characters.`;
}

/** What a check of a flow answers (`POST /flows/{id}/access-check`): why it restricts nothing, and each restriction it was asked of. */
export interface RestrictionCheckAnswer {
  refusal?: string | null;
  restrictions: { restrictionId: number; usable: boolean; refusal?: string | null }[];
}

/**
 * Whether the flow can restrict under the restriction `restrictionId` and lift it again, as a check of the flow answered: usable,
 * or refused with the run's own words (the flow's refusal first, which holds for every restriction); null while no answer
 * names it (not asked yet, or registered since it was asked), which a restriction waits on.
 */
export function restrictionUse(check: RestrictionCheckAnswer | undefined, restrictionId: number): { usable: boolean; refusal: string | null } | null {
  if (check === undefined) {
    return null;
  }

  if (check.refusal != null) {
    return { usable: false, refusal: check.refusal };
  }

  const use = check.restrictions.find((one) => one.restrictionId === restrictionId);
  if (use === undefined) {
    return null;
  }

  return use.usable ? { usable: true, refusal: null } : { usable: false, refusal: use.refusal ?? "The flow cannot lift it again." };
}

/** A restriction's groups in one line: `viewers a, b; owners c`, or one list when both name the same groups. */
export function aclText(viewers: readonly string[], owners: readonly string[]): string {
  const same = viewers.length === owners.length && viewers.every((group) => owners.some((owner) => owner.toLowerCase() === group.toLowerCase()));
  return same ? `${viewers.join(", ")} (viewers and owners)` : `viewers ${viewers.join(", ")}; owners ${owners.join(", ")}`;
}
