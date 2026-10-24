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

fn record_page(links: &GuiLinks, flow_id: &str, key: &str) -> String {
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

/// The explorer opened on one OSDU record, in the partition the row names, else the one its id starts with (a record's
/// id begins with its data partition). The explorer makes a linked partition the title bar's, so the record is read in
/// the partition that holds it whichever one the reader last worked in (osdu/docs/reference/concepts/explorer.md).
fn explorer_page(links: &GuiLinks, id: &str, partition: Option<&str>) -> String {
    let partition = partition.unwrap_or_else(|| id.split(':').next().unwrap_or_default());
    links.route(&format!("/delivery/explorer?id={}&partition={}", encode(id), encode(partition)))
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
}

/// The links of one row of a delivery payload, or None when the row is not one of the module's.
pub fn delivery_links(links: &GuiLinks, row: &Map<String, Value>) -> Option<Map<String, Value>> {
    let mut found = Map::new();
    let mut put = |name: &str, link: String| {
        found.entry(name.to_string()).or_insert_with(|| Value::String(link));
    };

    osdu_links(links, row, &mut put);

    // ---- Rows that are something of the module's, each by the field that makes it so ------------------------------

    // An audit trail entry. It has no page of its own; the trail is where it is read, and what it was done to (the
    // record, the submission) is linked beside it.
    if number(row, "activityId").is_some() && row.contains_key("actor") {
        put("page", links.route("/delivery/activity"));
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

    // A record: the ledger's flow id and the delivery key together are what its page is addressed by.
    if let (Some(flow_id), Some(key)) = (text(row, "flowId"), text(row, "deliveryKey")) {
        put("page", record_page(links, flow_id, key));
        if let Some(submission) = text(row, "lastSubmissionId") {
            put("lastSubmission", submission_page(links, submission));
        }
        return Some(found);
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

    // A dimension, or one of its builds: the dimensions page opened on it.
    if let Some(dimension) = number(row, "dimensionId") {
        put("page", links.route(&format!("/delivery/dimensions?d={dimension}")));
        return Some(found);
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

    // A partition of the registry (it says whether it is registered and the default).
    if let (Some(_), true, true) = (text(row, "name"), row.contains_key("registered"), row.contains_key("isDefault")) {
        put("page", links.route("/delivery/partitions"));
        if let Some(name) = text(row, "name") {
            put("cache", cache_page(links, name));
        }
        return Some(found);
    }

    // A partition's cache, one of its versions, or a change found in it: each names the partition as `scope`.
    if let Some(scope) = text(row, "scope") {
        if row.contains_key("version") || row.contains_key("versions") || row.contains_key("typeName") {
            put("page", cache_page(links, scope));
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
        assert_eq!(row["links"]["page"], json!("https://delivery.example.com/delivery/activity"));
        assert_eq!(row["links"]["record"], json!(format!("https://delivery.example.com/delivery/records/{FLOW}/{KEY}")));
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
        assert_eq!(build["links"]["page"], json!("https://delivery.example.com/delivery/dimensions?d=3"));
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

        let version = decorated(json!({ "scope": "dev", "version": "v12", "sequence": 12, "current": true, "runId": "run-6" }));
        assert_eq!(version["links"]["page"], json!("https://delivery.example.com/delivery/cache?partition=dev"));
        assert_eq!(version["links"]["run"], json!("https://delivery.example.com/runs/run-6"));

        let change = decorated(json!({ "tagId": 31, "scope": "dev", "typeName": "wellbore", "path": "data.FacilityName", "status": "pending" }));
        assert_eq!(change["links"]["page"], json!("https://delivery.example.com/delivery/cache?partition=dev"));
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
        assert_eq!(change["links"]["page"], json!("https://delivery.example.com/delivery/cache?partition=dev"));
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
