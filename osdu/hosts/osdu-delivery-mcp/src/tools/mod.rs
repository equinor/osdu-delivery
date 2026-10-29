//! The delivery module's tools.
//!
//! Every tool is a proxy over the module's own endpoints under `/api/v1/delivery` (osdu/src/SqlFlow.Delivery.ControlPlane),
//! the same ones the GUI and the CLI call. Nothing here reaches OSDU or the ledger's database: a read answers from the
//! ledger through the control plane, and anything that acts (a release, a redelivery, a read of what OSDU holds) goes
//! through the endpoint that records it in the ledger under the caller's name. A tool that composes several reads into
//! one answer still only reads.
//!
//! The tools are grouped as the product is: the ledger's records, flows and submissions, the audit trail, mappings and
//! templates, the partition caches, assertions, dimensions, and the operator's actions.

mod assertions;
mod cache;
mod dimensions;
mod documents;
mod flows;
mod operate;
mod records;

use sqlflow_mcp::rmcp::handler::server::router::tool::ToolRouter;
use sqlflow_mcp::McpContext;

/// The service every delivery tool is a method of: the context its requests go through.
#[derive(Clone)]
pub struct DeliveryTools {
    pub(crate) ctx: McpContext,
}

impl DeliveryTools {
    pub fn new(ctx: McpContext) -> Self {
        DeliveryTools { ctx }
    }

    /// Every delivery tool, one router per group added up.
    pub fn router() -> ToolRouter<Self> {
        Self::records_router()
            + Self::flows_router()
            + Self::documents_router()
            + Self::cache_router()
            + Self::assertions_router()
            + Self::dimensions_router()
            + Self::operate_router()
    }
}

/// The hint every tool gives for a pipeline id that is not one.
pub(crate) const PIPELINE_HINT: &str =
    "A pipeline id is the `id` of a flow in list_pipelines (kind delivery, retrieval, cache, assertion, dimension, inventory, removal or download), \
     or the `pipelineId` a delivery result carries.";

/// The hint for the two ids a record is addressed by.
pub(crate) const RECORD_HINT: &str =
    "A record is addressed by two ids together, both on every hit of delivery_find_records and every row of \
     delivery_flow_records: `flowId` (the ledger's identity of the flow's interface, NOT the pipeline id) and \
     `deliveryKey`.";
