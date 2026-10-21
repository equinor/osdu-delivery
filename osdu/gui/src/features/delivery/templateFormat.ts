import { deliveryApi, type DeliveryTemplateRole, type DeliveryTemplateVariable } from "../../api/delivery";

/**
 * How the template a repository's file holds is read as JSON text, by the Schema JSON tab and by the download alike, so the
 * file is the text the tab shows. The file is read where the repository was synced, so a page opened after the repository
 * changed reads it again.
 */
export function templateSchemaQuery(repoId: string, path: string) {
  return {
    queryKey: ["delivery", "templates", "schema", repoId, path] as const,
    queryFn: () => deliveryApi.templateSchema(repoId, path),
  };
}

/**
 * How a kind's template file is read from a release of the OSDU data definitions, by the download: a release is a tag at a
 * fixed commit, so what it holds never changes.
 */
export function osduTemplateFileQuery(release: string, kind: string) {
  return {
    queryKey: ["delivery", "osdu-definitions", "file", release, kind] as const,
    queryFn: () => deliveryApi.osduSchemaFile(release, kind),
    staleTime: Infinity,
  };
}

/**
 * The file a template downloads as: its kind, each colon an underscore
 * (osdu_wks_work-product-component--WellLog_1.4.0.json), the name the control plane gives the schema it answers
 * (TemplateSources.FileName) and the one the sample's template folder, osdu/samples/welldb/templates, names its schemas by.
 */
export function templateFileName(kind: string): string {
  return `${kind.replace(/:/g, "_")}.json`;
}

/** A variable's shape as a person reads it: the type of one value, a list of what, an object, or a list of objects. */
export function shapeText(variable: Pick<DeliveryTemplateVariable, "shape" | "type" | "itemType">): string {
  switch (variable.shape) {
    case "Value":
      return variable.type;
    case "ValueList":
      return `list of ${variable.itemType ?? "values"}`;
    case "Group":
      return "object";
    case "GroupList":
      return "list of objects";
    case "Whole":
      return `whole ${variable.type}`;
  }
}

/** How deep a variable sits below the record's sections: osdu.data.Name is 1, osdu.data.Curves[].CurveUnit is 2. */
export function pathDepth(path: string): number {
  return Math.max(0, path.split(".").length - 2);
}

/**
 * The variable that holds a path: osdu.data for osdu.data.Name, osdu.data.Curves for osdu.data.Curves[].CurveUnit. Null for
 * a property of the record itself (osdu.data, osdu.id), which no variable holds.
 */
export function holderPath(path: string): string | null {
  const at = path.lastIndexOf(".");
  if (at <= 0) {
    return null;
  }

  const holder = path.slice(0, at).replace(/\[\]$/, "");
  return holder === "osdu" ? null : holder;
}

/** A path split into what leads to the variable and the variable's own name, so the name can stand out. */
export function splitPath(path: string): { parent: string; leaf: string } {
  const at = path.lastIndexOf(".");
  return at < 0 ? { parent: "", leaf: path } : { parent: path.slice(0, at + 1), leaf: path.slice(at + 1) };
}

/** Who writes a variable no mapping may fill, as a short label; null for a variable a mapping fills. */
export function roleLabel(role: DeliveryTemplateRole): string | null {
  switch (role) {
    case "Engine":
      return "OSDU Delivery writes it";
    case "Osdu":
      return "OSDU sets it";
    case "Mapping":
      return null;
  }
}

/** The entity name of an OSDU kind, such as WellLog for osdu:wks:work-product-component--WellLog:1.4.0. */
export function entityName(kind: string): string {
  const parts = kind.split(":");
  const entityType = parts.length >= 3 ? parts[2] : kind;
  const at = entityType.lastIndexOf("--");
  return at < 0 ? entityType : entityType.slice(at + 2);
}

/**
 * An OSDU kind cut where a reader's eye goes: what leads to the entity name (osdu:wks:master-data--), the name itself
 * (Wellbore), and the version after it (:1.3.0). The three concatenate back to the kind; a string that is not an
 * authority:source:entity:version kind is all name.
 */
export function splitKind(kind: string): { prefix: string; entity: string; version: string } {
  const parts = kind.split(":");
  if (parts.length !== 4) {
    return { prefix: "", entity: kind, version: "" };
  }

  const at = parts[2].lastIndexOf("--");
  const cut = at < 0 ? 0 : at + 2;
  return { prefix: `${parts[0]}:${parts[1]}:${parts[2].slice(0, cut)}`, entity: parts[2].slice(cut), version: `:${parts[3]}` };
}

/** One string naming a template version, for a select's value. A kind holds no spaces, so a space separates the two. */
export function templateKey(kind: string, version: string): string {
  return `${kind} ${version}`;
}

/** The kind and version a {@link templateKey} names, or null for a value that is not one. */
export function parseTemplateKey(key: string): { kind: string; version: string } | null {
  const at = key.lastIndexOf(" ");
  return at <= 0 || at === key.length - 1 ? null : { kind: key.slice(0, at), version: key.slice(at + 1) };
}

/** authority:source:entityType, which every version of a kind shares. */
export function kindStem(kind: string): string {
  return kind.split(":").slice(0, 3).join(":");
}

/** The last part of a path: the file's own name. */
export function fileName(path: string): string {
  return path.slice(path.lastIndexOf("/") + 1);
}
