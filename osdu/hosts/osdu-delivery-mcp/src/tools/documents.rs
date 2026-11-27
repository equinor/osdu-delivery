//! The documents a delivery renders with: mappings, the templates they fill (files of their repository's template folders,
//! read from the file), and the OSDU data definitions a template's file is downloaded from. All of it is definition, not
//! data: a document, a schema, a check of one against the other.

use std::collections::HashMap;

use serde::Deserialize;
use serde_json::{json, Map, Value};
use sqlflow_mcp::rmcp::handler::server::wrapper::Parameters;
use sqlflow_mcp::rmcp::{self, schemars, tool, tool_router};
use sqlflow_mcp::{encode, json_str};

use super::{DeliveryTools, PIPELINE_HINT};
use crate::links::{explorer_kind, template_page};
use crate::support::{composed, guid, optional_guid, put_links, refuse, section, text, Query};

/// The most characters a string of a value profile's task carries back; the counts are short.
const PROFILE_TEXT_LIMIT: usize = 4_000;

impl DeliveryTools {
    /// Profiles the values of each advised column in the raw table its pre-ingestion flow lands it in: one
    /// `inferColumnTypes` task per table, on a node, for the columns of it the answer advises. Each column gains
    /// `valueProfile` (the type inference would choose, the rows profiled and the counts behind it, or why there is none)
    /// and, where its advice names the count that must cover every value, `valuesFit`. Counts and types only.
    async fn profile_columns(&self, answer: &mut Value) {
        let Some(columns) = answer.get_mut("columns").and_then(Value::as_array_mut) else {
            return;
        };

        // The columns of each raw table, so one task profiles all of a table's columns.
        let mut tables: Vec<(Value, Vec<usize>)> = Vec::new();
        for (index, column) in columns.iter().enumerate() {
            let target = &column["profile"];
            if target.is_null() {
                continue;
            }
            let table = json!([target["reference"], target["database"], target["schema"], target["table"]]);
            match tables.iter_mut().find(|(key, _)| *key == table) {
                Some((_, indexes)) => indexes.push(index),
                None => tables.push((table, vec![index])),
            }
        }

        let mut profiles: Vec<(usize, Value)> = Vec::new();
        for (table, indexes) in &tables {
            let names: Vec<Value> = indexes.iter().map(|&i| columns[i]["profile"]["column"].clone()).collect();
            let mut body = json!({
                "operation": "inferColumnTypes",
                "reference": table[0],
                "schema": table[2],
                "objectName": table[3],
                "columns": names,
            });
            if !table[1].is_null() {
                body["database"] = table[1].clone();
            }
            let task = match self.ctx.send("/api/v1/datasources/tasks", body).await {
                Ok(accepted) => self.ctx.follow_task("inferColumnTypes", &accepted, Some(PROFILE_TEXT_LIMIT)).await,
                Err(error) => Err(error),
            };
            for &index in indexes {
                let profile = match &task {
                    Ok(task) if task["status"] == json!("succeeded") => {
                        let name = columns[index]["profile"]["column"].as_str().unwrap_or_default().to_lowercase();
                        task["result"]["columns"]
                            .as_array()
                            .and_then(|rows| rows.iter().find(|row| row["columnName"].as_str().is_some_and(|c| c.to_lowercase() == name)))
                            .map(|row| {
                                json!({
                                    "inferredType": row["inferredType"],
                                    "profiled": row["profiled"],
                                    "rows": row["rows"],
                                    "evidence": row["evidence"],
                                })
                            })
                            .unwrap_or_else(|| json!({ "error": "the profile of the raw table holds no such column" }))
                    }
                    Ok(task) => json!({ "error": task["error"].clone(), "status": task["status"].clone() }),
                    Err(error) => json!({ "error": format!("{error:#}") }),
                };
                profiles.push((index, profile));
            }
        }

        for (index, column) in columns.iter_mut().enumerate() {
            let profile = profiles
                .iter()
                .find(|(i, _)| *i == index)
                .map(|(_, p)| p.clone())
                .unwrap_or_else(|| json!({ "error": "lineage reaches no raw table this column is landed in, so its values cannot be profiled" }));
            let fit = match (column["profileCount"].as_str(), profile.get("evidence")) {
                (Some(count), Some(evidence)) if evidence.is_object() => {
                    Some(evidence[count].as_i64().is_some() && evidence[count] == evidence["nonNull"])
                }
                _ => None,
            };
            if let Some(map) = column.as_object_mut() {
                map.insert("valueProfile".to_string(), profile);
                if let Some(fit) = fit {
                    map.insert("valuesFit".to_string(), json!(fit));
                }
            }
        }
    }
}

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
pub struct ColumnTypesInput {
    /// The delivery flow's pipeline id: the `id` of a delivery flow in list_pipelines.
    #[serde(rename = "pipelineId")]
    pub pipeline_id: String,
    /// The interface of a flow that declares several.
    pub interface: Option<String>,
    /// The partition, for a flow that names its partitions.
    pub partition: Option<String>,
    /// One column, by name; omit for every column the mapping reads.
    pub column: Option<String>,
    /// true also profiles each column's values on a node, in the raw table the pre-ingestion flow lands them in: counts,
    /// lengths and the type inference would choose, never a value.
    pub profile: Option<bool>,
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
        description = "Check a mapping document against the template it fills: whether it loads, every issue (error \
or warning, with its variable), the variables it fills, and requiredAndEmpty, the properties the schema requires \
that nothing fills, which make OSDU refuse a record. Run it on every mapping YAML you write or edit and fix every \
error before showing or proposing it (it is to a mapping what validate_flow is to a flow). Give repoId for a mapping \
of a synced repository: its template folders settle the template, as its runs do. shape=true adds the record's \
structure with placeholders for row and cache values. Reads no row, renders nothing."
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

