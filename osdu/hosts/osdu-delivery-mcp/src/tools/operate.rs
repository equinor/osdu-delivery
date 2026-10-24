//! The operator's actions: the interventions the ledger records (release, redeliver, verify, sync, a decision on a
//! cache change) and the probe of a flow's target.
//!
//! Every tool here calls an endpoint under the control plane's `operate` policy, so a token without that scope is
//! refused by the control plane, and every intervention is written to the audit trail under the caller's name by the
//! endpoint itself: nothing here is a second path to OSDU. None of them returns data: an action answers with what
//! was queued or how many records it touched.
//!
//! What is deliberately not here: reading what OSDU holds, reading a source row, rendering a record, and removing
//! records from OSDU. The first three return data, which this server never does; the last is an operator's decision
//! made in the GUI, where the confirmation shows what it would take away.

use serde::Deserialize;
use serde_json::{json, Map, Value};
use sqlflow_mcp::rmcp::handler::server::wrapper::Parameters;
use sqlflow_mcp::rmcp::{self, schemars, tool, tool_router};
use sqlflow_mcp::{done, json_str};

use super::{DeliveryTools, PIPELINE_HINT, RECORD_HINT};
use crate::links::{flow_page, record_page};
use crate::support::{guid, optional_guid, put_links, refuse, text, Query};

/// The most records one call names by key, as a run's payload carries at most.
const MAX_KEYS: usize = 1000;

#[derive(Debug, Deserialize, schemars::JsonSchema)]
pub struct FlowTargetInput {
    /// The delivery flow's pipeline id: `id` in list_pipelines(kind="delivery").
    #[serde(rename = "pipelineId")]
    pub pipeline_id: String,
    /// Required for a source with several interfaces.
    pub interface: Option<String>,
    /// Required for a flow that names several partitions.
    pub partition: Option<String>,
}

#[derive(Debug, Deserialize, schemars::JsonSchema)]
pub struct RecordRefInput {
    /// `flowId` of a hit from delivery_find_records or delivery_flow_records: the ledger's flow id, NOT the pipeline id.
    #[serde(rename = "flowId")]
    pub flow_id: String,
    /// `deliveryKey` of the same hit.
    #[serde(rename = "deliveryKey")]
    pub delivery_key: String,
}

#[derive(Debug, Deserialize, schemars::JsonSchema)]
pub struct RecordFilter {
    /// One custody state: pending, delivering, delivered, held, failed, deleted or waiting.
    pub status: Option<String>,
    /// A term over the OSDU id, source key and label.
    pub search: Option<String>,
    /// "prefix" (default) or "contains".
    pub mode: Option<String>,
    #[serde(rename = "submissionId")]
    pub submission_id: Option<String>,
    #[serde(rename = "deliveredBy")]
    pub delivered_by: Option<String>,
    #[serde(rename = "runId")]
    pub run_id: Option<String>,
    pub drifted: Option<bool>,
}

#[derive(Debug, Deserialize, schemars::JsonSchema)]
pub struct SyncInput {
    /// Sync ONE record: its `flowId`, with `deliveryKey`.
    #[serde(rename = "flowId")]
    pub flow_id: Option<String>,
    #[serde(rename = "deliveryKey")]
    pub delivery_key: Option<String>,
    /// Sync records OF A FLOW: its pipeline id. With neither keys nor filter, every record of the interface.
    #[serde(rename = "pipelineId")]
    pub pipeline_id: Option<String>,
    /// With pipelineId: required for a source with several interfaces.
    pub interface: Option<String>,
    /// With pipelineId: required for a flow that names several partitions.
    pub partition: Option<String>,
    /// With pipelineId: delivery keys to sync, at most 1000. Not with filter.
    pub keys: Option<Vec<String>>,
    /// With pipelineId: sync every record this filter matches (the filter of delivery_flow_records), at most 1000.
    pub filter: Option<RecordFilter>,
    /// Required with filter: the `total` delivery_flow_records reported for the same filter. The sync is refused
    /// when the filter matches a different number now.
    pub expected: Option<i32>,
}

