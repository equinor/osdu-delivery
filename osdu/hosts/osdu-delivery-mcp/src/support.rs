//! What every delivery tool shares: checked identifiers, query strings, how a composed answer is put together, and
//! how the fields that carry data are kept out of an answer.

use serde_json::{json, Map, Value};
use sqlflow_mcp::{encode, json_str};

/// The page size a listing tool asks for when the caller names none. The control plane's own default is twice this;
/// a tool result is read by a model, and a first page that answers the question is worth more than a long one.
pub(crate) const DEFAULT_PAGE_SIZE: i64 = 25;

/// How many characters of an activity's captured log a listing keeps; the activity read alone carries more.
pub(crate) const LOG_PREVIEW: usize = 400;

/// How many characters of the captured log one activity read alone returns.
pub(crate) const LOG_LIMIT: usize = 40_000;

/// A tool's refusal of its own arguments, as the text a model reads.
pub(crate) fn refuse(message: impl AsRef<str>) -> String {
    format!("Error: {}", message.as_ref())
}

/// `value` as a GUID in its canonical form, or the refusal to answer with. `hint` says where the id comes from, since
/// the two ids a record is addressed by (the ledger's flow id and the delivery key) are easy to confuse with a
/// pipeline id.
pub(crate) fn guid(name: &str, value: &str, hint: &str) -> Result<String, String> {
    let trimmed = value.trim().trim_start_matches('{').trim_end_matches('}');
    let canonical = trimmed.len() == 36
        && trimmed.bytes().enumerate().all(|(index, byte)| match index {
            8 | 13 | 18 | 23 => byte == b'-',
            _ => byte.is_ascii_hexdigit(),
        });
    if canonical {
        Ok(trimmed.to_ascii_lowercase())
    } else {
        Err(refuse(format!("{name} '{value}' is not a GUID. {hint}")))
    }
}

/// An optional GUID: `None` when absent or blank, checked when given.
pub(crate) fn optional_guid(name: &str, value: Option<&str>, hint: &str) -> Result<Option<String>, String> {
    match value.map(str::trim).filter(|v| !v.is_empty()) {
        Some(given) => guid(name, given, hint).map(Some),
        None => Ok(None),
    }
}

/// A text argument trimmed, or `None` when it is absent or blank.
pub(crate) fn text(value: Option<String>) -> Option<String> {
    value.map(|v| v.trim().to_string()).filter(|v| !v.is_empty())
}

