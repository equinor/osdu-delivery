//! The ledger's records: finding one across every flow, and reconstructing everything that happened to it.

use serde::Deserialize;
use serde_json::{json, Map, Value};
use sqlflow_mcp::rmcp::handler::server::wrapper::Parameters;
use sqlflow_mcp::rmcp::{self, schemars, tool, tool_router};

use super::{DeliveryTools, RECORD_HINT};
use crate::support::{clip_field, composed, guid, optional_guid, refuse, section, Query, LOG_PREVIEW};

#[derive(Debug, Deserialize, schemars::JsonSchema)]
pub struct FindRecordsInput {
    /// A delivery key (GUID), or the START of an OSDU id, a source key, a label or an ingestion file name. Prefix
    /// match: give the beginning of the value, an OSDU id from its partition on ("dev:master-data--Wellbore:12").
    /// Omit to list the newest records.
    pub search: Option<String>,
    /// One custody state: pending, delivering, delivered, held, failed, deleted or waiting.
    pub status: Option<String>,
    /// One flow's ledger: the `flowId` a hit carries (not a pipeline id).
    #[serde(rename = "flowId")]
    pub flow_id: Option<String>,
    /// One OSDU data partition. Omit for all.
    pub partition: Option<String>,
    pub page: Option<i64>,
    /// Default 25, max 200.
    #[serde(rename = "pageSize")]
    pub page_size: Option<i64>,
}

#[derive(Debug, Deserialize, schemars::JsonSchema)]
pub struct RecordInput {
    /// `flowId` of a hit from delivery_find_records or delivery_flow_records: the ledger's flow id, NOT the pipeline id.
    #[serde(rename = "flowId")]
    pub flow_id: String,
    /// `deliveryKey` of the same hit.
    #[serde(rename = "deliveryKey")]
    pub delivery_key: String,
    /// Parts of the trace to read beside the state: "attempts", "chain", "activities". Omit for all three; pass []
    /// for the state alone.
    pub include: Option<Vec<String>>,
    /// Most attempts and most activities, each, newest first (default 20, max 500).
    pub max: Option<i64>,
}

const SECTIONS: [&str; 3] = ["attempts", "chain", "activities"];

#[tool_router(router = records_router, vis = "pub(crate)")]
impl DeliveryTools {
    #[tool(
        description = "Find delivery records across every flow by what you hold: a delivery key, or the start of an OSDU \
id, source key, label or ingestion file name (indexed prefix match; text from the middle of a value finds nothing). \
Each hit gives the flow, interface, partition, custody state, OSDU id, last delivery time, and the source file and \
row. Start here for any question about one record ('was wellbore X delivered', 'which records came from this file'), \
then pass a hit's flowId and deliveryKey to delivery_record. With no search it lists the newest records. \
totalCapped=true means more match than were counted: narrow with status, flowId or partition."
    )]
    async fn delivery_find_records(&self, Parameters(i): Parameters<FindRecordsInput>) -> String {
        let flow_id = match optional_guid("flowId", i.flow_id.as_deref(), RECORD_HINT) {
            Ok(flow_id) => flow_id,
            Err(refused) => return refused,
        };
        let query = Query::new()
            .text("search", i.search)
            .text("status", i.status)
            .text("flowId", flow_id)
            .text("partition", i.partition)
            .page(i.page, i.page_size);
        self.ctx.get("/api/v1/delivery/records", query.pairs()).await
    }

    #[tool(
        description = "Everything the ledger holds about one record. `record` is its state: custody state, the OSDU id and version it \
landed as, pending work, attempt count, last error, what it waits for. Its trace: `attempts` (each delivery try with \
outcome, phase, run and error), `chain` (the ingestion file and row it came from, and the run behind each change of \
the row) and `activities` (who did what to it and when). Use it for 'why did this record fail', 'who deleted it', \
'which file is it from', 'when was it delivered and as which version'. Takes flowId AND deliveryKey from \
delivery_find_records or delivery_flow_records. Ledger bookkeeping only: the record's content is never returned."
    )]
    async fn delivery_record(&self, Parameters(i): Parameters<RecordInput>) -> String {
        let (flow_id, key) = match (
            guid("flowId", &i.flow_id, RECORD_HINT),
            guid("deliveryKey", &i.delivery_key, RECORD_HINT),
        ) {
            (Ok(flow_id), Ok(key)) => (flow_id, key),
            (Err(refused), _) | (_, Err(refused)) => return refused,
        };

        let wanted: Vec<String> = match i.include {
            None => SECTIONS.iter().map(|s| s.to_string()).collect(),
            Some(named) => named.iter().map(|s| s.trim().to_ascii_lowercase()).filter(|s| !s.is_empty()).collect(),
        };
        if let Some(unknown) = wanted.iter().find(|s| !SECTIONS.contains(&s.as_str())) {
            return refuse(format!(
                "include names '{unknown}', which is not a part of a record's trace. The parts are: {}.",
                SECTIONS.join(", ")
            ));
        }
        let wants = |part: &str| wanted.iter().any(|s| s == part);

        let base = format!("/api/v1/delivery/records/{flow_id}/{key}");
        // The record itself decides whether there is anything to answer with: an unknown record is the tool's answer,
        // not a trace of failed sections.
        let record = match self.ctx.read(&base, &[]).await {
            Ok(record) => record,
            Err(error) => return refuse(format!("{error:#}")),
        };

        let max = Query::new().value("max", Some(i.max.unwrap_or(20).clamp(1, 500)));
        let attempts_path = format!("{base}/attempts");
        let chain_path = format!("{base}/chain");
        let activities_path = format!("{base}/activities");
        let (attempts, chain, activities) = tokio::join!(
            async {
                if wants("attempts") { Some(self.ctx.read(&attempts_path, max.pairs()).await) } else { None }
            },
            async { if wants("chain") { Some(self.ctx.read(&chain_path, &[]).await) } else { None } },
            async {
                if wants("activities") { Some(self.ctx.read(&activities_path, max.pairs()).await) } else { None }
            },
        );

        // The trace is about the record, so its explorer link (once it has landed in OSDU) goes on the envelope beside its page.
        let landed = record.pointer("/record/links/osdu").cloned();
        let mut sections: Vec<(&str, Value)> = vec![("record", record)];
        if let Some(read) = attempts {
            sections.push(("attempts", section(read)));
        }
        if let Some(read) = chain {
            sections.push(("chain", section(read)));
        }
        if let Some(read) = activities {
            let mut listed = section(read);
            clip_field(&mut listed, "log", LOG_PREVIEW);
            sections.push(("activities", listed));
        }

        let mut links = Map::new();
        links.insert(
            "page".to_string(),
            json!(self.ctx.links().route(&format!("/delivery/records/{flow_id}/{key}"))),
        );
        if let Some(osdu) = landed {
            links.insert("osdu".to_string(), osdu);
        }
        composed(
            sections,
            links,
            "record.record is the current state; record.pipelineId and record.flowName name the flow that holds it \
             (null when no synced flow does any more). attempts and activities are newest first, at most `max` each; \
             an activity's run log is cut short here and whole in delivery_activities(activityId). A part that could \
             not be read carries an `error` instead of its content.",
        )
    }
}