#[derive(Debug, Deserialize, schemars::JsonSchema)]
pub struct ReleaseInput {
    /// Release ONE record: its `flowId`, with `deliveryKey`.
    #[serde(rename = "flowId")]
    pub flow_id: Option<String>,
    #[serde(rename = "deliveryKey")]
    pub delivery_key: Option<String>,
    /// Release records OF A FLOW: its pipeline id, with keys or allHeld.
    #[serde(rename = "pipelineId")]
    pub pipeline_id: Option<String>,
    /// With pipelineId: required for a source with several interfaces.
    pub interface: Option<String>,
    /// With pipelineId: required for a flow that names several partitions.
    pub partition: Option<String>,
    /// With pipelineId: the delivery keys to release, at most 1000.
    pub keys: Option<Vec<String>>,
    /// With pipelineId and no keys: true releases EVERY held record of the interface. It has to be said.
    #[serde(rename = "allHeld")]
    pub all_held: Option<bool>,
}

#[derive(Debug, Deserialize, schemars::JsonSchema)]
pub struct RedeliverInput {
    /// The record's `flowId` (the ledger's flow id, not the pipeline id).
    #[serde(rename = "flowId")]
    pub flow_id: String,
    #[serde(rename = "deliveryKey")]
    pub delivery_key: String,
    /// The part to send again: "all" (default), "record" (the document alone), "files", "bulk" or "workflow". A
    /// part the record's route does not send is refused.
    pub scope: Option<String>,
    /// true (default) queues the deliver run now; false only marks the record for the flow's next run.
    pub run: Option<bool>,
    /// With run: the worker pool to route the run to.
    pub pool: Option<String>,
}

#[derive(Debug, Deserialize, schemars::JsonSchema)]
pub struct DecideCacheChangesInput {
    /// The `tagId` of each change, from delivery_cache_changes(status="pending"). At most 1000.
    #[serde(rename = "tagIds")]
    pub tag_ids: Vec<i64>,
    /// true approves (affected records are redelivered by their flows' next runs); false rejects (OSDU stays as it is).
    pub approve: bool,
}

