//! Dimension flows, as metadata: which dimensions exist, what each reads, how many values and keys it holds, and how
//! its builds went. The values and keys themselves are what OSDU records hold, which is data, so they are not read here.

use serde::Deserialize;
use serde_json::{json, Map, Value};
use sqlflow_mcp::rmcp::handler::server::wrapper::Parameters;
use sqlflow_mcp::rmcp::{self, schemars, tool, tool_router};
use sqlflow_mcp::{encode, json_str, GuiLinks};

use super::{DeliveryTools, PIPELINE_HINT};
use crate::links::flow_page;
use crate::support::{composed, optional_guid, put_links, refuse, relinked, section, text, Query};

#[derive(Debug, Deserialize, schemars::JsonSchema)]
pub struct DimensionsInput {
    /// Read ONE dimension with its recent builds: the `dimensionId` a board lists. Other arguments are ignored.
    #[serde(rename = "dimensionId")]
    pub dimension_id: Option<i64>,
    /// One dimension flow's board: its pipeline id, `id` in list_pipelines(kind="dimension"). Omit for every flow.
    #[serde(rename = "pipelineId")]
    pub pipeline_id: Option<String>,
    /// The partition to read the board in. Omit for the one a run of the flow would build in.
    pub partition: Option<String>,
    /// With dimensionId: most builds, newest first (default 10, max 500).
    #[serde(rename = "maxBuilds")]
    pub max_builds: Option<i64>,
    /// With pipelineId: lay out how this dimension (its name in the flow) is built instead of the board.
    pub blueprint: Option<String>,
}

#[derive(Debug, Deserialize, schemars::JsonSchema)]
pub struct FlowFileInput {
    /// The kind of flow written: `dimension` (the default) or `cache`.
    #[serde(rename = "flowType")]
    pub flow_type: Option<String>,
    /// The whole flow document to check, exactly as its file is to hold it. Omit it to read the patterns a new flow
    /// follows first.
    pub yaml: Option<String>,
    /// With yaml: the file the document is meant for, relative to its repository (`flows/acme-wellbore-dimensions.yaml`),
    /// which the issues name.
    pub path: Option<String>,
}

