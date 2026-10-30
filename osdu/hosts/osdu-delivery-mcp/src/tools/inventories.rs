//! Inventory and removal flows, as metadata: what each reads, how its last builds and runs went, and what waits for an
//! operator. The ids an inventory holds and the keys a removal found are not read here, and nothing here removes anything:
//! a removal is an operator's decision, made in the GUI.

use serde::Deserialize;
use serde_json::{json, Map};
use sqlflow_mcp::rmcp::handler::server::wrapper::Parameters;
use sqlflow_mcp::rmcp::{self, schemars, tool, tool_router};

use super::{DeliveryTools, PIPELINE_HINT};
use crate::links::flow_page;
use crate::support::{composed, optional_guid, section, text, Query};

#[derive(Debug, Deserialize, schemars::JsonSchema)]
pub struct InventoriesInput {
    /// One inventory flow: its pipeline id, `id` in list_pipelines(kind="inventory"). Omit for every inventory.
    #[serde(rename = "pipelineId")]
    pub pipeline_id: Option<String>,
    /// The partition to read. Omit for every partition.
    pub partition: Option<String>,
}

#[derive(Debug, Deserialize, schemars::JsonSchema)]
pub struct RemovalsInput {
    /// One removal flow, with its recent runs: its pipeline id, `id` in list_pipelines(kind="removal"). Omit for every
    /// removal flow of the partition.
    #[serde(rename = "pipelineId")]
    pub pipeline_id: Option<String>,
    /// The partition to read. Omit for the one the flow serves.
    pub partition: Option<String>,
    /// With pipelineId: most runs, newest first (default 10).
    #[serde(rename = "maxRuns")]
    pub max_runs: Option<i64>,
}

#[tool_router(router = inventories_router, vis = "pub(crate)")]
impl DeliveryTools {
    #[tool(
        description = "Inventories: what an inventory flow found OSDU holding of a kind: its query, its last build and \
reconcile, the count per finding and its newest run, never the ids. Use it for 'which inventories exist', 'how many \
orphans did it find'. pipelineId narrows it to one flow. A removal of what one found is made in the GUI."
    )]
    async fn delivery_inventories(&self, Parameters(i): Parameters<InventoriesInput>) -> String {
        let pipeline = match optional_guid("pipelineId", i.pipeline_id.as_deref(), PIPELINE_HINT) {
            Ok(pipeline) => pipeline,
            Err(refused) => return refused,
        };
        let query = Query::new().text("partition", text(i.partition));
        match pipeline {
            Some(pipeline) => {
                let path = format!("/api/v1/delivery/flows/{pipeline}/inventories");
                self.ctx.get(&path, query.pairs()).await
            }
            None => self.ctx.get("/api/v1/delivery/inventories", query.pairs()).await,
        }
    }

    #[tool(
        description = "Removal flows: each one's guard and, per removal, the delivery flow it writes to and the deletions \
awaiting approval. With pipelineId, also its recent runs with their tallies and error. Use it for 'what will this \
removal take out', 'why did the guard hold it'. It returns no key; approving and removing are made in the GUI."
    )]
    async fn delivery_removals(&self, Parameters(i): Parameters<RemovalsInput>) -> String {
        let pipeline = match optional_guid("pipelineId", i.pipeline_id.as_deref(), PIPELINE_HINT) {
            Ok(pipeline) => pipeline,
            Err(refused) => return refused,
        };
        let partition = text(i.partition);
        let query = Query::new().text("partition", partition.clone());
        let Some(pipeline) = pipeline else {
            return self.ctx.get("/api/v1/delivery/removals", query.pairs()).await;
        };

        let base = format!("/api/v1/delivery/removals/flows/{pipeline}");
        let flow = match self.ctx.read(&base, query.pairs()).await {
            Ok(flow) => flow,
            Err(error) => return crate::support::refuse(format!("{error:#}")),
        };
        let limit = i.max_runs.unwrap_or(10).clamp(1, 1000);
        let runs_query = Query::new().text("partition", partition.clone()).value("limit", Some(limit));
        let runs = self.ctx.read(&format!("{base}/runs"), runs_query.pairs()).await;
        let mut links = Map::new();
        links.insert(
            "page".to_string(),
            json!(flow_page(self.ctx.links(), &pipeline, None, partition.as_deref(), &[])),
        );
        composed(
            vec![("flow", flow), ("runs", section(runs))],
            links,
            "flow.removals lists what the flow takes out and what waits for approval; runs are newest first.",
        )
    }
}