#[tool_router(router = operate_router, vis = "pub(crate)")]
impl DeliveryTools {
    #[tool(
        description = "Probe a delivery flow's OSDU target (operator action): the control plane runs the flow's token exchange and reaches \
the service it delivers to, and the answer says whether it answered, with the status and the path asked. Use it for \
'is the dev OSDU reachable', 'why does every delivery fail with 401', and after credentials or configuration changed. \
It reads no records and writes nothing to OSDU; the probe is recorded in the audit trail under your name. It answers in seconds."
    )]
    async fn delivery_probe_target(&self, Parameters(i): Parameters<FlowTargetInput>) -> String {
        let pipeline = match guid("pipelineId", &i.pipeline_id, PIPELINE_HINT) {
            Ok(pipeline) => pipeline,
            Err(refused) => return refused,
        };
        let path = Query::new()
            .text("interface", i.interface)
            .text("partition", i.partition)
            .onto(&format!("/api/v1/delivery/flows/{pipeline}/probe"));
        self.ctx.post(&path, json!({})).await
    }

    #[tool(
        description = "Queue a verify run for one record (operator action): a node compares what OSDU holds with what the \
ledger delivered and records the outcome on the record; a difference is drift. Returns the run id: poll get_run, \
then read delivery_record (lastVerifiedUtc, lastVerifyOutcome). Use it when asked whether OSDU still holds what was \
delivered. To verify a whole flow, use trigger_run with operation=\"verify\"."
    )]
    async fn delivery_verify_record(&self, Parameters(i): Parameters<RecordRefInput>) -> String {
        let (flow_id, key) = match self.record_ref(Some(&i.flow_id), Some(&i.delivery_key)) {
            Ok(reference) => reference,
            Err(refused) => return refused,
        };
        let links = self.on_record(&flow_id, &key);
        self.act(&format!("/api/v1/delivery/records/{flow_id}/{key}/verify"), json!({}), links).await
    }

    #[tool(
        description = "Queue a sync run (operator action) that reconciles the ledger with the ingestion tables: for each \
record it records what the ledger lacks of its row, marks a row that changed unseen to be planned by the flow's next \
run, and notes a row that is gone. It sends nothing to OSDU. Give one record (flowId and deliveryKey), or a flow \
(pipelineId) with keys, a filter plus the expected count, or neither for every record of the interface. Returns the \
run id: poll get_run, then read its entry in delivery_activities. Use it when the ledger and the source disagree \
('the row changed but nothing was redelivered')."
    )]
    async fn delivery_sync_with_source(&self, Parameters(i): Parameters<SyncInput>) -> String {
        let by_record = i.flow_id.is_some() || i.delivery_key.is_some();
        match (by_record, i.pipeline_id.as_deref()) {
            (true, None) => {
                let (flow_id, key) = match self.record_ref(i.flow_id.as_deref(), i.delivery_key.as_deref()) {
                    Ok(reference) => reference,
                    Err(refused) => return refused,
                };
                let links = self.on_record(&flow_id, &key);
                self.act(&format!("/api/v1/delivery/records/{flow_id}/{key}/sync"), json!({}), links).await
            }
            (false, Some(pipeline)) => {
                let pipeline = match guid("pipelineId", pipeline, PIPELINE_HINT) {
                    Ok(pipeline) => pipeline,
                    Err(refused) => return refused,
                };
                let selection = match selection(i.keys, i.filter) {
                    Ok(selection) => selection,
                    Err(refused) => return refused,
                };
                let mut body = json!({});
                match selection {
                    Selection::Keys(keys) => body["keys"] = json!(keys),
                    Selection::Filter(filter) => {
                        let Some(expected) = i.expected else {
                            return refuse(
                                "A sync by filter needs `expected`: the total delivery_flow_records reports for the \
                                 same filter, so the sync is refused if the selection changed since you looked.",
                            );
                        };
                        body["filter"] = filter;
                        body["expected"] = json!(expected);
                    }
                    Selection::Everything => {}
                }
                let (interface, partition) = (text(i.interface), text(i.partition));
                let links = self.on_flow(&pipeline, interface.as_deref(), partition.as_deref());
                let path = Query::new()
                    .text("interface", interface)
                    .text("partition", partition)
                    .onto(&format!("/api/v1/delivery/flows/{pipeline}/sync"));
                self.act(&path, body, links).await
            }
            _ => refuse(
                "Give either one record (flowId and deliveryKey) or a flow (pipelineId, with keys, a filter, or \
                 neither for every record).",
            ),
        }
    }

    #[tool(
        description = "Release held records (operator action) so their flow's next run tries them again. A record is held \
when it used up its attempts or a gate stopped it; releasing says the cause is dealt with. Give one record (flowId \
and deliveryKey), or a flow (pipelineId) with keys, or allHeld=true for every held record of the interface. Returns \
how many were released and is recorded in the audit trail under your name; it sends nothing itself. Read why they are \
held first (delivery_flow_records status=held, lastError): releasing without fixing the cause only spends their \
attempts again."
    )]
    async fn delivery_release_records(&self, Parameters(i): Parameters<ReleaseInput>) -> String {
        let by_record = i.flow_id.is_some() || i.delivery_key.is_some();
        match (by_record, i.pipeline_id.as_deref()) {
            (true, None) => {
                let (flow_id, key) = match self.record_ref(i.flow_id.as_deref(), i.delivery_key.as_deref()) {
                    Ok(reference) => reference,
                    Err(refused) => return refused,
                };
                let links = self.on_record(&flow_id, &key);
                self.act(&format!("/api/v1/delivery/records/{flow_id}/{key}/release"), json!({}), links).await
            }
            (false, Some(pipeline)) => {
                let pipeline = match guid("pipelineId", pipeline, PIPELINE_HINT) {
                    Ok(pipeline) => pipeline,
                    Err(refused) => return refused,
                };
                let keys = match checked_keys(i.keys) {
                    Ok(keys) => keys,
                    Err(refused) => return refused,
                };
                let body = match (keys.is_empty(), i.all_held.unwrap_or(false)) {
                    (false, false) => json!({ "keys": keys }),
                    (true, true) => json!({}),
                    (false, true) => return refuse("Give keys or allHeld, not both."),
                    (true, false) => {
                        return refuse(
                            "Name the delivery keys to release, or say allHeld: true to release every held record of \
                             the interface.",
                        )
                    }
                };
                let (interface, partition) = (text(i.interface), text(i.partition));
                let links = self.on_flow(&pipeline, interface.as_deref(), partition.as_deref());
                let path = Query::new()
                    .text("interface", interface)
                    .text("partition", partition)
                    .onto(&format!("/api/v1/delivery/flows/{pipeline}/release"));
                self.act(&path, body, links).await
            }
            _ => refuse("Give either one record (flowId and deliveryKey) or a flow (pipelineId, with keys or allHeld)."),
        }
    }

    #[tool(
        description = "Send one record to OSDU again (operator action), whether or not it changed. The record is marked \
for redelivery in the ledger under your name and, unless run=false, a deliver run is queued now. THIS WRITES A NEW \
VERSION TO OSDU: do it only when asked, for a record whose copy in OSDU is not what it should be (drift, a record \
removed by hand, a payload that went missing). A record that merely changed at the source needs no redelivery; its \
flow's next run delivers it. Returns how many records were marked and the run id: poll get_run, then delivery_record \
for the new attempt."
    )]
    async fn delivery_redeliver_record(&self, Parameters(i): Parameters<RedeliverInput>) -> String {
        let (flow_id, key) = match self.record_ref(Some(&i.flow_id), Some(&i.delivery_key)) {
            Ok(reference) => reference,
            Err(refused) => return refused,
        };
        let body = json!({ "scope": text(i.scope), "run": i.run.unwrap_or(true), "pool": text(i.pool) });
        let links = self.on_record(&flow_id, &key);
        self.act(&format!("/api/v1/delivery/records/{flow_id}/{key}/redeliver"), body, links).await
    }

    #[tool(
        description = "Approve or reject pending cache changes (operator action): the changes a refresh found in a cached \
type declared `onChange: approve`, which wait until someone decides. Approving releases the delivered records built \
from the old value, so each flow's next run redelivers them; rejecting leaves OSDU as it is. Takes tagIds from \
delivery_cache_changes(status=\"pending\"). APPROVING CAUSES REDELIVERIES, possibly many: read each change's \
affectedRecords and waitingFlows first, and decide only what you were asked to decide. The decision is recorded with \
your name."
    )]
    async fn delivery_decide_cache_changes(&self, Parameters(i): Parameters<DecideCacheChangesInput>) -> String {
        if i.tag_ids.is_empty() {
            return refuse("Name at least one tag to decide: the tagId of each change in delivery_cache_changes.");
        }
        if i.tag_ids.len() > 1000 {
            return refuse("At most 1000 changes can be decided in one call.");
        }
        if i.tag_ids.iter().any(|id| *id <= 0) {
            return refuse("A tagId is the positive number a listed change carries.");
        }
        // The changes decided are read on the cache's Deliveries tab, in the partition the title bar names: a decision
        // may span partitions, and the tags it names do not say which.
        let mut links = Map::new();
        links.insert("changes".to_string(), json!(self.ctx.links().route("/delivery/cache?tab=deliveries")));
        self.act("/api/v1/delivery/cache/tags/decide", json!({ "tagIds": i.tag_ids, "approve": i.approve }), links).await
    }
}

