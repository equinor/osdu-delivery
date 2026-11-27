// Templates as the delivery API answers them: the template files of the synced repositories, a template laid out
// variable by variable, and the releases of the OSDU data definitions, their schemas and how two versions of a kind
// compare.

/** The shape of the value a template variable takes. */
export type DeliveryTemplateShape = "Value" | "ValueList" | "Group" | "GroupList" | "Whole";

/** Who writes a template variable: a mapping, OSDU Delivery itself (the id and the kind), or OSDU when it stores the record. */
export type DeliveryTemplateRole = "Mapping" | "Engine" | "Osdu";

/**
 * A file of a synced repository's template folder, as the repository's last sync found it: the template it holds, or why it
 * holds none. The file is the template; the control plane reads it where the repository was synced, and keeps none of it.
 */
export interface DeliveryTemplate {
  repoId: string;
  /** The repository's name. */
  repository: string;
  /** The file's path in the repository, such as welldb/templates/osdu_wks_work-product-component--WellLog_1.4.0.json. */
  path: string;
  /** The template folder it is in, such as welldb/templates. */
  folder: string;
  /** The kind it holds; null when it holds no template. */
  kind: string | null;
  /** The template version it holds, the hash of its schema; null when it holds none. */
  version: string | null;
  /** Why it holds no template; null when it holds one. */
  problem: string | null;
  /** When the repository's last sync saw it. */
  seenUtc: string;
  /** The kind the template is linked to as its parent (x-osdu-inheriting-from-kind); null for a template linked to none. */
  parent?: string | null;
}

/** The repository file a template laid out was read from. */
export interface DeliveryTemplateFile {
  repoId: string;
  repository: string;
  path: string;
}

/**
 * What a template a document of a synced repository names settles to, from the template folders the repository's last
 * sync recorded on the document's path; `suggestedPath` is where a file of the template goes for the document to read it.
 */
export interface DeliveryTemplateResolution {
  found: boolean;
  kind: string;
  version?: string | null;
  file?: string | null;
  problem?: string | null;
  suggestedPath: string;
}

/** One variable of a template: a property of the OSDU record, with what the schema says about it. */
export interface DeliveryTemplateVariable {
  /** The variable's path, such as osdu.data.Name or osdu.data.Curves[].CurveUnit; `[]` steps into an array of objects. */
  path: string;
  shape: DeliveryTemplateShape;
  type: string;
  itemType: string | null;
  format: string | null;
  required: boolean;
  role: DeliveryTemplateRole;
  relationships: string[];
  pattern: string | null;
  unitContext: string | null;
  title: string | null;
  description: string | null;
  /** For an object with free keys (tags): the type of the value under any key. Entries target `<path>.<name>`. */
  keyValueType: string | null;
  /** A list inside a repeated item: listed for reference, never fillable. */
  nested: boolean;
  /** The repository's cached types this variable can be read from, when a repository was given. */
  cacheTypes: string[];
  /** An object the schema leaves open (data.ExtensionProperties): entries may target any path inside it. */
  open: boolean;
}

/**
 * A template laid out variable by variable, parents before their children in schema order. `file` is the repository file it
 * was read from; null for a schema of the OSDU data definitions being looked at.
 */
export interface DeliveryTemplateDetail {
  kind: string;
  version: string;
  title: string | null;
  description: string | null;
  file: DeliveryTemplateFile | null;
  variables: DeliveryTemplateVariable[];
  /** The kind the template is linked to as its parent, whose records its mappings read with $parent; null for none. */
  parent?: string | null;
  /**
   * The kinds of the repository's templates linked to this one as their parent, whose records a mapping of this template
   * can link as children with $child; empty when none is.
   */
  children?: string[];
}

/**
 * A release of the OSDU data definitions, the Open Group's public repository of OSDU schemas: its version tag, the commit
 * the tag names, when that was committed, and the release's schema folder in the repository.
 */
