// Access restrictions (osdu/docs/reference/concepts/access-restriction.md): per OSDU partition, a named set of entitlements
// groups a record's ACL in OSDU can be replaced with, so no one outside them reads it, without removing anything. A removal
// of scope "restrict" picks one by name; lifting it gives each record back the ACL it had. Same conventions as delivery.ts:
// one function per endpoint, pages compose them with TanStack Query.

import { del, get, post, put } from "@/api/client";

/** One registered access restriction, and how many records carry it now. */
export interface AccessRestriction {
  restrictionId: number;
  /** The data-partition-id the restriction's groups belong to: records of that partition alone are restricted under it. */
  partition: string;
  /** What operators pick it by, unique within its partition. */
  name: string;
  /** What it is for (a legal hold, a quality quarantine); null when no one said. */
  description: string | null;
  /** The groups a restricted record's `acl.viewers` becomes. */
  viewers: string[];
  /** The groups a restricted record's `acl.owners` becomes; the flow's identity has to be a member of one to lift it. */
  owners: string[];
  /** Records whose access is restricted under it now, or asked to be. It is removed only while none is. */
  records: number;
  createdUtc: string;
  createdBy: string;
  updatedUtc: string;
  updatedBy: string;
}

/** A restriction to register. Its groups never change once registered; a different set of groups is a restriction of its own. */
export interface AccessRestrictionAddRequest {
  partition: string;
  name: string;
  description: string | null;
  viewers: string[];
  owners: string[];
}

/** The query key every list of restrictions is cached under, so a change refreshes each. */
export const ACCESS_RESTRICTIONS_KEY = ["delivery", "access-restrictions"] as const;

export const accessRestrictionsApi = {
  /** The restrictions of one partition, by name, each with its records; of every partition when none is named. */
  list: (partition?: string | null) =>
    get<AccessRestriction[]>("/api/v1/delivery/access-restrictions", partition ? { partition } : {}),
  /** Registers a restriction for a registered partition. Admin only. */
  add: (request: AccessRestrictionAddRequest) => post<AccessRestriction>("/api/v1/delivery/access-restrictions", request),
  /** Says what a restriction is for; empty clears it. Its groups stay as registered. Admin only. */
  describe: (restrictionId: number, description: string | null) =>
    put<AccessRestriction>(`/api/v1/delivery/access-restrictions/${restrictionId}`, { description }),
  /** Removes a restriction no record carries; refused (409) while records carry it. Admin only. */
  remove: (restrictionId: number) => del<void>(`/api/v1/delivery/access-restrictions/${restrictionId}`),
};