/// What a request names its records by.
#[derive(Debug)]
enum Selection {
    Keys(Vec<String>),
    Filter(Value),
    Everything,
}

/// The delivery keys a request names: each checked, each once, at most [`MAX_KEYS`].
fn checked_keys(keys: Option<Vec<String>>) -> Result<Vec<String>, String> {
    let keys = keys.unwrap_or_default();
    if keys.len() > MAX_KEYS {
        return Err(refuse(format!(
            "At most {MAX_KEYS} delivery keys can be named in one call; {} were given.",
            keys.len()
        )));
    }
    let mut checked = Vec::with_capacity(keys.len());
    for key in &keys {
        let key = guid("keys", key, "A delivery key is the `deliveryKey` of a record.")?;
        if !checked.contains(&key) {
            checked.push(key);
        }
    }
    Ok(checked)
}

/// The listing filter as the control plane's request carries it, with its ids checked.
fn filter_body(filter: RecordFilter) -> Result<Value, String> {
    let hint = "A submission id comes from delivery_submissions, a run id from list_runs.";
    Ok(json!({
        "status": text(filter.status),
        "search": text(filter.search),
        "mode": text(filter.mode),
        "submissionId": optional_guid("filter.submissionId", filter.submission_id.as_deref(), hint)?,
        "deliveredBy": optional_guid("filter.deliveredBy", filter.delivered_by.as_deref(), hint)?,
        "runId": optional_guid("filter.runId", filter.run_id.as_deref(), hint)?,
        "drifted": filter.drifted.unwrap_or(false),
    }))
}