/// The query of one control-plane request: only the values a caller gave, in the order they were added.
#[derive(Default)]
pub(crate) struct Query(Vec<(&'static str, String)>);

impl Query {
    pub(crate) fn new() -> Self {
        Query::default()
    }

    /// Adds a text value when it is given and not blank.
    pub(crate) fn text(mut self, name: &'static str, value: Option<String>) -> Self {
        if let Some(value) = text(value) {
            self.0.push((name, value));
        }
        self
    }

    /// Adds a number or a flag when it is given.
    pub(crate) fn value<T: ToString>(mut self, name: &'static str, value: Option<T>) -> Self {
        if let Some(value) = value {
            self.0.push((name, value.to_string()));
        }
        self
    }

    /// Adds the page a listing is read at, with this server's own default size.
    pub(crate) fn page(self, page: Option<i64>, page_size: Option<i64>) -> Self {
        self.value("page", page)
            .value("pageSize", Some(page_size.unwrap_or(DEFAULT_PAGE_SIZE)))
    }

    pub(crate) fn pairs(&self) -> &[(&'static str, String)] {
        &self.0
    }

    /// `path` with this query appended, for a POST whose route reads query values beside its body.
    pub(crate) fn onto(&self, path: &str) -> String {
        if self.0.is_empty() {
            return path.to_string();
        }
        let query: Vec<String> = self
            .0
            .iter()
            .map(|(name, value)| format!("{name}={}", encode(value)))
            .collect();
        format!("{path}?{}", query.join("&"))
    }
}

/// One section of a composed answer: what the read returned, or why it did not. A section that fails says so in its
/// own place, so the rest of the answer still stands.
pub(crate) fn section(read: anyhow::Result<Value>) -> Value {
    match read {
        Ok(value) => value,
        Err(error) => json!({ "error": format!("{error:#}") }),
    }
}

/// A composed answer as the text a tool returns: the sections under their names, the subject's links, and a line on
/// how to read it.
pub(crate) fn composed(sections: Vec<(&str, Value)>, links: Map<String, Value>, reading: &str) -> String {
    let mut answer = Map::new();
    for (name, value) in sections {
        answer.insert(name.to_string(), value);
    }
    if !links.is_empty() {
        answer.insert("links".to_string(), Value::Object(links));
    }
    answer.insert("readingThis".to_string(), json!(reading));
    json_str(&Value::Object(answer))
}

/// Cuts the string `field` holds, in every object of `value`, to `max` characters, marking the cut and saying how
/// long it was. Used for the one field of the ledger too long to read whole in a listing: an activity's log.
pub(crate) fn clip_field(value: &mut Value, field: &str, max: usize) {
    match value {
        Value::Array(items) => {
            for item in items {
                clip_field(item, field, max);
            }
        }
        Value::Object(map) => {
            if let Some(Value::String(text)) = map.get_mut(field) {
                let length = text.chars().count();
                if length > max {
                    let head: String = text.chars().take(max).collect();
                    *text = format!("{head}... [truncated: {length} characters in all]");
                }
            }
            for (name, nested) in map.iter_mut() {
                if name != field {
                    clip_field(nested, field, max);
                }
            }
        }
        _ => {}
    }
}

/// Removes the named fields from every object of `value`, at any depth. This server answers from metadata: where an
/// endpoint's row carries the data itself beside it (the value a cached record held before and after a change), the
/// row is returned without it.
pub(crate) fn drop_fields(value: &mut Value, fields: &[&str]) {
    match value {
        Value::Array(items) => items.iter_mut().for_each(|item| drop_fields(item, fields)),
        Value::Object(map) => {
            for field in fields {
                map.remove(*field);
            }
            map.values_mut().for_each(|nested| drop_fields(nested, fields));
        }
        _ => {}
    }
}

/// Sets links an answer's tool knows on the answer's own `links`, beside those its rows' rules gave it: a link of the
/// same name is the tool's, every other one stays. A value that is not an object has nowhere to hold them.
pub(crate) fn put_links(value: &mut Value, links: Map<String, Value>) {
    if let Value::Object(map) = value {
        let held = map.entry("links").or_insert_with(|| Value::Object(Map::new()));
        if let Value::Object(held) = held {
            held.extend(links);
        }
    }
}

/// An answer a tool has rendered, with links only the tool knows added to it. A refusal (`Error: ...`) is not JSON
/// and is returned as it was.
pub(crate) fn relinked(answer: String, link: impl FnOnce(&mut Value)) -> String {
    match serde_json::from_str::<Value>(&answer) {
        Ok(mut value) => {
            link(&mut value);
            json_str(&value)
        }
        Err(_) => answer,
    }
}

/// Replaces every list held under `field` with how many entries it had, under `counted`. Used where a row lists the
/// records behind a finding: how many there were is metadata, and which records and what they held is not.
pub(crate) fn count_instead(value: &mut Value, field: &str, counted: &str) {
    match value {
        Value::Array(items) => items.iter_mut().for_each(|item| count_instead(item, field, counted)),
        Value::Object(map) => {
            if let Some(removed) = map.remove(field) {
                let count = removed.as_array().map(Vec::len).unwrap_or(0);
                map.insert(counted.to_string(), json!(count));
            }
            map.values_mut().for_each(|nested| count_instead(nested, field, counted));
        }
        _ => {}
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_guid_is_taken_in_any_case_and_with_braces() {
        assert_eq!(
            guid("flowId", " {5F0F8C1E-58A5-4D6F-9D1E-0A4B6F1C2D3E} ", "").unwrap(),
            "5f0f8c1e-58a5-4d6f-9d1e-0a4b6f1c2d3e"
        );
    }

    #[test]
    fn anything_else_is_refused_with_where_the_id_comes_from() {
        for bad in ["", "wellbore-1", "5f0f8c1e58a54d6f9d1e0a4b6f1c2d3e", "5f0f8c1e-58a5-4d6f-9d1e-0a4b6f1c2d3e/../x"] {
            let refused = guid("key", bad, "Take it from a record hit.").unwrap_err();
            assert!(refused.starts_with("Error: key '"), "{refused}");
            assert!(refused.ends_with("is not a GUID. Take it from a record hit."), "{refused}");
        }
    }

    #[test]
    fn an_optional_guid_is_absent_when_blank_and_checked_when_given() {
        assert_eq!(optional_guid("runId", None, "").unwrap(), None);
        assert_eq!(optional_guid("runId", Some("  "), "").unwrap(), None);
        assert!(optional_guid("runId", Some("nope"), "").is_err());
        assert!(optional_guid("runId", Some("5f0f8c1e-58a5-4d6f-9d1e-0a4b6f1c2d3e"), "").unwrap().is_some());
    }

    #[test]
    fn a_query_carries_only_what_was_given() {
        let query = Query::new()
            .text("search", Some("  wellbore 1 ".to_string()))
            .text("status", Some("  ".to_string()))
            .text("mode", None)
            .value("drifted", Some(true))
            .value::<i64>("max", None)
            .page(None, None);
        assert_eq!(
            query.pairs(),
            [
                ("search", "wellbore 1".to_string()),
                ("drifted", "true".to_string()),
                ("pageSize", "25".to_string()),
            ]
        );
    }

    #[test]
    fn a_query_on_a_path_is_encoded() {
        let query = Query::new()
            .text("interface", Some("well logs".to_string()))
            .text("partition", Some("dev&x=1".to_string()));
        assert_eq!(query.onto("/api/v1/x"), "/api/v1/x?interface=well%20logs&partition=dev%26x%3D1");
        assert_eq!(Query::new().onto("/api/v1/x"), "/api/v1/x");
    }

    #[test]
    fn a_failed_section_says_why_in_its_own_place() {
        let answer = composed(
            vec![
                ("record", json!({ "status": "delivered" })),
                ("attempts", section(Err(anyhow::anyhow!("attempts returned 500")))),
            ],
            Map::new(),
            "how to read it",
        );
        let answer: Value = serde_json::from_str(&answer).unwrap();
        assert_eq!(answer["record"]["status"], json!("delivered"));
        assert_eq!(answer["attempts"]["error"], json!("attempts returned 500"));
        assert_eq!(answer["readingThis"], json!("how to read it"));
        assert!(answer["links"].is_null());
    }

    #[test]
    fn a_long_log_is_clipped_wherever_it_sits_and_says_how_long_it_was() {
        let mut value = json!({
            "items": [{ "activityId": 1, "log": "x".repeat(50) }, { "activityId": 2, "log": "short" }, { "activityId": 3, "log": null }],
            "log": "y".repeat(12),
            "summary": "z".repeat(50),
        });
        clip_field(&mut value, "log", 10);
        assert_eq!(value["items"][0]["log"], json!(format!("{}... [truncated: 50 characters in all]", "x".repeat(10))));
        assert_eq!(value["items"][1]["log"], json!("short"));
        assert!(value["items"][2]["log"].is_null());
        assert_eq!(value["log"], json!(format!("{}... [truncated: 12 characters in all]", "y".repeat(10))));
        assert_eq!(value["summary"], json!("z".repeat(50)), "only the named field is cut");
    }

    #[test]
    fn data_fields_are_dropped_at_every_depth_and_nothing_else_is() {
        let mut page = json!({
            "items": [
                { "tagId": 1, "typeName": "wellbore", "path": "data.FacilityName", "oldValue": "A-1", "newValue": "A-1H", "affectedRecords": 12,
                  "waitingFlows": [{ "flowId": "f", "records": 3, "oldValue": "nested" }] }
            ],
            "total": 1
        });
        drop_fields(&mut page, &["oldValue", "newValue"]);
        assert_eq!(
            page,
            json!({
                "items": [{ "tagId": 1, "typeName": "wellbore", "path": "data.FacilityName", "affectedRecords": 12,
                            "waitingFlows": [{ "flowId": "f", "records": 3 }] }],
                "total": 1
            })
        );
    }

    #[test]
    fn a_list_of_examples_becomes_how_many_there_were() {
        let mut report = json!({
            "results": [{
                "test": "wellbore-names",
                "assertions": [
                    { "label": "has a name", "failing": 2, "examples": [{ "id": "dev:w:1", "value": "secret", "reason": "empty" }, { "id": "dev:w:2", "value": "x", "reason": "empty" }] },
                    { "label": "is unique", "failing": 0, "examples": [] },
                    { "label": "no examples field at all", "failing": 0 }
                ]
            }]
        });
        count_instead(&mut report, "examples", "exampleCount");
        let assertions = &report["results"][0]["assertions"];
        assert_eq!(assertions[0], json!({ "label": "has a name", "failing": 2, "exampleCount": 2 }));
        assert_eq!(assertions[1], json!({ "label": "is unique", "failing": 0, "exampleCount": 0 }));
        assert_eq!(assertions[2], json!({ "label": "no examples field at all", "failing": 0 }));
        assert!(!report.to_string().contains("secret"));
    }
}
