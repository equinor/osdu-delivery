//! The GUI routes of the rows the delivery endpoints return.
//!
//! SQLFlow's link decorator recognises its own rows by the identity fields they carry. The delivery module's rows
//! carry identities of their own, and several of them carry SQLFlow's too (a mapping has an id, a repo, a name and a
//! kind, exactly as a flow does), so this rule claims them first: a record opens its record page, a submission its
//! own, an assertion run its report, a mapping the mapping builder, and so on. A claimed row still gets the links to
//! what it references (its run, its flow) from SQLFlow's rules.
//!
//! An OSDU id a row names, whatever the row is, opens in the explorer (`osdu`, `osduParent`, `osduOccupied`), so an
//! answer can make the id itself the link to the record as OSDU holds it.
//!
//! The routes are the delivery GUI module's (`osdu/gui/src/module.tsx`), with the query parameters its pages read.

use serde_json::{Map, Value};
use sqlflow_mcp::{encode, GuiLinks};

/// A non-empty string field, or None.
fn text<'a>(row: &'a Map<String, Value>, name: &str) -> Option<&'a str> {
    row.get(name).and_then(Value::as_str).filter(|value| !value.is_empty())
}

/// A positive whole-number field, or None.
fn number(row: &Map<String, Value>, name: &str) -> Option<i64> {
    row.get(name).and_then(Value::as_i64).filter(|value| *value > 0)
}

pub(crate) fn record_page(links: &GuiLinks, flow_id: &str, key: &str) -> String {
    links.route(&format!("/delivery/records/{}/{}", encode(flow_id), encode(key)))
}

fn submission_page(links: &GuiLinks, id: &str) -> String {
    links.route(&format!("/delivery/submissions/{}", encode(id)))
}

fn cache_page(links: &GuiLinks, partition: &str) -> String {
    links.route(&format!("/delivery/cache?partition={}", encode(partition)))
}

/// Whether a value is an OSDU record id as storage's contract spells one (`osdu/specs/core/storage/openapi.yaml`):
/// `{data-partition-id}:{object-type}:{id}`, the first two parts of letters, digits, `_`, `-` and `.`, the last of
/// those, `:` and `%`. A lookup table's key, a GUID or an object key is not one.
fn is_osdu_id(value: &str) -> bool {
    let part = |part: &str, more: &[u8]| {
        !part.is_empty()
            && part
                .bytes()
                .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'_' | b'-' | b'.') || more.contains(&byte))
    };
    let mut parts = value.splitn(3, ':');
    match (parts.next(), parts.next(), parts.next()) {
        (Some(partition), Some(kind), Some(rest)) => part(partition, b"") && part(kind, b"") && part(rest, b":%"),
        _ => false,
    }
}

/// An OSDU id field of a row, when it holds one.
fn osdu_id<'a>(row: &'a Map<String, Value>, name: &str) -> Option<&'a str> {
    text(row, name).filter(|value| is_osdu_id(value))
}

/// Whether a value is an OSDU kind as storage's contract spells one (`osdu/specs/core/storage/openapi.yaml`):
/// `{authority}:{source}:{entity-type}:{major}.{minor}.{patch}`. An object's kind (`Table`), a flow's (`delivery`) or an
/// activity's (`redeliver`) is not one.
fn is_osdu_kind(value: &str) -> bool {
    let word = |part: &str| {
        !part.is_empty() && part.bytes().all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'_' | b'-' | b'.'))
    };
    let number = |part: &str| !part.is_empty() && part.bytes().all(|byte| byte.is_ascii_digit());
    match value.split(':').collect::<Vec<_>>().as_slice() {
        [authority, source, entity, version] => {
            word(authority)
                && word(source)
                && word(entity)
                && matches!(version.split('.').collect::<Vec<_>>().as_slice(), [major, minor, patch]
                    if number(major) && number(minor) && number(patch))
        }
        _ => false,
    }
}

/// Whether a value is a GUID other than the all-zero one a row added up over several ledgers carries for its flow id.
fn is_ledger_id(value: &str) -> bool {
    value.len() == 36
        && value.bytes().enumerate().all(|(index, byte)| match index {
            8 | 13 | 18 | 23 => byte == b'-',
            _ => byte.is_ascii_hexdigit(),
        })
        && value.bytes().any(|byte| byte.is_ascii_hexdigit() && byte != b'0')
}

/// A ledger's flow id field of a row, when it names one ledger.
fn ledger_id<'a>(row: &'a Map<String, Value>, name: &str) -> Option<&'a str> {
    text(row, name).filter(|value| is_ledger_id(value))
}

/// Whether a free-text address is a web address, the only kind an answer can open.
fn is_web_url(value: &str) -> bool {
    let lowered = value.trim_start().to_ascii_lowercase();
    lowered.starts_with("http://") || lowered.starts_with("https://")
}

/// The explorer opened on one OSDU record, in the partition the row names, else the one its id starts with (a record's
/// id begins with its data partition). The explorer makes a linked partition the title bar's, so the record is read in
/// the partition that holds it whichever one the reader last worked in (osdu/docs/reference/concepts/explorer.md).
fn explorer_page(links: &GuiLinks, id: &str, partition: Option<&str>) -> String {
    let partition = partition.unwrap_or_else(|| id.split(':').next().unwrap_or_default());
    links.route(&format!("/delivery/explorer?id={}&partition={}", encode(id), encode(partition)))
}

/// The Templates page on the repository's file holding one template version of an OSDU kind, or a notice that none does.
pub(crate) fn template_page(links: &GuiLinks, kind: &str, version: &str) -> String {
    links.route(&format!("/delivery/templates?kind={}&version={}", encode(kind), encode(version)))
}

/// The explorer opened on the records of one OSDU kind, in the partition the row names, else the title bar's.
pub(crate) fn explorer_kind(links: &GuiLinks, kind: &str, partition: Option<&str>) -> String {
    match partition {
        Some(partition) => links.route(&format!("/delivery/explorer?kind={}&partition={}", encode(kind), encode(partition))),
        None => links.route(&format!("/delivery/explorer?kind={}", encode(kind))),
    }
}