export interface DeliveryOsduRelease {
  name: string;
  commit: string;
  publishedUtc: string | null;
  webUrl: string;
  /** The release is in the control plane's local copy, so reading it needs no download. */
  local: boolean;
}

/** The releases, newest first, and when the control plane last read the list from the repository. */
export interface DeliveryOsduReleases {
  syncedUtc: string | null;
  releases: DeliveryOsduRelease[];
}

/** What a sync did: the release list as read just now, and the releases it downloaded into the local copy. */
export interface DeliveryOsduSync {
  syncedUtc: string;
  releases: DeliveryOsduRelease[];
  downloaded: string[];
}

/** A record schema a release publishes: its kind, its status in the release, and its file with a link to it. */
export interface DeliveryOsduSchema {
  kind: string;
  entityType: string;
  version: string;
  /** PUBLISHED, DEVELOPMENT or OBSOLETE; null when the release's index gives none. */
  status: string | null;
  /** The file under the release's Generated folder, such as master-data/Wellbore.1.3.0.json. */
  path: string;
  webUrl: string;
}

/** Every record schema one release publishes, by entity type and newest version first. */
export interface DeliveryOsduSchemaIndex {
  release: DeliveryOsduRelease;
  schemas: DeliveryOsduSchema[];
}

/** A kind's schema bundled from a release: the template version its file holds, and where it came from. Nothing is kept. */
export interface DeliveryOsduSchemaFile {
  kind: string;
  version: string;
  release: DeliveryOsduRelease;
  path: string;
  webUrl: string;
  origin: string;
  schema: Record<string, unknown>;
}

/** What a difference between two template versions means for a mapping written for the older one. */
export type DeliveryTemplateChangeImpact = "Breaking" | "Additive" | "Wording";

/** A field of a variable that differs between the versions; `before` is null for a new variable, `after` for a removed one. */
export interface DeliveryTemplateFieldChange {
  /** type, format, pattern, keyValueType, unitContext, role, nested, required, relationships, title or description. */
  field: string;
  before: string | null;
  after: string | null;
  impact: DeliveryTemplateChangeImpact;
}

/** A variable that differs between two versions of a template. */
export interface DeliveryTemplateVariableChange {
  path: string;
  change: "Added" | "Removed" | "Changed";
  impact: DeliveryTemplateChangeImpact;
  role: DeliveryTemplateRole;
  fields: DeliveryTemplateFieldChange[];
}

/** One side of a comparison: a kind's version in a release, its status there, the template version it saves as, and its file as published. */
export interface DeliveryOsduComparisonSide {
  kind: string;
  release: DeliveryOsduRelease;
  path: string;
  webUrl: string;
  status: string | null;
  templateVersion: string;
  fileText: string;
}

/** A shared schema file the two versions refer to whose published text differs; a path, link and text are null where a version does not refer to it. */
export interface DeliveryOsduReferencedFile {
  /** The file's name without its version, such as AbstractFacility. */
  name: string;
  fromPath: string | null;
  toPath: string | null;
  fromWebUrl: string | null;
  toWebUrl: string | null;
  fromText: string | null;
  toText: string | null;
}

/** The formats the report on moving from one version of a kind to another is taken in: Markdown to read or paste, JSON for a tool. */
export type TemplateMoveReportFormat = "md" | "json";

/** Two versions of a kind from the OSDU data definitions, compared as files and variable by variable. */
export interface DeliveryOsduComparison {
  from: DeliveryOsduComparisonSide;
  to: DeliveryOsduComparisonSide;
  sameFile: boolean;
  /** The files say the same thing once each one's own kind and file name are set aside. */
  onlyIdentifiersDiffer: boolean;
  sameTemplate: boolean;
  breaking: number;
  additive: number;
  wording: number;
  unchanged: number;
  changes: DeliveryTemplateVariableChange[];
  /** How many of the shared schema files both versions refer to are the same. */
  sameReferencedFiles: number;
  /** The shared schema files that differ, or that only one version refers to. */
  referencedFiles: DeliveryOsduReferencedFile[];
}