/// The records a request names: by key, by filter, or neither. Naming both is refused, as the control plane refuses it.
fn selection(keys: Option<Vec<String>>, filter: Option<RecordFilter>) -> Result<Selection, String> {
    let keys = checked_keys(keys)?;
    match (keys.is_empty(), filter) {
        (false, Some(_)) => Err(refuse("Name the records either by keys or by filter, not both.")),
        (false, None) => Ok(Selection::Keys(keys)),
        (true, Some(filter)) => Ok(Selection::Filter(filter_body(filter)?)),
        (true, None) => Ok(Selection::Everything),
    }
}

impl DeliveryTools {
    /// The two ids of a record, checked.
    fn record_ref(&self, flow_id: Option<&str>, key: Option<&str>) -> Result<(String, String), String> {
        let (Some(flow_id), Some(key)) = (flow_id, key) else {
            return Err(refuse(format!("A record needs both flowId and deliveryKey. {RECORD_HINT}")));
        };
        Ok((guid("flowId", flow_id, RECORD_HINT)?, guid("deliveryKey", key, RECORD_HINT)?))
    }

    /// An action's answer, with what it acted on linked beside what the rules linked on it (the run it queued).
    async fn act(&self, path: &str, body: Value, links: Map<String, Value>) -> String {
        done(self.ctx.send(path, body).await.map(|mut answer| {
            put_links(&mut answer, links);
            json_str(&answer)
        }))
    }

    /// The record an action acted on: its page.
    fn on_record(&self, flow_id: &str, key: &str) -> Map<String, Value> {
        let mut links = Map::new();
        links.insert("record".to_string(), json!(record_page(self.ctx.links(), flow_id, key)));
        links
    }

    /// The flow an action acted on, in the interface and partition it named: its page and its records.
    fn on_flow(&self, pipeline: &str, interface: Option<&str>, partition: Option<&str>) -> Map<String, Value> {
        let mut links = Map::new();
        links.insert("flow".to_string(), json!(flow_page(self.ctx.links(), pipeline, interface, partition, &[])));
        links.insert(
            "records".to_string(),
            json!(flow_page(self.ctx.links(), pipeline, interface, partition, &[("tab", "records")])),
        );
        links
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const KEY_A: &str = "5f0f8c1e-58a5-4d6f-9d1e-0a4b6f1c2d3e";
    const KEY_B: &str = "6a1b2c3d-0000-4000-8000-0123456789ab";

    fn filter(status: &str) -> RecordFilter {
        RecordFilter {
            status: Some(status.to_string()),
            search: None,
            mode: None,
            submission_id: None,
            delivered_by: None,
            run_id: None,
            drifted: None,
        }
    }

    #[test]
    fn keys_are_checked_and_named_once() {
        let keys = checked_keys(Some(vec![KEY_A.to_string(), KEY_A.to_uppercase(), KEY_B.to_string()])).unwrap();
        assert_eq!(keys, [KEY_A, KEY_B]);

        assert!(checked_keys(Some(vec!["wellbore-1".to_string()])).unwrap_err().contains("is not a GUID"));
        let many: Vec<String> = (0..=MAX_KEYS).map(|n| format!("{n:08x}-0000-4000-8000-000000000000")).collect();
        assert!(checked_keys(Some(many)).unwrap_err().contains("At most 1000 delivery keys"));
        assert!(checked_keys(None).unwrap().is_empty());
    }

    #[test]
    fn a_filter_carries_the_listing_filter_with_its_ids_checked() {
        let body = filter_body(filter("failed")).unwrap();
        assert_eq!(body["status"], json!("failed"));
        assert_eq!(body["drifted"], json!(false));
        assert!(body["submissionId"].is_null());

        let mut bad = filter("delivered");
        bad.delivered_by = Some("last night".to_string());
        let refused = filter_body(bad).unwrap_err();
        assert!(refused.contains("filter.deliveredBy 'last night' is not a GUID"), "{refused}");
    }

    #[test]
    fn records_are_named_one_way() {
        assert!(matches!(selection(None, None).unwrap(), Selection::Everything));
        assert!(matches!(selection(Some(vec![]), None).unwrap(), Selection::Everything));
        assert!(matches!(selection(Some(vec![KEY_A.to_string()]), None).unwrap(), Selection::Keys(_)));
        assert!(matches!(selection(None, Some(filter("held"))).unwrap(), Selection::Filter(_)));
        let both = selection(Some(vec![KEY_A.to_string()]), Some(filter("held"))).unwrap_err();
        assert!(both.contains("either by keys or by filter"), "{both}");
    }
}