/// A flow's page on one of its ledgers, as the GUI links one (`flowLedgerRoute`): the tab and what it narrows to, then
/// the interface and the partition. The page takes a partition the flow serves as the title bar's and ignores another.
pub(crate) fn flow_page(
    links: &GuiLinks,
    pipeline: &str,
    interface: Option<&str>,
    partition: Option<&str>,
    narrowed: &[(&str, &str)],
) -> String {
    let mut query: Vec<String> = narrowed.iter().map(|(name, value)| format!("{name}={}", encode(value))).collect();
    if let Some(interface) = interface {
        query.push(format!("interface={}", encode(interface)));
    }
    if let Some(partition) = partition {
        query.push(format!("partition={}", encode(partition)));
    }
    if query.is_empty() {
        links.pipeline(pipeline)
    } else {
        links.route(&format!("/pipelines/{}?{}", encode(pipeline), query.join("&")))
    }
}

/// The audit trail narrowed to one ledger, in the partition that keeps it.
fn activity_page(links: &GuiLinks, flow_id: &str, partition: Option<&str>) -> String {
    match partition {
        Some(partition) => links.route(&format!("/delivery/activity?flow={}&partition={}", encode(flow_id), encode(partition))),
        None => links.route(&format!("/delivery/activity?flow={}", encode(flow_id))),
    }
}

/// The records an ingestion file brought, found by its name as the record lookup finds a file (a prefix match).
fn records_from_file(links: &GuiLinks, file: &str, partition: Option<&str>) -> String {
    match partition {
        Some(partition) => links.route(&format!("/delivery/records?q={}&partition={}", encode(file), encode(partition))),
        None => links.route(&format!("/delivery/records?q={}", encode(file))),
    }
}

/// A partition's cache page on one of its tabs (`history`, `deliveries`).
fn cache_tab(links: &GuiLinks, partition: &str, tab: &str) -> String {
    links.route(&format!("/delivery/cache?partition={}&tab={}", encode(partition), encode(tab)))
}

/// The partition a row names its flow's ledger in. An audit trail entry says it apart (`namedPartition`, null for a
/// flow whose partition is its header's); every other row says it as `partition`.
fn ledger_partition(row: &Map<String, Value>) -> Option<&str> {
    if row.contains_key("namedPartition") {
        text(row, "namedPartition")
    } else {
        text(row, "partition")
    }
}

/// The template of an OSDU kind a row names, on the Templates page: the repository's file itself when the row says
/// which (`repoId` and `templatePath`), else the file holding the template version the row names. A schema listing's
/// `version` is the schema's own, not a template version, so only a bundled schema file's (it says its `origin`) is.
fn template_link(links: &GuiLinks, row: &Map<String, Value>, put: &mut impl FnMut(&str, String)) {
    if let (Some(repo), Some(path)) = (text(row, "repoId"), text(row, "templatePath")) {
        put("template", links.route(&format!("/delivery/templates?repoId={}&path={}", encode(repo), encode(path))));
        return;
    }
    let Some(kind) = text(row, "kind").filter(|kind| is_osdu_kind(kind)) else {
        return;
    };
    let bundled = row.contains_key("origin") && row.contains_key("release");
    let version = text(row, "templateVersion").or_else(|| if bundled { text(row, "version") } else { None });
    if let Some(version) = version {
        put("template", template_page(links, kind, version));
    }
}

/// The explorer links of the OSDU ids a row names, each under what it is to the row: `osdu` the record the row is or
/// landed as (a record's `targetId`, a cached record, an id a document refers to), `osduParent` the record it reads as
/// its parent, `osduOccupied` the record OSDU already holds at the id its key makes. Asked of every row, whichever rule
/// claims it, since an id is linked the same way wherever it appears.
fn osdu_links(links: &GuiLinks, row: &Map<String, Value>, put: &mut impl FnMut(&str, String)) {
    let partition = text(row, "partition");
    for (field, name) in [("targetId", "osdu"), ("parentId", "osduParent"), ("occupiedTargetId", "osduOccupied")] {
        if let Some(id) = osdu_id(row, field) {
            put(name, explorer_page(links, id, partition));
        }
    }

    // A cached record, or a change found in one: its partition is the cache's (`scope`). A lookup table, dictionary or
    // dimension caches rows by a key of its own, which is not an OSDU id, so only an id of the scope's partition is linked.
    if let (Some(scope), true) = (text(row, "scope"), row.contains_key("typeName")) {
        if let Some(id) = osdu_id(row, "recordId").or_else(|| osdu_id(row, "itemId")) {
            if id.split(':').next() == Some(scope) {
                put("osdu", explorer_page(links, id, Some(scope)));
            }
        }
    }

    // An id the pending document of a record refers to, and the property that holds it.
    if let (Some(id), true, 2) = (osdu_id(row, "id"), row.contains_key("property"), row.len()) {
        put("osdu", explorer_page(links, id, None));
    }

    // An OSDU kind (a mapping's, a template's, an interface's, a dimension's, a test's, a retrieval's when it reads one)
    // opens the explorer on the records OSDU holds of it.
    if let Some(kind) = text(row, "kind").or_else(|| text(row, "kinds")).filter(|kind| is_osdu_kind(kind)) {
        put("osduKind", explorer_kind(links, kind, partition));
    }
    template_link(links, row, put);

    // A schema's page where its release publishes it, outside the GUI, under the names SQLFlow gives such an address.
    for (field, name) in [("webUrl", "url"), ("fromWebUrl", "fromUrl"), ("toWebUrl", "toUrl")] {
        if let Some(address) = text(row, field).filter(|address| is_web_url(address)) {
            put(name, address.to_string());
        }
    }
}