        // The template checked against, on the Templates page by the version the mapping pins, and the records of its kind
        // OSDU holds, in the explorer.
        let mut template = json!({ "kind": coverage["kind"], "version": coverage["version"] });
        if let (Some(kind), Some(version)) = (coverage["kind"].as_str(), coverage["version"].as_str()) {
            let mut links = Map::new();
            links.insert("template".to_string(), json!(template_page(self.ctx.links(), kind, version)));
            links.insert("osduKind".to_string(), json!(explorer_kind(self.ctx.links(), kind, None)));
            put_links(&mut template, links);
        }
        let mut answer = Map::new();
        answer.insert("loads".to_string(), json!(loads));
        answer.insert("valid".to_string(), json!(loads && !has_error));
        answer.insert("template".to_string(), template);
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
delivery_templates): mapping YAML holding the ACL and legal entries to fill and, when a partition is named, a cache \
lookup for each single-valued reference its cached types answer, with the issues left. This is how a new mapping \
starts; do not write one from scratch. Then add the properties the template requires from the source's columns, fill \
the ACL and legal lists, finish the lookups, run delivery_check_mapping with the repoId until valid, and propose it \
with propose_pipelines into the mappings folder beside the template's folder. Writes nothing."
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
template folders. Without arguments: every file with repoId, repository, path, the kind and version it holds or why \
none, and the parent kind a custom template links to ($parent). With repoId and path: that template by variable \
(path, type, format, required, the entity types an id refers to, pattern, description) and its children ($child). \
Use it for 'which properties does a Wellbore require' and whenever you write or review a mapping: a variable not in \
the template cannot be filled. Narrow with search or requiredOnly. A kind no repository holds: \
delivery_osdu_schemas."
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
        // The file's own page, beside what the rules linked on it (the records of its kind in the explorer).
        let mut page = Map::new();
        page.insert(
            "page".to_string(),
            json!(self.ctx.links().route(&format!("/delivery/templates?repoId={}&path={}", encode(&repo), encode(&path)))),
        );
        put_links(&mut detail, page);
        json_str(&detail)
    }

    #[tool(
        description = "The SQL type each column a delivery flow's mapping reads should have, for the OSDU properties it \
reaches: per column, the property paths and their template types, its type in the catalog, the type its pre-ingestion \
flow declares and the one its last run detected (declaredInFlow is the flow to change), and the advice: the type to \
declare, a verdict (fits, parsed, approximate, refused when the render holds every record, unknown) and why. Use it for \
'what datatype should elevation be', 'why are records held as not a number'. profile=true adds each column's value \
counts from the raw table the pre flow lands it in."
    )]
    async fn delivery_column_types(&self, Parameters(i): Parameters<ColumnTypesInput>) -> String {
        let pipeline = match guid("pipelineId", &i.pipeline_id, PIPELINE_HINT) {
            Ok(pipeline) => pipeline,
            Err(refused) => return refused,
        };
        let query = Query::new().text("interface", i.interface).text("partition", i.partition).text("column", i.column);
        let mut answer = match self.ctx.read(&format!("/api/v1/delivery/flows/{pipeline}/column-types"), query.pairs()).await {
            Ok(answer) => answer,
            Err(error) => return refuse(format!("{error:#}")),
        };
        if i.profile.unwrap_or(false) {
            self.profile_columns(&mut answer).await;
        }

        let mut links = Map::new();
        links.insert("page".to_string(), json!(self.ctx.links().pipeline(&pipeline)));
        put_links(&mut answer, links);
        if let Some(map) = answer.as_object_mut() {
            map.insert(
                "readingThis".to_string(),
                json!(
                    "Declare `recommended` for a column in the transform.columns of declaredInFlow, where the type a column \
                     reaches the delivery with is set. `refused` holds every record that reads the column; `parsed` holds \
                     each record whose value cannot be read as the property needs it. With a profile, valuesFit says whether \
                     every value profiled converts to what the property needs (the evidence count profileCount names \
                     against nonNull)."
                ),
            );
        }
        json_str(&answer)
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
        description = "Compare two versions of an OSDU kind from the data definitions: what moving a mapping from one to \
the other takes. Returns counts of breaking, additive and wording changes; every synced mapping of the kind with its \
state and what it has to change; and every variable that differs, with what it asks of a mapping. Use it for 'what \
breaks if we move WellLog from 1.4.0 to 1.5.0', 'which of our mappings need changing'. Kinds per release come from \
delivery_osdu_schemas(release)."
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

        // The report on the move, which the GUI's Report copies: the comparison's changes with what each asks of a
        // mapping, and every synced mapping of the kind set against the newer version. It carries no file's text, which is
        // the diff a person reads in the GUI and would only crowd the changes out here.
        let query = query.text("format", Some("json".to_string()));
        self.ctx.get("/api/v1/delivery/templates/osdu/compare/report", query.pairs()).await
    }
}
