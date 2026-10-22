//! The documents a delivery renders with: mappings, the templates they fill (files of their repository's template folders,
//! read from the file), and the OSDU data definitions a template's file is downloaded from. All of it is definition, not
//! data: a document, a schema, a check of one against the other.

use std::collections::HashMap;

use serde::Deserialize;
use serde_json::{json, Map, Value};
use sqlflow_mcp::rmcp::handler::server::wrapper::Parameters;
use sqlflow_mcp::rmcp::{self, schemars, tool, tool_router};
use sqlflow_mcp::{encode, json_str};

use super::DeliveryTools;
use crate::support::{composed, optional_guid, refuse, section, text, Query};

#[derive(Debug, Deserialize, schemars::JsonSchema)]
pub struct MappingsInput {
    /// Read ONE mapping with its YAML and the flows that render with it: its `id` from a listing.
    #[serde(rename = "mappingId")]
    pub mapping_id: Option<String>,
    /// The mappings of one repository (its id).
    #[serde(rename = "repoId")]
    pub repo_id: Option<String>,
    /// "valid", or "invalid" for the mappings the sync could not read.
    pub status: Option<String>,
}

#[derive(Debug, Deserialize, schemars::JsonSchema)]
pub struct CheckMappingInput {
    /// The mapping document's full YAML text.
    pub yaml: String,
    /// The file's repo-relative path, used in messages (default "mapping.yaml").
    pub path: Option<String>,
    /// The synced repository the mapping is a document of (delivery_mapping gives it): its template folders settle a
    /// template the mapping names by kind alone. Without it the mapping must name its template's version.
    #[serde(rename = "repoId")]
    pub repo_id: Option<String>,
    /// true also returns the shape of the record the mapping renders, with a placeholder for every value that
    /// comes from a row or the cache.
    pub shape: Option<bool>,
    /// With shape: mapping parameter values to draw it with, by name.
    pub parameters: Option<HashMap<String, String>>,
}

#[derive(Debug, Deserialize, schemars::JsonSchema)]
pub struct ScaffoldMappingInput {
    /// The synced repository whose template file the mapping fills (a `repoId` from delivery_templates).
    #[serde(rename = "repoId")]
    pub repo_id: String,
    /// The template file's path in that repository, for example "welldb/templates/osdu_wks_master-data--Wellbore_1.5.1.json".
    #[serde(rename = "templatePath")]
    pub template_path: String,
    /// The mapping's name: flows pin it as `name@mappingVersion`.
    pub name: String,
    /// The mapping's own version, for example "1.0.0".
    #[serde(rename = "mappingVersion")]
    pub mapping_version: String,
    /// The source system the mapping reads records of.
    pub system: String,
    /// A partition whose cached types the lookups are prefilled from and checked against. Omit for none.
    pub partition: Option<String>,
}

#[derive(Debug, Deserialize, schemars::JsonSchema)]
pub struct TemplatesInput {
    /// With `path`: read the template one repository's file holds, variable by variable. Omit both to list the files.
    #[serde(rename = "repoId")]
    pub repo_id: Option<String>,
    /// The file's path in the repository, as a listing gives it.
    pub path: Option<String>,
    /// A partition, so each variable names the cached types of it that can supply it.
    pub partition: Option<String>,
    /// Keep variables whose path contains this text, for example "VerticalMeasurement".
    pub search: Option<String>,
    /// true keeps only the variables the schema requires.
    #[serde(rename = "requiredOnly")]
    pub required_only: Option<bool>,
}

#[derive(Debug, Deserialize, schemars::JsonSchema)]
pub struct OsduSchemasInput {
    /// A release tag. Omit to list the releases; alone, lists the schemas the release publishes.
    pub release: Option<String>,
    /// With release: one kind (for example "osdu:wks:master-data--Wellbore:1.5.1") to read the schema of.
    pub kind: Option<String>,
    /// With release alone: keep kinds containing this text, for example "WellLog".
    pub search: Option<String>,
}