/// The links of one row of a delivery payload, or None when the row is not one of the module's.
pub fn delivery_links(links: &GuiLinks, row: &Map<String, Value>) -> Option<Map<String, Value>> {
    let mut found = Map::new();
    let mut put = |name: &str, link: String| {
        found.entry(name.to_string()).or_insert_with(|| Value::String(link));
    };

    osdu_links(links, row, &mut put);

    // The flow a row belongs to, on the interface and partition the row names, as the GUI's own flow links open it.
    // SQLFlow's `flow` is the same page on the first interface and the title bar's partition, so this one comes first.
    if let Some(pipeline) = text(row, "pipelineId") {
        let (interface, partition) = (text(row, "interface"), ledger_partition(row));
        if interface.is_some() || partition.is_some() {
            put("flow", flow_page(links, pipeline, interface, partition, &[]));
        }
    }

    // ---- Rows that are something of the module's, each by the field that makes it so ------------------------------

    // An audit trail entry. It has no page of its own; the trail narrowed to its ledger is where it is read, and what it
    // was done to (the record, the submission) is linked beside it.
    if number(row, "activityId").is_some() && row.contains_key("actor") {
        let page = match ledger_id(row, "flowId") {
            Some(flow_id) => activity_page(links, flow_id, text(row, "partition")),
            None => links.route("/delivery/activity"),
        };
        put("page", page);
        if let (Some(flow_id), Some(key)) = (text(row, "flowId"), text(row, "deliveryKey")) {
            put("record", record_page(links, flow_id, key));
        }
        if let Some(submission) = text(row, "submissionId") {
            put("submission", submission_page(links, submission));
        }
        return Some(found);
    }

    // A delivery try: part of a record's history, with no page of its own. It names its submission and its run.
    if number(row, "attemptId").is_some() {
        if let Some(submission) = text(row, "submissionId") {
            put("submission", submission_page(links, submission));
        }
        return if found.is_empty() { None } else { Some(found) };
    }

    // A record: the ledger's flow id and the delivery key together are what its page is addressed by. The ingestion file
    // it came from opens the records that file brought.
    if let (Some(flow_id), Some(key)) = (text(row, "flowId"), text(row, "deliveryKey")) {
        put("page", record_page(links, flow_id, key));
        if let Some(submission) = text(row, "lastSubmissionId") {
            put("lastSubmission", submission_page(links, submission));
        }
        if let Some(file) = text(row, "sourceFileName") {
            put("sourceFile", records_from_file(links, file, text(row, "partition")));
        }
        return Some(found);
    }

    // A submission as its page reads it, with the flow, interface and partition that planned it: the flow's records it
    // planned and the ones it delivered, as the submission page links them.
    let planned = row.get("submission").and_then(|submission| submission.get("submissionId")).and_then(Value::as_str);
    if let (Some(pipeline), Some(submission), true) = (text(row, "pipelineId"), planned, row.contains_key("runIds")) {
        let (interface, partition) = (text(row, "interface"), text(row, "partition"));
        put("records", flow_page(links, pipeline, interface, partition, &[("tab", "records"), ("submission", submission)]));
        put("delivered", flow_page(links, pipeline, interface, partition, &[("tab", "records"), ("delivered", submission)]));
    }

    // A flow's figures, whole or one interface's: its records, its submissions and its audit trail. A sum over several
    // ledgers carries no ledger's flow id, so it has no trail of its own.
    if let (Some(pipeline), true, true) = (text(row, "pipelineId"), row.contains_key("submissions"), row.contains_key("delivered")) {
        let (interface, partition) = (text(row, "interface"), text(row, "partition"));
        put("records", flow_page(links, pipeline, interface, partition, &[("tab", "records")]));
        put("submissions", flow_page(links, pipeline, interface, partition, &[("tab", "submissions")]));
        if let Some(flow_id) = ledger_id(row, "flowId") {
            put("activity", activity_page(links, flow_id, partition));
        }
    }

    // One interface of a flow (its ledger, route and mapping): its audit trail. Its figures are linked as the flow's are.
    if let (Some(flow_id), true, true) = (ledger_id(row, "flowId"), row.contains_key("ledger"), row.contains_key("route")) {
        put("activity", activity_page(links, flow_id, text(row, "partition")));
    }

    // A flow that renders a mapping, by the ledger it keeps: its records and its audit trail.
    if let (Some(flow_id), Some(pipeline)) = (ledger_id(row, "ledgerFlowId"), text(row, "pipelineId")) {
        let (interface, partition) = (text(row, "interface"), text(row, "partition"));
        put("records", flow_page(links, pipeline, interface, partition, &[("tab", "records")]));
        put("activity", activity_page(links, flow_id, partition));
    }

    // A submission (it carries when the ledger received it), or a row that only names one (a work batch).
    if let Some(submission) = text(row, "submissionId") {
        if row.contains_key("receivedUtc") {
            put("page", submission_page(links, submission));
            return Some(found);
        }
        put("submission", submission_page(links, submission));
    }

    // An assertion run opens its report.
    if let Some(run) = number(row, "assertionRunId") {
        put("page", links.route(&format!("/delivery/assertions/runs/{run}")));
        return Some(found);
    }

    // A dimension opens the dimensions page on it, and one of its builds the same page on its builds.
    if let Some(dimension) = number(row, "dimensionId") {
        let page = if number(row, "buildId").is_some() {
            format!("/delivery/dimensions?d={dimension}&view=builds")
        } else {
            format!("/delivery/dimensions?d={dimension}")
        };
        put("page", links.route(&page));
        return Some(found);
    }

    // A dimension flow on a board (its dimensions, and whether it builds the board's partition) opens its Dimensions tab;
    // an assertion flow on a board (its tests, and what fails its run) its tests.
    if let Some(pipeline) = text(row, "pipelineId") {
        let tab = if row.contains_key("dimensions") && row.contains_key("buildsPartition") {
            Some("dimensions")
        } else if row.contains_key("tests") && row.contains_key("failRunOn") {
            Some("tests")
        } else {
            None
        };
        if let Some(tab) = tab {
            put("page", flow_page(links, pipeline, None, text(row, "partition"), &[("tab", tab)]));
            return Some(found);
        }
    }

    // A mapping as the sync found it. It carries an id, a repo, a name and a kind as a flow does, so it has to be
    // claimed here or it would be linked as a pipeline it is not.
    if let (Some(id), true, true) = (text(row, "id"), row.contains_key("reference"), row.contains_key("contentHash")) {
        put("page", links.route(&format!("/delivery/mappings/build?mappingId={}", encode(id))));
        if let Some(repo) = text(row, "repoId") {
            put("repo", links.repo(repo));
        }
        return Some(found);
    }

    // A file of a repository's template folder, as its sync recorded it: the Templates page opens it, and the repository
    // that holds it is linked beside. Claimed here, or its repo id would be all SQLFlow's rules see of it.
    if let (Some(repo), Some(path), true, true) = (text(row, "repoId"), text(row, "path"), row.contains_key("folder"), row.contains_key("seenUtc")) {
        put(
            "page",
            links.route(&format!("/delivery/templates?repoId={}&path={}", encode(repo), encode(path))),
        );
        put("repo", links.repo(repo));
        return Some(found);
    }

    // A partition of the registry (it says whether it is registered and the default): its cache, and what OSDU holds in
    // it in the explorer.
    if let (Some(name), true, true) = (text(row, "name"), row.contains_key("registered"), row.contains_key("isDefault")) {
        put("page", links.route("/delivery/partitions"));
        put("cache", cache_page(links, name));
        put("explorer", links.route(&format!("/delivery/explorer?partition={}", encode(name))));
        return Some(found);
    }

    // A cache flow (its file, its schedules, the types it fills) opens the cache it fills. It carries a repo as a
    // repository row does, so it has to be claimed here or it would be linked as the repository it is not.
    if let (Some(name), true, true) = (text(row, "name"), row.contains_key("relativePath"), row.contains_key("schedules")) {
        put("page", links.route(&format!("/delivery/cache?flow={}", encode(name))));
        if let Some(repo) = text(row, "repoId") {
            put("repo", links.repo(repo));
        }
        return Some(found);
    }

    // A schedule a cache flow runs on: its runs, as SQLFlow links a schedule.
    if let (Some(id), true, true) = (text(row, "id"), row.contains_key("cron"), row.contains_key("chained")) {
        put("page", links.schedule_runs(id));
        return Some(found);
    }

    // A partition's cache and what is in it, each naming the partition as `scope`: a change found in it opens the
    // changes, a version the history, the cache itself its records, and a cached type or record the records of its type.
    if let Some(scope) = text(row, "scope") {
        let page = if number(row, "tagId").is_some() {
            Some(cache_tab(links, scope, "deliveries"))
        } else if row.contains_key("sequence") && row.contains_key("version") {
            Some(cache_tab(links, scope, "history"))
        } else if row.contains_key("versions") {
            Some(cache_page(links, scope))
        } else if let Some(type_name) = text(row, "typeName") {
            Some(links.route(&format!("/delivery/cache?partition={}&type={}", encode(scope), encode(type_name))))
        } else if row.contains_key("version") {
            Some(cache_page(links, scope))
        } else {
            None
        };
        if let Some(page) = page {
            put("page", page);
            return Some(found);
        }
    }

    if found.is_empty() {
        None
    } else {
        Some(found)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    const FLOW: &str = "5f0f8c1e-58a5-4d6f-9d1e-0a4b6f1c2d3e";
    const KEY: &str = "6a1b2c3d-0000-4000-8000-0123456789ab";
    const SUBMISSION: &str = "11111111-2222-4333-8444-555555555555";

    fn decorated(value: Value) -> Value {
        let links = GuiLinks::new("https://delivery.example.com").with_rules(vec![std::sync::Arc::new(delivery_links)]);
        let mut value = value;
        links.decorate(&mut value);
        value
    }

    #[test]
    fn a_record_opens_its_page_and_keeps_its_flow() {
        let row = decorated(json!({
            "deliveryKey": KEY, "flowId": FLOW, "pipelineId": "pipe-1", "flowName": "wells",
            "status": "delivered", "lastSubmissionId": SUBMISSION
        }));
        assert_eq!(
            row["links"]["page"],
            json!(format!("https://delivery.example.com/delivery/records/{FLOW}/{KEY}"))
        );
        assert_eq!(
            row["links"]["lastSubmission"],
            json!(format!("https://delivery.example.com/delivery/submissions/{SUBMISSION}"))
        );
        // The flow that holds it is a reference, under its own name.
        assert_eq!(row["links"]["flow"], json!("https://delivery.example.com/pipelines/pipe-1"));
    }

    #[test]
    fn a_submission_opens_its_page_and_a_batch_only_names_it() {
        let submission = decorated(json!({
            "submissionId": SUBMISSION, "flowId": FLOW, "receivedUtc": "2026-10-01T00:00:00Z", "runId": "run-1"
        }));
        assert_eq!(
            submission["links"]["page"],
            json!(format!("https://delivery.example.com/delivery/submissions/{SUBMISSION}"))
        );
        assert_eq!(submission["links"]["run"], json!("https://delivery.example.com/runs/run-1"));

        let batch = decorated(json!({ "submissionId": SUBMISSION, "index": 3, "location": "work/3.jsonl", "runId": "run-2" }));
        assert_eq!(
            batch["links"]["submission"],
            json!(format!("https://delivery.example.com/delivery/submissions/{SUBMISSION}"))
        );
        // A batch has no page of its own, so its run takes it, as for any row that only references things.
        assert_eq!(batch["links"]["page"], json!("https://delivery.example.com/runs/run-2"));
    }

    #[test]
    fn an_activity_links_the_trail_and_what_it_was_done_to() {
        let row = decorated(json!({
            "activityId": 7, "flowId": FLOW, "kind": "redeliver", "actor": "user:alice",
            "deliveryKey": KEY, "submissionId": SUBMISSION, "runId": "run-3"
        }));
        // The trail narrowed to the activity's ledger, in the partition that keeps it when the row names one.
        assert_eq!(row["links"]["page"], json!(format!("https://delivery.example.com/delivery/activity?flow={FLOW}")));
        assert_eq!(row["links"]["record"], json!(format!("https://delivery.example.com/delivery/records/{FLOW}/{KEY}")));
        let kept = decorated(json!({ "activityId": 8, "flowId": FLOW, "kind": "deliver", "actor": "schedule:hourly", "partition": "test" }));
        assert_eq!(
            kept["links"]["page"],
            json!(format!("https://delivery.example.com/delivery/activity?flow={FLOW}&partition=test"))
        );
        assert_eq!(
            row["links"]["submission"],
            json!(format!("https://delivery.example.com/delivery/submissions/{SUBMISSION}"))
        );
        assert_eq!(row["links"]["run"], json!("https://delivery.example.com/runs/run-3"));
    }

    #[test]
    fn an_attempt_names_its_submission_and_takes_no_record_page() {
        // An attempt carries a delivery key and no flow id, so it cannot address a record page.
        let row = decorated(json!({ "attemptId": 12, "deliveryKey": KEY, "submissionId": SUBMISSION, "runId": "run-4", "outcome": "delivered" }));
        assert_eq!(
            row["links"]["submission"],
            json!(format!("https://delivery.example.com/delivery/submissions/{SUBMISSION}"))
        );
        assert_eq!(row["links"]["page"], json!("https://delivery.example.com/runs/run-4"));
    }

    #[test]
    fn a_mapping_is_not_taken_for_a_flow() {
        let row = decorated(json!({
            "id": "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee", "repoId": "repo-1", "reference": "wellbore@1.0.0",
            "name": "wellbore", "version": "1.0.0", "kind": "osdu:wks:master-data--Wellbore:1.5.1",
            "relativePath": "mappings/wellbore.yaml", "contentHash": "abc", "status": "valid"
        }));
        assert_eq!(
            row["links"]["page"],
            json!("https://delivery.example.com/delivery/mappings/build?mappingId=aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee")
        );
        assert_eq!(row["links"]["repo"], json!("https://delivery.example.com/repos/repo-1"));
        assert!(row["links"]["lineage"].is_null(), "a mapping is not a pipeline, so it has no flow lineage");
    }

    #[test]
    fn the_modules_other_rows_open_their_pages() {
        let run = decorated(json!({ "assertionRunId": 42, "flowId": FLOW, "status": "failed", "runId": "run-5" }));
        assert_eq!(run["links"]["page"], json!("https://delivery.example.com/delivery/assertions/runs/42"));
        assert_eq!(run["links"]["run"], json!("https://delivery.example.com/runs/run-5"));

        let dimension = decorated(json!({ "dimensionId": 3, "name": "Operator", "kind": "osdu:wks:master-data--Wellbore:1.5.1" }));
        assert_eq!(dimension["links"]["page"], json!("https://delivery.example.com/delivery/dimensions?d=3"));

        let build = decorated(json!({ "buildId": 8, "dimensionId": 3, "status": "failed", "runId": "run-7" }));
        assert_eq!(build["links"]["page"], json!("https://delivery.example.com/delivery/dimensions?d=3&view=builds"));
        assert_eq!(build["links"]["run"], json!("https://delivery.example.com/runs/run-7"));

        let template = decorated(json!({
            "repoId": "repo-1", "repository": "welldb", "path": "welldb/templates/osdu_wks_master-data--Wellbore_1.5.1.json",
            "folder": "welldb/templates", "kind": "osdu:wks:master-data--Wellbore:1.5.1", "version": "2f8a99cb38d32480",
            "problem": null, "seenUtc": "2026-09-01T00:00:00Z"
        }));
        assert_eq!(
            template["links"]["page"],
            json!("https://delivery.example.com/delivery/templates?repoId=repo-1&path=welldb%2Ftemplates%2Fosdu_wks_master-data--Wellbore_1.5.1.json")
        );
        assert_eq!(template["links"]["repo"], json!("https://delivery.example.com/repos/repo-1"));

        let partition = decorated(json!({ "name": "dev", "registered": true, "isDefault": true, "items": 10 }));
        assert_eq!(partition["links"]["page"], json!("https://delivery.example.com/delivery/partitions"));
        assert_eq!(partition["links"]["cache"], json!("https://delivery.example.com/delivery/cache?partition=dev"));
        assert_eq!(partition["links"]["explorer"], json!("https://delivery.example.com/delivery/explorer?partition=dev"));

        // A cache opens on its records, a version on the history, a change on the changes, a cached type on its records.
        let cache = decorated(json!({ "scope": "dev", "flows": [], "types": [], "versions": 4 }));
        assert_eq!(cache["links"]["page"], json!("https://delivery.example.com/delivery/cache?partition=dev"));

        let version = decorated(json!({ "scope": "dev", "version": "v12", "sequence": 12, "current": true, "runId": "run-6" }));
        assert_eq!(version["links"]["page"], json!("https://delivery.example.com/delivery/cache?partition=dev&tab=history"));
        assert_eq!(version["links"]["run"], json!("https://delivery.example.com/runs/run-6"));

        let change = decorated(json!({ "tagId": 31, "scope": "dev", "typeName": "wellbore", "path": "data.FacilityName", "status": "pending" }));
        assert_eq!(change["links"]["page"], json!("https://delivery.example.com/delivery/cache?partition=dev&tab=deliveries"));

        let used = decorated(json!({ "scope": "dev", "typeName": "unit", "itemId": "m", "path": "data.Code", "kind": "lookup" }));
        assert_eq!(used["links"]["page"], json!("https://delivery.example.com/delivery/cache?partition=dev&type=unit"));
    }

    const PIPELINE: &str = "c0ffee00-1111-4222-8333-444455556666";
    const BASE: &str = "https://delivery.example.com";

    #[test]
    fn an_osdu_kind_is_one_as_storage_spells_it() {
        for kind in ["osdu:wks:master-data--Wellbore:1.5.1", "welldb:custom:work-product-component--WellLog:10.0.12"] {
            assert!(is_osdu_kind(kind), "{kind}");
        }
        for value in [
            "Table",
            "delivery",
            "redeliver",
            "osdu:wks:master-data--Wellbore",
            "osdu:wks:master-data--Wellbore:1.5",
            "osdu:wks:master-data--Wellbore:1.5.x",
            "dev:master-data--Wellbore:W-1",
            "osdu:wks:master-data--Wellbore:1.5.1,osdu:wks:master-data--Well:1.2.0",
        ] {
            assert!(!is_osdu_kind(value), "{value}");
        }
    }

    #[test]
    fn a_flow_opens_on_the_interface_and_partition_the_row_names() {
        let hit = decorated(json!({
            "deliveryKey": KEY, "flowId": FLOW, "pipelineId": PIPELINE, "flowName": "welldb-welllog-03-delivery",
            "interface": "logs", "partition": "test", "status": "delivered", "sourceFileName": "welllog 2026-10-23.csv"
        }));
        assert_eq!(hit["links"]["flow"], json!(format!("{BASE}/pipelines/{PIPELINE}?interface=logs&partition=test")));
        // The ingestion file it came from opens the records that file brought, in the record's partition.
        assert_eq!(
            hit["links"]["sourceFile"],
            json!(format!("{BASE}/delivery/records?q=welllog%202026-10-23.csv&partition=test"))
        );

        // An audit trail entry names its flow's partition apart: none for a flow whose partition is its header's.
        let activity = decorated(json!({
            "activityId": 9, "flowId": FLOW, "kind": "deliver", "actor": "schedule:hourly", "pipelineId": PIPELINE,
            "partition": "dev", "namedPartition": null, "interface": null
        }));
        assert_eq!(activity["links"]["flow"], json!(format!("{BASE}/pipelines/{PIPELINE}")));
        assert_eq!(activity["links"]["page"], json!(format!("{BASE}/delivery/activity?flow={FLOW}&partition=dev")));
    }

    #[test]
    fn a_flows_figures_open_its_records_its_submissions_and_its_trail() {
        let stats = decorated(json!({
            "pipelineId": PIPELINE, "flowName": "wells", "flowId": FLOW, "total": 10, "delivered": 9, "submissions": 3,
            "interface": "wells", "partition": "dev"
        }));
        assert_eq!(
            stats["links"]["records"],
            json!(format!("{BASE}/pipelines/{PIPELINE}?tab=records&interface=wells&partition=dev"))
        );
        assert_eq!(
            stats["links"]["submissions"],
            json!(format!("{BASE}/pipelines/{PIPELINE}?tab=submissions&interface=wells&partition=dev"))
        );
        assert_eq!(stats["links"]["activity"], json!(format!("{BASE}/delivery/activity?flow={FLOW}&partition=dev")));

        // Figures added up over several ledgers carry no ledger's flow id, so they have no trail of their own.
        let whole = decorated(json!({
            "pipelineId": PIPELINE, "flowName": "wells", "flowId": "00000000-0000-0000-0000-000000000000",
            "total": 10, "delivered": 9, "submissions": 3
        }));
        assert_eq!(whole["links"]["records"], json!(format!("{BASE}/pipelines/{PIPELINE}?tab=records")));
        assert!(whole["links"]["activity"].is_null(), "{whole}");

        let interface = decorated(json!({
            "interface": "logs", "flowId": FLOW, "ledger": "wells/logs", "route": "storage", "mapping": "welllog@1.0.0",
            "kind": "osdu:wks:work-product-component--WellLog:1.4.0", "recordObject": "welldb.silver.WellLog", "partition": "dev"
        }));
        assert_eq!(interface["links"]["activity"], json!(format!("{BASE}/delivery/activity?flow={FLOW}&partition=dev")));
        assert_eq!(
            interface["links"]["osduKind"],
            json!(format!("{BASE}/delivery/explorer?kind=osdu%3Awks%3Awork-product-component--WellLog%3A1.4.0&partition=dev"))
        );

        let renders = decorated(json!({
            "pipelineId": PIPELINE, "flow": "wells", "interface": null, "partition": "dev", "ledgerFlowId": FLOW,
            "recordObject": "welldb.silver.Wellbore", "route": "storage"
        }));
        assert_eq!(renders["links"]["records"], json!(format!("{BASE}/pipelines/{PIPELINE}?tab=records&partition=dev")));
        assert_eq!(renders["links"]["activity"], json!(format!("{BASE}/delivery/activity?flow={FLOW}&partition=dev")));
    }

    #[test]
    fn a_submission_opens_the_records_it_planned_and_delivered() {
        let detail = decorated(json!({
            "submission": { "submissionId": SUBMISSION, "flowId": FLOW, "receivedUtc": "2026-10-01T00:00:00Z" },
            "pipelineId": PIPELINE, "runIds": ["run-1"], "interface": "logs", "partition": "dev"
        }));
        assert_eq!(
            detail["links"]["records"],
            json!(format!("{BASE}/pipelines/{PIPELINE}?tab=records&submission={SUBMISSION}&interface=logs&partition=dev"))
        );
        assert_eq!(
            detail["links"]["delivered"],
            json!(format!("{BASE}/pipelines/{PIPELINE}?tab=records&delivered={SUBMISSION}&interface=logs&partition=dev"))
        );
        assert_eq!(detail["submission"]["links"]["page"], json!(format!("{BASE}/delivery/submissions/{SUBMISSION}")));
    }

    #[test]
    fn a_kind_opens_its_records_in_the_explorer_and_its_template() {
        let mapping = decorated(json!({
            "id": "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee", "repoId": "repo-1", "reference": "wellbore@1.0.0",
            "name": "wellbore", "version": "1.0.0", "kind": "osdu:wks:master-data--Wellbore:1.5.1",
            "relativePath": "mappings/wellbore.yaml", "contentHash": "abc", "status": "valid",
            "templateVersion": "2f8a99cb38d32480", "templatePath": "welldb/templates/wellbore.json"
        }));
        assert_eq!(
            mapping["links"]["osduKind"],
            json!(format!("{BASE}/delivery/explorer?kind=osdu%3Awks%3Amaster-data--Wellbore%3A1.5.1"))
        );
        // The repository's own file when the row names it, rather than whichever file holds the version.
        assert_eq!(
            mapping["links"]["template"],
            json!(format!("{BASE}/delivery/templates?repoId=repo-1&path=welldb%2Ftemplates%2Fwellbore.json"))
        );

        let side = decorated(json!({
            "kind": "osdu:wks:master-data--Wellbore:1.5.1", "path": "Generated/master-data/Wellbore.1.5.1.json",
            "webUrl": "https://community.example.org/data-definitions/Wellbore.1.5.1.json", "status": "PUBLISHED",
            "templateVersion": "2f8a99cb38d32480"
        }));
        assert_eq!(
            side["links"]["template"],
            json!(format!("{BASE}/delivery/templates?kind=osdu%3Awks%3Amaster-data--Wellbore%3A1.5.1&version=2f8a99cb38d32480"))
        );
        assert_eq!(side["links"]["url"], json!("https://community.example.org/data-definitions/Wellbore.1.5.1.json"));

        // A bundled schema file's version is the template version its file holds; a listing's is the schema's own.
        let file = decorated(json!({
            "kind": "osdu:wks:master-data--Wellbore:1.5.1", "version": "2f8a99cb38d32480", "release": { "name": "v0.27" },
            "path": "Generated/master-data/Wellbore.1.5.1.json", "origin": "v0.27", "schema": {}
        }));
        assert_eq!(
            file["links"]["template"],
            json!(format!("{BASE}/delivery/templates?kind=osdu%3Awks%3Amaster-data--Wellbore%3A1.5.1&version=2f8a99cb38d32480"))
        );
        let listed = decorated(json!({
            "kind": "osdu:wks:master-data--Wellbore:1.5.1", "entityType": "master-data--Wellbore", "version": "1.5.1",
            "status": "PUBLISHED", "path": "Generated/master-data/Wellbore.1.5.1.json", "webUrl": "ftp://not-a-web-address"
        }));
        assert!(listed["links"]["template"].is_null() && listed["links"]["url"].is_null(), "{listed}");
        assert!(listed["links"]["osduKind"].is_string());

        // A retrieval that reads one kind opens it; one that reads several names no single place.
        let one = decorated(json!({ "retrievalId": 4, "kinds": "osdu:wks:master-data--Well:1.2.0", "runId": "run-9" }));
        assert!(one["links"]["osduKind"].is_string(), "{one}");
        let several = decorated(json!({
            "retrievalId": 5, "kinds": "osdu:wks:master-data--Well:1.2.0,osdu:wks:master-data--Wellbore:1.5.1", "runId": "run-9"
        }));
        assert!(several["links"]["osduKind"].is_null(), "{several}");
    }

    #[test]
    fn a_cache_flow_opens_its_cache_and_its_schedules_their_runs() {
        let flow = decorated(json!({
            "name": "welldb-lookups-00-cache", "repoId": "repo-1", "repoName": "welldb", "relativePath": "flows/lookups.yaml",
            "pipelineId": PIPELINE, "schedules": [{ "id": "sched-1", "name": "hourly", "cron": "0 * * * *", "intervalSeconds": null, "chained": false }],
            "types": ["unit"]
        }));
        assert_eq!(flow["links"]["page"], json!(format!("{BASE}/delivery/cache?flow=welldb-lookups-00-cache")));
        assert_eq!(flow["links"]["repo"], json!(format!("{BASE}/repos/repo-1")));
        assert_eq!(flow["schedules"][0]["links"]["page"], json!(format!("{BASE}/runs?scheduleId=sched-1")));
    }

    #[test]
    fn a_dimension_flow_and_an_assertion_flow_open_their_tabs() {
        let dimensions = decorated(json!({
            "pipelineId": PIPELINE, "repoId": "repo-1", "name": "welldb-operators-dimension", "partition": "dev",
            "buildsPartition": true, "partitions": ["dev"], "dimensions": []
        }));
        assert_eq!(dimensions["links"]["page"], json!(format!("{BASE}/pipelines/{PIPELINE}?tab=dimensions&partition=dev")));

        let assertions = decorated(json!({
            "pipelineId": PIPELINE, "repoId": "repo-1", "name": "welldb-assertions", "partition": "dev",
            "failRunOn": "error", "tests": []
        }));
        assert_eq!(assertions["links"]["page"], json!(format!("{BASE}/pipelines/{PIPELINE}?tab=tests&partition=dev")));
    }

    fn explorer(id: &str, partition: &str) -> Value {
        json!(format!("https://delivery.example.com/delivery/explorer?id={}&partition={partition}", encode(id)))
    }

    #[test]
    fn an_osdu_id_is_one_as_storage_spells_it() {
        for id in [
            "dev:master-data--Wellbore:W-1",
            "dev:work-product-component--WellLog:6a1b2c3d-0000-4000-8000-0123456789ab",
            "test:reference-data--UnitOfMeasure:m%2Fs",
            "dev:dataset--File.Generic:abc:1",
            "my_partition.1:master-data--Well:x",
        ] {
            assert!(is_osdu_id(id), "{id}");
        }
        for value in [
            "",
            "dev",
            "dev:master-data--Wellbore",
            "dev::W-1",
            ":master-data--Wellbore:W-1",
            "dev:master-data--Wellbore:",
            "dev:master-data--Wellbore:W 1",
            "dev:master-data--Wellbore:Å-1",
            "dev:master/data:W-1",
            "dw.arc.wells",
            "5f0f8c1e-58a5-4d6f-9d1e-0a4b6f1c2d3e",
            "sch:db|schema",
        ] {
            assert!(!is_osdu_id(value), "{value}");
        }
    }

    #[test]
    fn a_records_osdu_ids_open_in_the_explorer_in_its_partition() {
        let row = decorated(json!({
            "deliveryKey": KEY, "flowId": FLOW, "status": "held", "partition": "test",
            "targetId": "test:master-data--Wellbore:W-1", "parentId": "test:master-data--Wellbore:P-1",
            "occupiedTargetId": "test:master-data--Wellbore:W-1-old"
        }));
        assert_eq!(row["links"]["page"], json!(format!("https://delivery.example.com/delivery/records/{FLOW}/{KEY}")));
        assert_eq!(
            row["links"]["osdu"],
            json!("https://delivery.example.com/delivery/explorer?id=test%3Amaster-data--Wellbore%3AW-1&partition=test")
        );
        assert_eq!(row["links"]["osduParent"], explorer("test:master-data--Wellbore:P-1", "test"));
        assert_eq!(row["links"]["osduOccupied"], explorer("test:master-data--Wellbore:W-1-old", "test"));

        // A row that does not say its partition is read in the one its id starts with.
        let hit = decorated(json!({ "deliveryKey": KEY, "flowId": FLOW, "status": "delivered", "targetId": "dev:master-data--Well:7" }));
        assert_eq!(hit["links"]["osdu"], explorer("dev:master-data--Well:7", "dev"));

        // A record that has not landed has no OSDU id, so it has no explorer link.
        let pending = decorated(json!({ "deliveryKey": KEY, "flowId": FLOW, "status": "pending", "targetId": null, "partition": "dev" }));
        assert!(pending["links"]["osdu"].is_null(), "{pending}");
    }

    #[test]
    fn an_osdu_id_is_linked_on_a_row_no_rule_claims() {
        // An id a delivery minted, or one an inventory found: no page of the module's is addressed by it, the explorer is.
        let minted = decorated(json!({ "artifactId": 4, "slot": "file", "role": "dataset", "targetId": "dev:dataset--File.Generic:f-1" }));
        assert_eq!(minted["links"], json!({ "osdu": explorer("dev:dataset--File.Generic:f-1", "dev") }));

        // An id the pending document refers to, under the property that holds it, inside the record that refers to it.
        let record = decorated(json!({
            "deliveryKey": KEY, "flowId": FLOW, "status": "waiting", "partition": "dev",
            "references": [{ "id": "dev:master-data--Wellbore:W-9", "property": "data.WellboreID" }]
        }));
        assert_eq!(
            record["references"][0]["links"],
            json!({ "osdu": explorer("dev:master-data--Wellbore:W-9", "dev") })
        );

        // A field of that name that is not an OSDU id is left alone.
        let plain = decorated(json!({ "id": "W-9", "property": "data.WellboreID" }));
        assert!(plain.get("links").is_none(), "{plain}");
    }

    #[test]
    fn a_cached_record_opens_in_the_explorer_and_a_lookup_key_does_not() {
        let change = decorated(json!({
            "tagId": 31, "scope": "dev", "typeName": "wellbore", "itemId": "dev:master-data--Wellbore:W-1",
            "path": "data.FacilityName", "status": "pending"
        }));
        assert_eq!(change["links"]["page"], json!("https://delivery.example.com/delivery/cache?partition=dev&tab=deliveries"));
        assert_eq!(change["links"]["osdu"], explorer("dev:master-data--Wellbore:W-1", "dev"));

        let item = decorated(json!({
            "itemId": 912, "scope": "dev", "version": "v12", "typeName": "unit", "recordId": "dev:reference-data--UnitOfMeasure:m"
        }));
        assert_eq!(item["links"]["osdu"], explorer("dev:reference-data--UnitOfMeasure:m", "dev"));

        // A lookup table's key, and an id-shaped key of another partition, are not records of this cache's partition.
        for key in ["WIRELINE", "prod:x:y"] {
            let lookup = decorated(json!({ "tagId": 32, "scope": "dev", "typeName": "logSource", "itemId": key, "status": "pending" }));
            assert!(lookup["links"]["osdu"].is_null(), "{lookup}");
        }
    }

    #[test]
    fn every_link_the_instructions_cite_is_one_a_delivery_row_carries() {
        let rows = decorated(json!([
            {
                "deliveryKey": KEY, "flowId": FLOW, "status": "held", "partition": "dev", "pipelineId": "pipe-1",
                "targetId": "dev:master-data--Wellbore:W-1", "parentId": "dev:master-data--Wellbore:P-1",
                "occupiedTargetId": "dev:master-data--Wellbore:W-2", "lastSubmissionId": SUBMISSION
            },
            {
                "activityId": 7, "flowId": FLOW, "kind": "redeliver", "actor": "user:alice",
                "deliveryKey": KEY, "submissionId": SUBMISSION, "runId": "run-3"
            }
        ]));
        let carried: std::collections::BTreeSet<&str> = rows
            .as_array()
            .expect("the rows")
            .iter()
            .flat_map(|row| row["links"].as_object().expect("a row's links").keys().map(String::as_str))
            .collect();
        let cited: std::collections::BTreeSet<&str> = crate::instructions::INSTRUCTIONS
            .split("links.")
            .skip(1)
            .filter_map(|rest| rest.split(|c: char| !c.is_ascii_alphanumeric()).next())
            .collect();
        assert!(cited.contains("page") && cited.contains("osdu"), "{cited:?}");
        for name in cited {
            assert!(carried.contains(name), "the instructions cite links.{name}, which no delivery row carries: {carried:?}");
        }
    }

    #[test]
    fn sqlflows_own_rows_are_left_to_sqlflow() {
        let plain = GuiLinks::new("https://delivery.example.com");
        let payload = json!([
            { "key": "dw.arc.wells", "kind": "Table", "serverRef": "dw" },
            { "id": "5f0f8c1e-58a5-4d6f-9d1e-0a4b6f1c2d3e", "repoId": "repo-1", "name": "wells-delivery", "kind": "delivery" },
            { "runId": "run-1", "pipelineId": "pipe-1", "flowName": "wells-delivery", "status": "succeeded" },
            { "id": "sched-1", "repoId": "repo-1", "timezone": "UTC", "enabled": true },
            { "columnName": "FacilityName" }
        ]);
        let mut expected = payload.clone();
        plain.decorate(&mut expected);
        assert_eq!(decorated(payload), expected);
    }
}