#[tool_router(router = dimensions_router, vis = "pub(crate)")]
impl DeliveryTools {
    #[tool(
        description = "Dimensions: the distinct values OSDU records hold at a path (every operator, every curve type), collected by dimension flows. Returns \
definitions and build health, never the values. The board lists each with its OSDU kind, query and path, how many \
values and keys it holds, when it was last built and whether that build failed. dimensionId returns one with its \
recent builds. pipelineId and blueprint (a dimension's name) lay out how it is built: its YAML beside what each line \
does. Use it for 'which dimensions exist', 'why did the last build fail', 'what does this YAML make'. Build one by \
running its flow with trigger_run."
    )]
    async fn delivery_dimensions(&self, Parameters(i): Parameters<DimensionsInput>) -> String {
        if let Some(dimension) = i.dimension_id {
            if dimension <= 0 {
                return refuse("dimensionId is the positive number a board lists for a built dimension.");
            }
            let base = format!("/api/v1/delivery/dimensions/{dimension}");
            let detail = match self.ctx.read(&base, &[]).await {
                Ok(detail) => detail,
                Err(error) => return refuse(format!("{error:#}")),
            };
            let query = Query::new().value("max", Some(i.max_builds.unwrap_or(10).clamp(1, 500)));
            let builds = self.ctx.read(&format!("{base}/builds"), query.pairs()).await;
            let mut links = Map::new();
            links.insert(
                "page".to_string(),
                json!(self.ctx.links().route(&format!("/delivery/dimensions?d={dimension}"))),
            );
            return composed(
                vec![("dimension", detail), ("builds", section(builds))],
                links,
                "dimension.dimension is the dimension as its flow declares it and as its builds settled it; \
                 dimension.pipelineId is the flow to build it with (null when the catalog no longer holds the flow). \
                 builds are newest first.",
            );
        }

        let pipeline = match optional_guid("pipelineId", i.pipeline_id.as_deref(), PIPELINE_HINT) {
            Ok(pipeline) => pipeline,
            Err(refused) => return refused,
        };
        let partition = text(i.partition);
        let query = Query::new().text("partition", partition.clone());
        if let Some(name) = text(i.blueprint) {
            let Some(pipeline) = pipeline else {
                return refuse("A blueprint is read from its flow: give pipelineId too.");
            };
            let path = format!("/api/v1/delivery/flows/{pipeline}/dimensions/{}/blueprint", encode(&name));
            return self.ctx.get(&path, query.pairs()).await;
        }
        let board = match pipeline {
            Some(pipeline) => {
                let links = json!({
                    "page": flow_page(self.ctx.links(), &pipeline, None, partition.as_deref(), &[("tab", "dimensions")]),
                    "flow": self.ctx.links().pipeline(&pipeline),
                });
                self.ctx
                    .get_about(
                        &format!("/api/v1/delivery/flows/{pipeline}/dimensions"),
                        query.pairs(),
                        ("pipelineId", &pipeline),
                        links,
                    )
                    .await
            }
            None => {
                let links = json!({ "page": self.ctx.links().route("/delivery/dimensions") });
                self.ctx
                    .get_about("/api/v1/delivery/dimensions", query.pairs(), ("listing", "dimensions"), links)
                    .await
            }
        };
        relinked(board, |board| link_unbuilt(board, self.ctx.links()))
    }

    #[tool(
        description = "Write a whole dimension or cache flow file and check it; this is how a new one starts. flowType is dimension (the \
default) or cache. Without yaml: the catalog's flows of that type, each with its envelope (the document without its \
dimensions and views, or its types, as written), and the connections the estate's flows write their data through. \
Copy the nearest flow's envelope, references and all. A dimension flow keeps its tables in the estate's data \
database under target.connection, not the module's; a cached type goes with the cache flow that declares it \
already, when one does. With yaml: loads, valid, issues, and the flow it replaces. Run it until valid before \
showing the file. Writes nothing."
    )]
    async fn delivery_flow_file(&self, Parameters(i): Parameters<FlowFileInput>) -> String {
        let cache = match text(i.flow_type).as_deref() {
            None | Some("dimension") => false,
            Some("cache") => true,
            Some(other) => return refuse(format!("flowType is dimension or cache; '{other}' is neither.")),
        };
        let Some(yaml) = i.yaml.filter(|yaml| !yaml.trim().is_empty()) else {
            let read = if cache {
                self.ctx.read("/api/v1/delivery/cache/flow-patterns", &[]).await
            } else {
                self.ctx.read("/api/v1/delivery/dimensions/flow-patterns", &[]).await
            };
            let mut patterns = match read {
                Ok(patterns) => patterns,
                Err(error) => return refuse(format!("{error:#}")),
            };
            if let Some(flows) = patterns.get_mut("flows").and_then(Value::as_array_mut) {
                for flow in flows.iter_mut() {
                    if let Some(pipeline) = flow.get("pipelineId").and_then(Value::as_str).map(str::to_string) {
                        let mut links = Map::new();
                        links.insert("flow".to_string(), json!(self.ctx.links().pipeline(&pipeline)));
                        put_links(flow, links);
                    }
                }
            }
            if let Value::Object(answer) = &mut patterns {
                let reading = if cache {
                    "flows are the catalog's cache flows; envelope is each one's document without its types, as written, \
                     and envelopeCut says it was cut. types name what each flow caches already: a type of a name is one \
                     type in a partition's cache, so a new type of that name goes in that flow or agrees with it. \
                     connection is what a flow's table types read. connections are counted by the kind of flow writing \
                     or reading them. moreFlows counts the flows left out."
                } else {
                    "flows are the catalog's dimension flows; envelope is each one's document without its dimensions \
                     and views, as written, and envelopeCut says it was cut. target is null for a flow keeping its \
                     tables in the module's database. connections are counted by the kind of flow writing or reading \
                     them; module marks the module's own. moreFlows counts the flows left out."
                };
                answer.insert("readingThis".to_string(), json!(reading));
            }
            return json_str(&patterns);
        };

        let request = json!({ "yaml": yaml, "path": text(i.path) });
        let checked = if cache {
            self.ctx.send("/api/v1/delivery/cache/flow-check", request).await
        } else {
            self.ctx.send("/api/v1/delivery/dimensions/flow-check", request).await
        };
        let mut check = match checked {
            Ok(check) => check,
            Err(error) => return refuse(format!("{error:#}")),
        };
        if let Some(pipeline) = check.pointer("/existing/pipelineId").and_then(Value::as_str).map(str::to_string) {
            if let Some(existing) = check.get_mut("existing") {
                let mut links = Map::new();
                links.insert("flow".to_string(), json!(self.ctx.links().pipeline(&pipeline)));
                put_links(existing, links);
            }
        }
        if let Value::Object(answer) = &mut check {
            let reading = if cache {
                "valid is true when the document loads and no issue is an error; warnings do not stop it. Each issue \
                 names the type it is about, when it is about one. types are what a refresh would cache, each under the \
                 name a mapping reads it by. existing is the catalog's flow of the same name, null when there is none."
            } else {
                "valid is true when the document loads and no issue is an error; warnings do not stop it. Each \
                 issue names the dimension it is about, when it is about one. dimensions[].table is where a build \
                 writes each table. existing is the catalog's flow of the same name, null when there is none."
            };
            answer.insert("readingThis".to_string(), json!(reading));
        }
        json_str(&check)
    }
}

/// Gives each dimension no build has registered yet its page, which names it by its flow and its name until a build
/// gives it an id (`dimensionRef` in the GUI). A dimension's row names no flow; the flow around it does.
fn link_unbuilt(value: &mut Value, links: &GuiLinks) {
    match value {
        Value::Array(items) => items.iter_mut().for_each(|item| link_unbuilt(item, links)),
        Value::Object(map) => {
            let pipeline = map.get("pipelineId").and_then(Value::as_str).map(str::to_string);
            for (name, nested) in map.iter_mut() {
                match (name.as_str(), pipeline.as_deref(), nested) {
                    ("dimensions", Some(pipeline), Value::Array(dimensions)) => {
                        for dimension in dimensions.iter_mut().filter(|d| d.get("dimensionId").is_some_and(Value::is_null)) {
                            if let Some(declared) = dimension.get("name").and_then(Value::as_str).map(str::to_string) {
                                let reference = format!("{pipeline}/{declared}");
                                let mut page = Map::new();
                                page.insert(
                                    "page".to_string(),
                                    json!(links.route(&format!("/delivery/dimensions?d={}", encode(&reference)))),
                                );
                                put_links(dimension, page);
                            }
                        }
                    }
                    (_, _, nested) => link_unbuilt(nested, links),
                }
            }
        }
        _ => {}
    }
}