#[derive(Debug, Deserialize, schemars::JsonSchema)]
pub struct OsduCompareInput {
    #[serde(rename = "fromRelease")]
    pub from_release: String,
    /// The earlier kind with its version, for example "osdu:wks:work-product-component--WellLog:1.4.0".
    #[serde(rename = "fromKind")]
    pub from_kind: String,
    /// May be the same release as fromRelease.
    #[serde(rename = "toRelease")]
    pub to_release: String,
    /// The later kind with its version.
    #[serde(rename = "toKind")]
    pub to_kind: String,
}

#[tool_router(router = documents_router, vis = "pub(crate)")]
impl DeliveryTools {
    #[tool(
        description = "Mappings: the documents that say how an ingestion row becomes an OSDU record. Without mappingId, \
lists them as the repository sync found them: reference (name@version, what a flow pins), OSDU kind, file, and status \
with the loader's message for one it could not read. With mappingId, returns one with its YAML and every flow \
interface that renders with it. Use it for 'which mapping does this flow use', 'which flows does changing this \
mapping affect', 'why is this mapping invalid'. The mapping language is in the docs (search_docs \"mapping\"). Before \
proposing a changed mapping, run delivery_check_mapping on it."
    )]
    async fn delivery_mappings(&self, Parameters(i): Parameters<MappingsInput>) -> String {
        let ids = (
            optional_guid("mappingId", i.mapping_id.as_deref(), "A mapping id is the `id` of a listed mapping."),
            optional_guid("repoId", i.repo_id.as_deref(), "A repository id is the `id` of a repo in list_repos."),
        );
        let (mapping, repo) = match ids {
            (Ok(mapping), Ok(repo)) => (mapping, repo),
            (Err(refused), _) | (_, Err(refused)) => return refused,
        };

        let Some(mapping) = mapping else {
            let query = Query::new().text("repoId", repo).text("status", i.status);
            return self.ctx.get("/api/v1/delivery/mappings", query.pairs()).await;
        };

        let base = format!("/api/v1/delivery/mappings/{mapping}");
        let detail = match self.ctx.read(&base, &[]).await {
            Ok(detail) => detail,
            Err(error) => return refuse(format!("{error:#}")),
        };
        let flows = self.ctx.read(&format!("{base}/flows"), &[]).await;
        let mut links = Map::new();
        links.insert(
            "page".to_string(),
            json!(self.ctx.links().route(&format!("/delivery/mappings/build?mappingId={mapping}"))),
        );
        composed(
            vec![("mapping", detail), ("flows", section(flows))],
            links,
            "mapping.yaml is the document as the repository holds it. flows lists every interface of every delivery \
             flow of the repository that renders with it, in every partition; an empty list means no flow pins it.",
        )
    }

    #[tool(
        description = "Check a mapping document against the template it fills. Returns whether it loads, every issue \
found (error or warning, with the variable it concerns), the variables it fills, and requiredAndEmpty: properties the \
schema requires that nothing fills, which is what makes OSDU refuse a record. Run it on every mapping YAML you write \
or edit, and fix every error, before showing or proposing it (it is to a mapping what validate_flow is to a flow). \
Give repoId for a mapping of a synced repository: its template folders settle the template, as its runs do. \
shape=true also returns the record's structure with placeholders for row and cache values. No row is read and \
nothing is rendered."
    )]
    async fn delivery_check_mapping(&self, Parameters(i): Parameters<CheckMappingInput>) -> String {
        if i.yaml.trim().is_empty() {
            return refuse("Give the mapping document's YAML text.");
        }
        let path = text(i.path);
        let repo_id = text(i.repo_id);
        let request = json!({ "yaml": i.yaml, "path": path, "repoId": repo_id });
        let coverage = match self.ctx.send("/api/v1/delivery/mapping-builder/coverage", request).await {
            Ok(coverage) => coverage,
            Err(error) => return refuse(format!("{error:#}")),
        };

        let empty = Vec::new();
        let variables = coverage["variables"].as_array().unwrap_or(&empty);
        let issues = coverage["issues"].as_array().cloned().unwrap_or_default();
        let has_error = issues.iter().any(|issue| issue["severity"] == json!("error"));
        // A document that did not load, or whose template cannot be read, answers with an error and no variables.
        let loads = !(variables.is_empty() && has_error);

        let state_is = |variable: &Value, state: &str| {
            variable["state"].as_str().is_some_and(|s| s.eq_ignore_ascii_case(state))
        };
        let targets = |keep: &dyn Fn(&Value) -> bool| -> Vec<Value> {
            variables.iter().filter(|v| keep(v)).map(|v| v["target"].clone()).collect()
        };
        let always = targets(&|v| state_is(v, "Always") && v["direct"] == json!(true));
        let sometimes = targets(&|v| state_is(v, "Sometimes") && v["direct"] == json!(true));
        let required_and_empty = targets(&|v| v["required"] == json!(true) && state_is(v, "Empty"));

        let mut answer = Map::new();
        answer.insert("loads".to_string(), json!(loads));
        answer.insert("valid".to_string(), json!(loads && !has_error));
        answer.insert(
            "template".to_string(),
            json!({ "kind": coverage["kind"], "version": coverage["version"] }),
        );
        answer.insert("issues".to_string(), Value::Array(issues));
        answer.insert("requiredAndEmpty".to_string(), Value::Array(required_and_empty));
        answer.insert("filledOnEveryRow".to_string(), Value::Array(always));
        answer.insert("filledOnSomeRows".to_string(), Value::Array(sometimes));
        answer.insert("templateVariables".to_string(), json!(variables.len()));

        if i.shape.unwrap_or(false) {
            let request = json!({ "yaml": i.yaml, "path": path, "parameters": i.parameters.unwrap_or_default(), "repoId": repo_id });
            let shape = self.ctx.send("/api/v1/delivery/mapping-builder/shape", request).await;
            answer.insert("shape".to_string(), section(shape));
        }
        answer.insert(
            "readingThis".to_string(),
            json!(
                "valid is true when the document loads and no issue is an error. requiredAndEmpty must be empty before \
                 the mapping can deliver. filledOnEveryRow and filledOnSomeRows name the variables an entry targets \
                 itself; a holder filled through its children is not listed. shape.record is null when the shape \
                 could not be drawn, and shape.issues says why."
            ),
        );
        json_str(&Value::Object(answer))
    }

    #[tool(
        description = "Scaffold a mapping for a template file a synced repository holds (repoId and path from \
delivery_templates). Returns mapping YAML holding the ACL and legal entries to fill and, when a partition is named, a \
cache lookup for each single-valued reference its cached types can answer, with the issues still to resolve. This is \
how a new mapping starts; do not write one from scratch. Then add the properties the template requires from the \
source's columns, fill the ACL and legal lists, finish the lookups, run delivery_check_mapping with the repoId until \
valid, and propose it with propose_pipelines into the mappings folder beside the template's folder. Writes nothing."
    )]
    async fn delivery_scaffold_mapping(&self, Parameters(i): Parameters<ScaffoldMappingInput>) -> String {
        let required = [
            ("templatePath", &i.template_path),
            ("name", &i.name),
            ("mappingVersion", &i.mapping_version),
            ("system", &i.system),
        ];
        if let Some((name, _)) = required.iter().find(|(_, value)| value.trim().is_empty()) {
            return refuse(format!("{name} is required to scaffold a mapping."));
        }
        let repo_id = match optional_guid("repoId", Some(i.repo_id.as_str()), "A repository id is the `repoId` of a file delivery_templates lists.") {
            Ok(Some(repo_id)) => repo_id,
            Ok(None) => return refuse("repoId is required to scaffold a mapping: the repository whose template file it fills."),
            Err(refused) => return refused,
        };
        let template_path = i.template_path.trim().to_string();
        let partition = text(i.partition);
        // The draft goes back to the control plane exactly as the builder returned it, so both calls are made on
        // the client itself: nothing of this server's (a link on a row) may end up inside a draft.
        let builder = self.ctx.control_plane();
        let draft = builder
            .post(
                "/api/v1/delivery/mapping-builder/draft",
                json!({
                    "scope": partition,
                    "repoId": repo_id,
                    "templatePath": template_path,
                    "name": i.name.trim(),
                    "mappingVersion": i.mapping_version.trim(),
                    "system": i.system.trim(),
                }),
            )
            .await;
        let draft = match draft {
            Ok(draft) => draft,
            Err(error) => return refuse(format!("{error:#}")),
        };
        // The template the draft fills, as the builder read it from the file.
        let template = json!({
            "kind": draft["templateKind"],
            "version": draft["templateVersion"],
            "repoId": repo_id,
            "path": template_path,
        });
        let composed = builder
            .post(
                "/api/v1/delivery/mapping-builder/compose",
                json!({ "scope": partition, "draft": draft, "parameters": {} }),
            )
            .await;
        match composed {
            Ok(result) => json_str(&json!({
                "yaml": result["yaml"],
                "valid": result["valid"],
                "issues": result["issues"],
                "template": template,
                "partition": partition,
                "nextSteps": [
                    "Add the properties the template requires from the source's columns, fill the ACL and legal lists, and finish each lookup's findBy column.",
                    "Run delivery_check_mapping with this repoId on the edited YAML until valid is true.",
                    "Propose it with propose_pipelines as name@version.yaml in the mappings folder beside the template folder holding the template's file, so that folder is on its path, and pin it from the delivery flow as name@version."
                ],
            })),
            Err(error) => refuse(format!("{error:#}")),
        }
    }

    #[tool(
        description = "Templates: the OSDU record schemas mappings fill, held as files in the synced repositories' \
folders named templates. Without arguments, lists every file: repoId, repository, path, the kind and version it \
holds or why none, and the parent kind a custom template is linked to ($parent reads that record). With repoId and path, lays that file's template out variable by variable: path \
(osdu.data.FacilityName), type, format, whether required, the entity types an id refers to, pattern and description. \
Use it for 'which properties does a Wellbore require' and whenever you write or review a mapping: a variable not in \
the template cannot be filled. Narrow with search or requiredOnly. A kind no repository holds: delivery_osdu_schemas."
    )]
    async fn delivery_templates(&self, Parameters(i): Parameters<TemplatesInput>) -> String {
        let repo = match optional_guid("repoId", i.repo_id.as_deref(), "A repository id is the `repoId` of a listed template file.") {
            Ok(repo) => repo,
            Err(refused) => return refused,
        };
        let (repo, path) = match (repo, text(i.path)) {
            (None, None) => {
                let links = json!({ "page": self.ctx.links().route("/delivery/templates") });
                return self
                    .ctx
                    .get_about("/api/v1/delivery/templates", &[], ("listing", "templates"), links)
                    .await;
            }
            (Some(repo), Some(path)) => (repo, path),
            _ => return refuse("Give both repoId and path to read one template file, or neither to list them."),
        };

        let query = Query::new()
            .text("repoId", Some(repo.clone()))
            .text("path", Some(path.clone()))
            .text("scope", i.partition);
        let mut detail = match self.ctx.read("/api/v1/delivery/templates/detail", query.pairs()).await {
            Ok(detail) => detail,
            Err(error) => return refuse(format!("{error:#}")),
        };

        let search = text(i.search).map(|s| s.to_lowercase());
        let required_only = i.required_only.unwrap_or(false);
        if let Some(variables) = detail.get_mut("variables").and_then(Value::as_array_mut) {
            let total = variables.len();
            variables.retain(|variable| {
                let path = variable["path"].as_str().unwrap_or_default().to_lowercase();
                search.as_ref().is_none_or(|term| path.contains(term))
                    && (!required_only || variable["required"] == json!(true))
            });
            let shown = variables.len();
            if let Some(map) = detail.as_object_mut() {
                map.insert("variablesInTemplate".to_string(), json!(total));
                map.insert("variablesShown".to_string(), json!(shown));
            }
        }
        if let Some(map) = detail.as_object_mut() {
            map.insert(
                "links".to_string(),
                json!({ "page": self.ctx.links().route(&format!(
                    "/delivery/templates?repoId={}&path={}", encode(&repo), encode(&path)
                )) }),
            );
        }
        json_str(&detail)
    }

    #[tool(
        description = "Browse the OSDU data definitions (the public repository of OSDU schemas) through the control \
plane's local copy. No argument lists the releases. A release lists the record schemas it publishes (narrow with \
search). A release and a kind return that kind's bundled schema and the template version its file holds: the file a \
repository's template folder keeps, which the Templates page downloads. Use it for 'which WellLog versions does this \
release publish', 'is there a newer version than the template our repository holds'. To see what changed between two \
versions use delivery_osdu_schema_compare. Reading writes nothing."
    )]
    async fn delivery_osdu_schemas(&self, Parameters(i): Parameters<OsduSchemasInput>) -> String {
        let (release, kind) = (text(i.release), text(i.kind));
        match (release, kind) {
            (None, Some(_)) => refuse("Name the release to read the kind's schema from; call this with no argument to list the releases."),
            (None, None) => self.ctx.get("/api/v1/delivery/templates/osdu/releases", &[]).await,
            (Some(release), Some(kind)) => {
                let query = Query::new().text("release", Some(release)).text("kind", Some(kind));
                self.ctx.get("/api/v1/delivery/templates/osdu/schema", query.pairs()).await
            }
            (Some(release), None) => {
                let query = Query::new().text("release", Some(release));
                let mut index = match self.ctx.read("/api/v1/delivery/templates/osdu/schemas", query.pairs()).await {
                    Ok(index) => index,
                    Err(error) => return refuse(format!("{error:#}")),
                };
                let search = text(i.search).map(|s| s.to_lowercase());
                if let Some(schemas) = index.get_mut("schemas").and_then(Value::as_array_mut) {
                    let total = schemas.len();
                    if let Some(term) = &search {
                        schemas.retain(|schema| schema["kind"].as_str().unwrap_or_default().to_lowercase().contains(term));
                    }
                    let shown = schemas.len();
                    if let Some(map) = index.as_object_mut() {
                        map.insert("schemasInRelease".to_string(), json!(total));
                        map.insert("schemasShown".to_string(), json!(shown));
                    }
                }
                json_str(&index)
            }
        }
    }

    #[tool(
        description = "Compare two versions of an OSDU kind from the data definitions: what a mapping has to change to \
move from one to the other. Returns how many variable changes are breaking, additive or wording only, and every \
variable that differs (added, removed or changed) with the field that moved and what it means for a mapping. Use it \
for 'what breaks if we move WellLog from 1.4.0 to 1.5.0', 'is this upgrade additive'. Kinds and versions per release \
come from delivery_osdu_schemas(release)."
    )]
    async fn delivery_osdu_schema_compare(&self, Parameters(i): Parameters<OsduCompareInput>) -> String {
        let query = Query::new()
            .text("fromRelease", Some(i.from_release))
            .text("fromKind", Some(i.from_kind))
            .text("toRelease", Some(i.to_release))
            .text("toKind", Some(i.to_kind));
        if query.pairs().len() != 4 {
            return refuse("fromRelease, fromKind, toRelease and toKind are all required.");
        }
        match self.ctx.read("/api/v1/delivery/templates/osdu/compare", query.pairs()).await {
            Ok(mut comparison) => {
                // The two published files ride along whole, which is the diff a person reads in the GUI; here the
                // variable changes are the answer, and the files would only crowd them out.
                for side in ["from", "to"] {
                    if let Some(map) = comparison.get_mut(side).and_then(Value::as_object_mut) {
                        map.remove("fileText");
                    }
                }
                if let Some(files) = comparison.get_mut("referencedFiles").and_then(Value::as_array_mut) {
                    for file in files {
                        if let Some(map) = file.as_object_mut() {
                            map.remove("fromText");
                            map.remove("toText");
                        }
                    }
                }
                json_str(&comparison)
            }
            Err(error) => refuse(format!("{error:#}")),
        }
    }
}
