//! The OSDU Delivery MCP server as a client meets it: the host this crate composes, served over a stdio session,
//! against a control plane that is a small HTTP server of the tests' own.
//!
//! The control plane here answers the routes each test gives it and keeps every request it received, so a test reads
//! exactly what a tool asked the control plane for and what it made of the answer. Nothing reaches a database, an
//! OSDU or the network.

use std::collections::{BTreeSet, HashMap};
use std::convert::Infallible;
use std::net::SocketAddr;
use std::sync::{Arc, Mutex};
use std::time::Duration;

use bytes::Bytes;
use http_body_util::{BodyExt, Full};
use hyper::body::Incoming;
use hyper::service::service_fn;
use hyper_util::rt::TokioIo;
use osdu_delivery_mcp::{docs, instructions, WITHHELD};
use serde_json::{json, Value};
use sqlflow_mcp::config::TokenCache;
use sqlflow_mcp::control_plane::ControlPlane;
use sqlflow_mcp::rmcp::ServiceExt;
use sqlflow_mcp::server::SqlFlowMcp;
use tokio::io::{AsyncBufReadExt, AsyncWriteExt, BufReader};

const FLOW: &str = "5f0f8c1e-58a5-4d6f-9d1e-0a4b6f1c2d3e";
const KEY: &str = "6a1b2c3d-0000-4000-8000-0123456789ab";
const PIPELINE: &str = "11111111-2222-4333-8444-555555555555";
const SUBMISSION: &str = "22222222-3333-4444-8555-666666666666";
const REPO: &str = "33333333-4444-4555-8666-777777777777";

/// The delivery tools this server offers. A tool added to the module is added here, which is also where it is
/// decided whether it returns metadata alone.
const DELIVERY_TOOLS: &[&str] = &[
    "delivery_activities",
    "delivery_assertion_runs",
    "delivery_assertions",
    "delivery_cache_changes",
    "delivery_cache_versions",
    "delivery_caches",
    "delivery_check_mapping",
    "delivery_config",
    "delivery_decide_cache_changes",
    "delivery_dimensions",
    "delivery_find_records",
    "delivery_flow",
    "delivery_flow_records",
    "delivery_mappings",
    "delivery_osdu_schema_compare",
    "delivery_osdu_schemas",
    "delivery_partitions",
    "delivery_probe_target",
    "delivery_record",
    "delivery_redeliver_record",
    "delivery_release_records",
    "delivery_retrievals",
    "delivery_scaffold_mapping",
    "delivery_submissions",
    "delivery_sync_with_source",
    "delivery_templates",
    "delivery_verify_record",
];

/// The tools of SQLFlow's own server this product offers: each was read and found to answer from metadata (the
/// catalog, lineage, runs, schedules, code and documentation) or to be an action that returns none. With
/// [`WITHHELD`] it covers every tool SQLFlow's server has; a tool SQLFlow adds later fails the test below until it is
/// put on one side or the other.
const SQLFLOW_OFFERED: &[&str] = &[
    "search_docs", "get_doc", "get_doc_by_yaml_path", "get_doc_by_cli_command", "related_docs", "list_docs",
    "validate_flow", "list_flow_keys", "describe_flow_key",
    "get_control_plane_url", "set_control_plane_url", "check_connectivity", "login", "check_auth_status",
    "set_access_token", "logout",
    "list_repos", "get_repo", "list_pipelines", "list_flow_batches", "get_pipeline", "pipeline_definition",
    "pipeline_file_stats", "pipeline_columns",
    "list_runs", "get_run", "run_statements", "run_assertions", "run_files", "run_health_metrics",
    "list_schemas", "lineage_objects", "lineage_object_detail", "lineage_object_columns", "describe_object",
    "get_table_key", "get_table_joins", "object_lineage", "describe_object_refresh", "list_file_sources",
    "file_provenance", "catalog_tree", "lineage_edges", "lineage_waves", "lineage_dependencies",
    "database_schema_changes", "database_schema_history_databases", "database_object_ddl", "database_object_compare",
    "flow_definition_history", "flow_definition_file_history", "flow_definition_diff",
    "search_all", "search", "search_schema",
    // The semantic layer: the allow-listed schema with its business context, measures and stored example SQL.
    "get_semantic_layer", "search_semantic_layer", "list_semantic_tables", "describe_semantic_table",
    "list_subscribers", "describe_subscriber", "describe_subscriber_report",
    // Stored questions and the SQL that answers them, and recording a person's verdict on one: neither reads a row.
    "find_similar_questions", "confirm_question",
    // A `!` question's query: one read-only SELECT over the columns an approved AI context opens, checked by the
    // control plane on prepare and on run. The only tools here that return rows.
    "prepare_query", "run_query", "auto_run_trusted_match",
    // The conversation's mode: which one it is in, and switching it on the person's own command, which the model relays.
    "get_mode", "start_data_mode", "end_data_mode",
    "list_schedules", "get_schedule", "get_schedule_plan", "list_nodes", "list_repo_sources", "summary",
    "insights_flows", "insights_attention", "insights_recommendations", "insights_steps", "detect_stream_anomalies",
    "trigger_run", "cancel_run", "propose_pipelines",
];

// ---- A control plane that answers what a test gives it ----------------------------------------------------------

/// A route built from an id, as the name the control plane's table of answers is keyed by.
fn route(text: String) -> &'static str {
    Box::leak(text.into_boxed_str())
}

/// One request the control plane received.
#[derive(Debug, Clone)]
struct Seen {
    method: String,
    path: String,
    query: String,
    body: Value,
    authorization: String,
}

/// What the control plane answers one route with.
#[derive(Clone)]
struct Answer {
    status: u16,
    body: Value,
}

struct Estate {
    url: String,
    seen: Arc<Mutex<Vec<Seen>>>,
}

impl Estate {
    /// Starts a control plane that answers `routes` ("GET /api/v1/...") and 404 for anything else.
    async fn start(routes: Vec<(&str, u16, Value)>) -> Estate {
        let routes: Arc<HashMap<String, Answer>> = Arc::new(
            routes
                .into_iter()
                .map(|(route, status, body)| (route.to_string(), Answer { status, body }))
                .collect(),
        );
        let seen: Arc<Mutex<Vec<Seen>>> = Arc::default();
        let listener = tokio::net::TcpListener::bind(SocketAddr::from(([127, 0, 0, 1], 0)))
            .await
            .expect("a loopback port");
        let address = listener.local_addr().expect("a bound address");
        let log = seen.clone();
        tokio::spawn(async move {
            loop {
                let Ok((stream, _)) = listener.accept().await else {
                    return;
                };
                let (routes, log) = (routes.clone(), log.clone());
                tokio::spawn(async move {
                    let service = service_fn(move |req| answer(req, routes.clone(), log.clone()));
                    let _ = hyper::server::conn::http1::Builder::new()
                        .serve_connection(TokioIo::new(stream), service)
                        .await;
                });
            }
        });
        Estate { url: format!("http://{address}"), seen }
    }

    fn seen(&self) -> Vec<Seen> {
        self.seen.lock().expect("the request log").clone()
    }

    /// The requests received, as "METHOD path?query" lines, in order.
    fn calls(&self) -> Vec<String> {
        self.seen()
            .iter()
            .map(|s| match s.query.as_str() {
                "" => format!("{} {}", s.method, s.path),
                query => format!("{} {}?{query}", s.method, s.path),
            })
            .collect()
    }

    /// The one request received for `route`.
    fn only(&self, method: &str, path: &str) -> Seen {
        let found: Vec<Seen> = self.seen().into_iter().filter(|s| s.method == method && s.path == path).collect();
        assert_eq!(found.len(), 1, "expected one {method} {path}, saw {:?}", self.calls());
        found.into_iter().next().expect("one request")
    }
}

async fn answer(
    req: hyper::Request<Incoming>,
    routes: Arc<HashMap<String, Answer>>,
    seen: Arc<Mutex<Vec<Seen>>>,
) -> Result<hyper::Response<Full<Bytes>>, Infallible> {
    let method = req.method().as_str().to_string();
    let path = req.uri().path().to_string();
    let query = req.uri().query().unwrap_or_default().to_string();
    let authorization = req
        .headers()
        .get(http::header::AUTHORIZATION)
        .and_then(|v| v.to_str().ok())
        .unwrap_or_default()
        .to_string();
    let sent = req.into_body().collect().await.map(|b| b.to_bytes()).unwrap_or_default();
    let body = serde_json::from_slice::<Value>(&sent).unwrap_or(Value::Null);
    seen.lock().expect("the request log").push(Seen {
        method: method.clone(),
        path: path.clone(),
        query,
        body,
        authorization,
    });

    let (status, body) = match routes.get(&format!("{method} {path}")) {
        Some(answer) => (answer.status, answer.body.to_string()),
        None => (404, json!({ "title": "Not found", "detail": format!("No route {method} {path}.") }).to_string()),
    };
    Ok(hyper::Response::builder()
        .status(status)
        .header(http::header::CONTENT_TYPE, "application/json")
        .body(Full::new(Bytes::from(body)))
        .expect("a json response"))
}

// ---- The server, over a stdio session -----------------------------------------------------------------------------

/// A client's end of a stdio session with the composed server: one JSON-RPC message per line, each way.
struct Session {
    reader: BufReader<tokio::io::ReadHalf<tokio::io::DuplexStream>>,
    writer: tokio::io::WriteHalf<tokio::io::DuplexStream>,
    next_id: u64,
    initialized: Value,
}

impl Session {
    /// The host this crate composes, signed in, against `estate`, with links on the product's GUI.
    async fn open(estate: &Estate) -> Session {
        let host = osdu_delivery_mcp::host();
        let identity = host.identity().expect("the host is well named");
        let token = TokenCache {
            access_token: "operator-token".to_string(),
            scope: "read operate".to_string(),
            expires_at: None,
            renews: false,
        };
        // A stdio session presents the credential it holds, as a signed-in client on a person's machine does.
        let cp = Arc::new(ControlPlane::new(&identity, &estate.url, Some(token), true));
        let server: SqlFlowMcp = host
            .server(cp, "https://delivery.example.com", false)
            .expect("the delivery host composes");

        let (client_end, server_end) = tokio::io::duplex(1 << 20);
        let (server_read, server_write) = tokio::io::split(server_end);
        tokio::spawn(async move {
            let running = server.serve((server_read, server_write)).await.expect("the session starts");
            let _ = running.waiting().await;
        });
        let (client_read, client_write) = tokio::io::split(client_end);
        let mut session = Session {
            reader: BufReader::new(client_read),
            writer: client_write,
            next_id: 0,
            initialized: Value::Null,
        };
        session.initialized = session
            .request(
                "initialize",
                json!({
                    "protocolVersion": "2025-03-26",
                    "capabilities": {},
                    "clientInfo": { "name": "delivery-server-test", "version": "0" },
                }),
            )
            .await;
        session.send(json!({ "jsonrpc": "2.0", "method": "notifications/initialized" })).await;
        session
    }

    async fn send(&mut self, message: Value) {
        self.writer.write_all(format!("{message}\n").as_bytes()).await.expect("the server reads");
        self.writer.flush().await.expect("the server reads");
    }

    async fn request(&mut self, method: &str, params: Value) -> Value {
        self.next_id += 1;
        let id = self.next_id;
        self.send(json!({ "jsonrpc": "2.0", "id": id, "method": method, "params": params })).await;
        let answer = tokio::time::timeout(Duration::from_secs(60), async {
            loop {
                let mut line = String::new();
                let read = self.reader.read_line(&mut line).await.expect("the server writes");
                assert!(read > 0, "the server closed the session before answering {method}");
                if let Ok(message) = serde_json::from_str::<Value>(line.trim()) {
                    if message["id"] == json!(id) {
                        return message;
                    }
                }
            }
        })
        .await;
        answer.unwrap_or_else(|_| panic!("no answer to {method} within 60s"))
    }

    /// Calls a tool and returns the text of its result.
    async fn call(&mut self, tool: &str, arguments: Value) -> String {
        let answer = self.request("tools/call", json!({ "name": tool, "arguments": arguments })).await;
        answer["result"]["content"][0]["text"]
            .as_str()
            .unwrap_or_else(|| panic!("{tool} returned no text: {answer}"))
            .to_string()
    }

    /// Calls a tool and parses its result as JSON.
    async fn json(&mut self, tool: &str, arguments: Value) -> Value {
        let text = self.call(tool, arguments).await;
        serde_json::from_str(&text).unwrap_or_else(|e| panic!("{tool} did not return json ({e}): {text}"))
    }

    async fn tools(&mut self) -> Vec<Value> {
        self.request("tools/list", json!({})).await["result"]["tools"]
            .as_array()
            .expect("a tool list")
            .clone()
    }

    fn instructions(&self) -> &str {
        self.initialized["result"]["instructions"].as_str().expect("instructions")
    }
}

fn names(tools: &[Value]) -> BTreeSet<String> {
    tools.iter().map(|tool| tool["name"].as_str().expect("a tool name").to_string()).collect()
}

// ---- What the server offers ---------------------------------------------------------------------------------------

#[tokio::test(flavor = "multi_thread")]
async fn it_is_sqlflows_server_with_the_delivery_tools_and_without_the_ones_that_read_data() {
    let estate = Estate::start(vec![]).await;
    let mut session = Session::open(&estate).await;

    assert_eq!(session.initialized["result"]["serverInfo"]["name"], json!("osdu-delivery-mcp"));
    assert_eq!(session.initialized["result"]["serverInfo"]["version"], json!(env!("CARGO_PKG_VERSION")));

    let listed = names(&session.tools().await);
    let delivery: BTreeSet<String> = DELIVERY_TOOLS.iter().map(|t| t.to_string()).collect();
    let offered: BTreeSet<String> = SQLFLOW_OFFERED.iter().map(|t| t.to_string()).collect();
    let withheld: BTreeSet<String> = WITHHELD.iter().map(|t| t.to_string()).collect();

    // Every tool of SQLFlow's server is decided on: offered here, or left out as one that reads data.
    let sqlflows: BTreeSet<String> = SqlFlowMcp::tool_names().into_iter().collect();
    let decided: BTreeSet<String> = offered.union(&withheld).cloned().collect();
    assert_eq!(
        sqlflows.difference(&decided).collect::<Vec<_>>(),
        Vec::<&String>::new(),
        "SQLFlow's server has tools this product has not decided on. Read each: one that returns rows, or reaches a \
         datasource or a source's content, goes in WITHHELD (src/lib.rs); one that answers from metadata goes in \
         SQLFLOW_OFFERED here."
    );
    assert_eq!(decided.difference(&sqlflows).collect::<Vec<_>>(), Vec::<&String>::new(), "a decided tool is gone");
    assert!(offered.is_disjoint(&withheld));

    // What a client sees is exactly the offered tools and the module's: this stdio session names no mode, so it lists
    // every one.
    let expected: BTreeSet<String> = offered.union(&delivery).cloned().collect();
    assert_eq!(listed, expected);
    for tool in WITHHELD {
        assert!(!listed.contains(*tool), "{tool} reads data and is listed");
        let called = session.request("tools/call", json!({ "name": tool, "arguments": {} })).await;
        assert!(called["error"]["message"].as_str().unwrap_or_default().contains("tool not found"), "{tool}: {called}");
    }
    assert!(estate.calls().is_empty(), "listing and refusing reach no control plane");
}

#[tokio::test(flavor = "multi_thread")]
async fn every_delivery_tool_is_described_for_a_model_choosing_between_tools() {
    let estate = Estate::start(vec![]).await;
    let mut session = Session::open(&estate).await;
    let tools: Vec<Value> = session
        .tools()
        .await
        .into_iter()
        .filter(|tool| tool["name"].as_str().is_some_and(|name| name.starts_with("delivery_")))
        .collect();
    assert_eq!(tools.len(), DELIVERY_TOOLS.len());

    let mut total = 0;
    for tool in &tools {
        let name = tool["name"].as_str().expect("a name");
        let description = tool["description"].as_str().unwrap_or_default();
        let length = description.chars().count();
        total += length;
        // Long enough to say what it returns, when to use it and where its ids come from; short enough that a
        // hundred tools' descriptions still leave a model room to think.
        assert!((300..=750).contains(&length), "{name}'s description is {length} characters");
        assert!(!description.contains('\u{2014}'), "{name}'s description uses an em dash");
        assert!(!description.contains("  "), "{name}'s description has a run of spaces: a broken line continuation");
        assert!(description.contains("Use it") || description.contains("operator action") || description.contains("Run it")
            || description.contains("Start here") || description.contains("This is how"),
            "{name}'s description does not say when to use it");

        // Every argument a model may pass says what it is, unless its name alone does.
        let properties = tool["inputSchema"]["properties"].as_object().cloned().unwrap_or_default();
        for (argument, schema) in &properties {
            let plain = ["page", "version", "submissionId", "runId", "deliveryKey", "deliveredBy", "drifted", "fromRelease"];
            if plain.contains(&argument.as_str()) {
                continue;
            }
            assert!(
                schema["description"].as_str().is_some_and(|d| !d.trim().is_empty()),
                "{name}.{argument} has no description"
            );
        }
    }
    assert!(total <= 17_000, "the delivery tools' descriptions add up to {total} characters");

    // The operator's actions say what they are; nothing else claims to be one.
    let acts = ["delivery_probe_target", "delivery_verify_record", "delivery_sync_with_source", "delivery_release_records",
        "delivery_redeliver_record", "delivery_decide_cache_changes"];
    for tool in &tools {
        let name = tool["name"].as_str().expect("a name");
        let says = tool["description"].as_str().unwrap_or_default().contains("(operator action)");
        assert_eq!(says, acts.contains(&name), "{name}: an action says it is an operator action, and only an action does");
    }
}

#[tokio::test(flavor = "multi_thread")]
async fn the_instructions_name_only_what_the_server_has() {
    let estate = Estate::start(vec![]).await;
    let mut session = Session::open(&estate).await;
    let text = session.instructions().to_string();
    let listed = names(&session.tools().await);

    // The product introduces itself first, then SQLFlow's instructions, then the module's section, then what is not
    // offered. Nothing recommends a tool this server leaves out.
    let module_at = text
        .find("OSDU DELIVERY (the extension: the delivery_* tools and the delivery- pages).")
        .expect("the module's section");
    let withheld_at = text.find("Not offered by this server").expect("the withheld note");
    assert!(text.starts_with(instructions::INTRODUCTION), "{text}");
    assert!(!text.contains("SQLFlow MCP server."), "{text}");
    assert!(!text.contains("Source discovery"), "the instructions recommend discover_source, which is withheld");
    assert!(module_at < withheld_at);
    for tool in WITHHELD {
        assert!(text[withheld_at..].contains(tool), "the note does not name {tool}");
        assert!(!instructions::INSTRUCTIONS.contains(tool), "the module's section names {tool}, which is not offered");
    }

    // Every tool the module's section names exists, and every delivery tool is named in it.
    let words: BTreeSet<&str> = instructions::INSTRUCTIONS
        .split(|c: char| !c.is_ascii_alphanumeric() && c != '_')
        .filter(|word| word.contains('_') && word.chars().all(|c| c.is_ascii_lowercase() || c == '_'))
        .filter(|word| *word != "delivery_")
        .collect();
    for word in &words {
        assert!(listed.contains(*word), "the instructions name {word}, which is not a tool of this server");
    }
    for tool in DELIVERY_TOOLS {
        assert!(words.contains(tool), "the instructions never say when to use {tool}");
    }
    assert!(!instructions::INSTRUCTIONS.contains('\u{2014}') && !instructions::INTRODUCTION.contains('\u{2014}'));

    // Every page the section names in full is one the doc tools return: the extension's own, and SQLFlow's.
    let ids: BTreeSet<String> = docs::pages().into_iter().map(|page| page.meta.id).collect();
    let named: BTreeSet<&str> = instructions::INSTRUCTIONS
        .split(|c: char| !c.is_ascii_alphanumeric() && c != '-')
        .filter(|word| word.starts_with("delivery-") && !word.ends_with('-'))
        .collect();
    assert!(named.contains("delivery-wiki-pattern-catalog") && named.contains("delivery-cli-run"), "{named:?}");
    for id in &named {
        assert!(ids.contains(*id), "the instructions name the page {id}, which is not indexed");
    }
    for id in ["flow-ing", "flow-source", "guide-table-to-table-ingestion"] {
        assert!(instructions::INSTRUCTIONS.contains(id));
        assert!(session.call("get_doc", json!({ "id": id })).await.starts_with("# "), "SQLFlow's {id}");
    }

    // The page they point at for a run's arguments carries them.
    assert!(instructions::INSTRUCTIONS.contains("get_doc(\"delivery-cli-run\")"));
    let page = session.call("get_doc", json!({ "id": "delivery-cli-run" })).await;
    assert!(page.contains("`recordKeys`") && page.contains("`redeliver`"), "{page}");
}

#[tokio::test(flavor = "multi_thread")]
async fn the_products_documentation_and_flow_kinds_are_answered_by_sqlflows_own_tools() {
    let estate = Estate::start(vec![]).await;
    let mut session = Session::open(&estate).await;

    // A page of the product is served whole, under its own title, beside SQLFlow's corpus.
    let page = session.call("get_doc", json!({ "id": "delivery-flow-cache" })).await;
    assert!(page.starts_with("# Cache flow"), "{}", &page[..page.len().min(200)]);
    assert!(page.contains("id: delivery-flow-cache") && page.contains("sourceRefs:"), "the page with its frontmatter");
    assert!(session.call("get_doc", json!({ "id": "cli-run" })).await.starts_with("# "));

    // A verb of the extension answers by its name, and a page leads to its related pages, SQLFlow's among them.
    assert!(session.call("get_doc_by_cli_command", json!({ "command": "check" })).await.contains("sqlflow check"));
    let related = session.json("related_docs", json!({ "id": "delivery-guide-lookup-table-cache" })).await;
    let related: Vec<&str> = related["related"].as_array().expect("related pages").iter().filter_map(|r| r["id"].as_str()).collect();
    assert!(related.contains(&"delivery-flow-cache"), "{related:?}");
    assert!(related.iter().any(|id| !id.starts_with("delivery-")), "a guide leads to SQLFlow's pages too: {related:?}");

    // The flow-language tools know the kinds and documents the module adds.
    for kind in ["delivery", "retrieval", "cache", "assertion", "dimension", "inventory"] {
        let keys = session.json("list_flow_keys", json!({ "flowType": kind })).await;
        assert!(keys["count"].as_u64().is_some_and(|count| count > 5), "{kind}: {keys}");
    }
    let key = session.json("describe_flow_key", json!({ "path": "render.mapping", "flowType": "delivery" })).await;
    assert_eq!(key["path"], json!("render.mapping"), "{key}");
    assert!(key["description"].as_str().is_some_and(|d| !d.is_empty()));
    assert!(estate.calls().is_empty(), "the docs and the census are in the binary");
}

/// A test of known values as a QC sheet's rows become one: the flow-language tools read it as the loader does, its full
/// paths through other records and its conditions included, and still catch a key the loader would refuse.
#[tokio::test(flavor = "multi_thread")]
async fn a_test_of_known_values_validates_and_its_keys_are_described() {
    let estate = Estate::start(vec![]).await;
    let mut session = Session::open(&estate).await;
    let flow = r#"flowType: assertion
name: qc-a-1-f-11
source:
  endpoint: ${env:OSDU_URL}
tests:
  - name: composite-log
    kind: osdu:wks:work-product-component--WellLog:1.5.0
    template: WellLog@1.5.0
    asOf: 2026-10-13
    validFor: 90d
    asOfField: createTime
    assert:
      - name: the curves of the COMPOSITE log of A/1-F-11
        knownValues:
          tolerance: 0.0005
          warnWithin: 1%
          notFound: warning
          locateBy:
            '[data.WellboreID, data.FacilityName]': A/1-F-11
            data.Name: { equals: COMPOSITE, ignoreCase: true }
          check:
            data.SamplingInterval: 0.1524
          rows:
            - locateBy:
                data.Curves.CurveID: DT
              check:
                data.Curves.CurveUnit: "{partition}:reference-data--UnitOfMeasure:us%2Fft:"
                data.Curves.TopDepth: { equals: 1023.0612, tolerance: 0.00005 }
                '[data.WellboreID, work-product-component--WellLog[data.WellboreID], data.Name]': { in: [COMPOSITE, PRESSURE], values: any }
                data.CompanyID: ~
                data.LogSource: { equals: OPERATOR, disabled: the log source is renamed next release }
            - locateBy: { data.Curves.CurveID: GR }
              disabled: GR is not delivered yet
      - aggregate: count
        field: data.Curves.CurveID
        except: [MD]
        equals: 2
        warnWithin: 10%
        asOf: 2026-10-13
        validFor: 1w
      - count: 1
        disabled: kept for when the second log arrives
"#;
    let answer = session.call("validate_flow", json!({ "yaml": flow })).await;
    assert_eq!(answer, "OK: no problems found.", "{answer}");

    let key = session.json("describe_flow_key", json!({ "path": "tests[].assert[].knownValues.locateBy", "flowType": "assertion" })).await;
    assert!(key["description"].as_str().is_some_and(|d| d.contains("full paths")), "{key}");
    let key = session.json("describe_flow_key", json!({ "path": "tests[].assert[].warnWithin", "flowType": "assertion" })).await;
    assert!(key["description"].as_str().is_some_and(|d| d.contains("only warn")), "{key}");
    let key = session.json("describe_flow_key", json!({ "path": "tests[].validFor", "flowType": "assertion" })).await;
    assert!(key["description"].as_str().is_some_and(|d| d.contains("needing new proof values")), "{key}");

    // A key the loader refuses is still reported.
    let wrong = session.call("validate_flow", json!({ "yaml": flow.replace("validFor: 90d", "validFrom: 90d") })).await;
    assert!(wrong.contains("validFrom"), "{wrong}");
}

/// Questions people ask, in their own words, and the pages that answer each. The documentation is written so that the
/// search, which ranks on a page's title, keywords and summary, puts the answer near the top
/// (osdu/docs/reference/README.md, Page format); a page whose frontmatter drifts away from how it is asked about fails
/// here. The first is the question this corpus was rebuilt for.
const QUESTIONS: &[(&str, &[&str])] = &[
    (
        "reading json files into a staging and then into a silver table, the next step is to read this into the cache so a mapping can use it",
        &["delivery-guide-lookup-table-cache"],
    ),
    ("lookup table from an ingestion table", &["delivery-guide-lookup-table-cache", "delivery-flow-cache"]),
    ("cache import", &["delivery-cli-cache"]),
    ("cache flow table key fields", &["delivery-flow-cache"]),
    ("replace a value from the cache", &["delivery-flow-mapping-modifiers"]),
    ("findBy cached reference data unit of measure", &["delivery-flow-mapping-lookups", "delivery-guide-reference-data-cache"]),
    ("why is my record held", &["delivery-concept-record-lifecycle", "delivery-guide-operations-runbook"]),
    ("redeliver a record", &["delivery-guide-operations-runbook", "delivery-concept-removal-and-reversal", "delivery-cli-run"]),
    ("remove records from osdu", &["delivery-concept-removal-and-reversal"]),
    ("well log curves bulk data wellbore ddms", &["delivery-guide-bulk-data", "delivery-flow-ddms"]),
    ("data quality tests", &["delivery-guide-data-quality-tests", "delivery-flow-assertion"]),
    ("distinct values of a field", &["delivery-flow-dimension", "delivery-guide-dimensions"]),
    ("join dimension tables into one view with typed columns", &["delivery-flow-dimension"]),
    ("find orphan records in osdu", &["delivery-guide-finding-orphans", "delivery-flow-inventory"]),
    ("select inventory ids by facility name and remove only those", &["delivery-flow-inventory"]),
    ("check a flow before running it", &["delivery-cli-check", "delivery-concept-preflight"]),
    ("getting started first delivery", &["delivery-guide-getting-started"]),
    ("one source delivers several kinds", &["delivery-flow-interfaces", "delivery-guide-multi-kind-source"]),
    ("download an osdu schema as a template file", &["delivery-cli-template", "delivery-concept-templates"]),
    ("register a partition", &["delivery-cli-partition", "delivery-concept-partitions"]),
    ("which route should a delivery flow use", &["delivery-flow-routes", "delivery-wiki-pattern-catalog"]),
    ("run payload recordKeys redeliver", &["delivery-cli-run"]),
    ("mapping expression when condition", &["delivery-flow-mapping-expressions", "delivery-flow-mapping-values"]),
    ("hold a record whose value breaks a business rule in the mapping", &["delivery-flow-mapping-assertions"]),
    ("search the platform for a wellbore in a mapping", &["delivery-flow-mapping-lookups"]),
    ("what changed since the last run, why did a record deliver again", &["delivery-concept-change-detection"]),
    ("retrieve osdu records into a table", &["delivery-guide-retrieving-records", "delivery-flow-retrieval"]),
    ("dictionary of static values for a mapping", &["delivery-flow-dictionary"]),
    ("approve a cache change", &["delivery-concept-partition-cache"]),
    ("delivery api endpoints", &["delivery-concept-api"]),
    ("environment variables for osdu delivery", &["delivery-concept-environment-variables"]),
    ("install the mcp server", &["delivery-guide-mcp"]),
    ("the ledger tables and traceability", &["delivery-concept-ledger"]),
    ("recover a stopped submission", &["delivery-concept-submissions", "delivery-guide-operations-runbook"]),
    ("literal secret in a flow", &["delivery-cli-validate", "delivery-flow-delivery"]),
    ("seismic store ddms", &["delivery-flow-ddms-services"]),
    ("undo unfinished deliveries", &["delivery-concept-removal-and-reversal"]),
    ("check the values a qc sheet states against what osdu holds", &["delivery-flow-known-values", "delivery-guide-data-quality-tests"]),
    ("find a well log by its wellbore name and check its curves", &["delivery-flow-known-values"]),
    ("proof values expire and need restating", &["delivery-flow-assertion"]),
    ("a count 10 percent off is acceptable but should only warn", &["delivery-flow-assertion"]),
    ("in which order are wells, wellbores and well logs delivered", &["delivery-concept-osdu-order"]),
    ("which schedule must run before another schedule", &["delivery-concept-osdu-order"]),
    ("build osdu lineage of a data definitions release", &["delivery-concept-osdu-order"]),
    ("update the cache during a schedule after a delivery", &["delivery-concept-partition-cache", "delivery-wiki-0017-pipelines-update-the-cache"]),
    ("update the cache before running a pipeline", &["delivery-concept-partition-cache", "delivery-flow-delivery"]),
    ("refresh only one type of a cache flow", &["delivery-flow-cache"]),
    ("which table holds what in the osdu schema", &["delivery-concept-database"]),
    ("which database table holds a record's attempts", &["delivery-concept-database", "delivery-concept-ledger"]),
];

/// Questions about what a table of the osdu schema holds, asked with `type: table`, and the table page that answers each.
const TABLE_QUESTIONS: &[(&str, &str)] = &[
    ("the source file and row a delivery attempt was built from", "delivery-table-attempt"),
    ("which cache flow captured a cached record", "delivery-table-cache-member"),
    ("the orphan ids an inventory found", "delivery-table-inventory-record"),
    ("what a worker appended under its lease", "delivery-table-record-event"),
    ("who approved a cache change and its rollout", "delivery-table-update-tag"),
    ("the audit trail of interventions", "delivery-table-activity"),
];

#[tokio::test(flavor = "multi_thread")]
async fn a_table_question_finds_its_table_when_the_search_asks_for_tables() {
    let estate = Estate::start(vec![]).await;
    let mut session = Session::open(&estate).await;

    let mut missed = Vec::new();
    for (question, answer) in TABLE_QUESTIONS {
        let found = session.json("search_docs", json!({ "query": question, "type": "table", "limit": 5 })).await;
        let results = found["results"].as_array().expect("search results");
        assert!(results.iter().all(|hit| hit["type"] == "table"), "'{question}' found a page that is no table: {results:?}");
        if !results.iter().any(|hit| hit["id"] == *answer) {
            let top: Vec<&str> = results.iter().filter_map(|hit| hit["id"].as_str()).collect();
            missed.push(format!("'{question}' found {top:?}, not {answer}"));
        }
    }
    assert!(missed.is_empty(), "table questions whose table is not in the top five:\n{}", missed.join("\n"));

    // Without the type, a task question is answered by the pages that explain the task, never by a table page.
    let untyped = session.json("search_docs", json!({ "query": "replace a value from the cache", "limit": 50 })).await;
    let results = untyped["results"].as_array().expect("search results");
    assert!(results.iter().all(|hit| hit["type"] != "table"), "{results:?}");
}

#[tokio::test(flavor = "multi_thread")]
async fn a_question_finds_the_page_that_answers_it() {
    let estate = Estate::start(vec![]).await;
    let mut session = Session::open(&estate).await;
    let ids: BTreeSet<String> = docs::pages().into_iter().map(|page| page.meta.id).collect();

    let mut missed = Vec::new();
    for (question, answers) in QUESTIONS {
        for answer in *answers {
            assert!(ids.contains(*answer), "{answer} is not a page of the corpus");
        }
        let found = session.json("search_docs", json!({ "query": question, "limit": 5 })).await;
        let top: Vec<String> = found["results"]
            .as_array()
            .expect("search results")
            .iter()
            .filter_map(|hit| hit["id"].as_str().map(str::to_string))
            .collect();
        if !answers.iter().any(|answer| top.iter().any(|id| id == answer)) {
            missed.push(format!("'{question}' found {top:?}, not any of {answers:?}"));
        }
    }
    assert!(missed.is_empty(), "questions whose answer is not in the top five:\n{}", missed.join("\n"));
}

// ---- Records ---------------------------------------------------------------------------------------------------------

#[tokio::test(flavor = "multi_thread")]
async fn a_record_is_found_by_what_the_caller_holds_and_linked_to_its_page() {
    let estate = Estate::start(vec![(
        "GET /api/v1/delivery/records",
        200,
        json!({
            "items": [{
                "deliveryKey": KEY, "flowId": FLOW, "flowName": "wells", "pipelineId": PIPELINE,
                "sourceKey": "W-1", "label": "Well One", "targetId": "dev:master-data--Wellbore:W-1",
                "status": "delivered", "sourceFileName": "wells_2026-10-01.csv", "sourceRowNumber": 12
            }],
            "page": 1, "pageSize": 25, "total": 1, "totalCapped": false
        }),
    )])
    .await;
    let mut session = Session::open(&estate).await;

    let hits = session
        .json(
            "delivery_find_records",
            json!({ "search": " dev:master-data--Wellbore:W ", "status": "delivered", "partition": "dev", "flowId": FLOW.to_uppercase() }),
        )
        .await;

    let request = estate.only("GET", "/api/v1/delivery/records");
    assert_eq!(
        request.query,
        format!("search=dev%3Amaster-data--Wellbore%3AW&status=delivered&flowId={FLOW}&partition=dev&pageSize=25")
    );
    assert_eq!(request.authorization, "Bearer operator-token");
    assert_eq!(
        hits["items"][0]["links"]["page"],
        json!(format!("https://delivery.example.com/delivery/records/{FLOW}/{KEY}"))
    );
    assert_eq!(
        hits["items"][0]["links"]["flow"],
        json!(format!("https://delivery.example.com/pipelines/{PIPELINE}"))
    );
}

#[tokio::test(flavor = "multi_thread")]
async fn a_records_whole_trace_is_one_call_and_carries_no_content() {
    let base = format!("/api/v1/delivery/records/{FLOW}/{KEY}");
    let estate = Estate::start(vec![
        (
            route(format!("GET {base}")),
            200,
            json!({ "record": { "deliveryKey": KEY, "flowId": FLOW, "status": "failed", "lastError": "401 from storage" },
                    "pipelineId": PIPELINE, "flowName": "wells" }),
        ),
        (
            route(format!("GET {base}/attempts")),
            200,
            json!([{ "attemptId": 9, "deliveryKey": KEY, "outcome": "failed", "phase": "record", "error": "401 from storage", "runId": "run-1" }]),
        ),
        (
            route(format!("GET {base}/chain")),
            200,
            json!({ "arrivedUtc": "2026-10-01T00:00:00Z", "changes": [{ "fileName": "wells_2026-10-01.csv", "rowNumber": 12 }] }),
        ),
        (
            route(format!("GET {base}/activities")),
            200,
            json!([{ "activityId": 4, "flowId": FLOW, "kind": "deliver", "actor": "schedule:hourly", "outcome": "failed", "log": "x".repeat(5000) }]),
        ),
        (route(format!("GET {base}/cache")), 200, json!([{ "typeName": "wellbore", "value": "a cached value" }])),
    ])
    .await;
    let mut session = Session::open(&estate).await;

    let trace = session.json("delivery_record", json!({ "flowId": FLOW, "deliveryKey": KEY })).await;

    assert_eq!(trace["record"]["record"]["status"], json!("failed"));
    assert_eq!(trace["attempts"][0]["error"], json!("401 from storage"));
    assert_eq!(trace["chain"]["changes"][0]["fileName"], json!("wells_2026-10-01.csv"));
    assert_eq!(trace["activities"][0]["actor"], json!("schedule:hourly"));
    assert_eq!(trace["links"]["page"], json!(format!("https://delivery.example.com/delivery/records/{FLOW}/{KEY}")));
    assert!(trace["readingThis"].is_string());

    // The run log is cut short in a trace, and says how long it was.
    let log = trace["activities"][0]["log"].as_str().expect("a log");
    assert!(log.ends_with("... [truncated: 5000 characters in all]") && log.len() < 500, "{}", log.len());

    // The cached values a record was rendered from are data: they are neither asked for nor returned.
    let calls = estate.calls();
    assert_eq!(calls.len(), 4, "{calls:?}");
    assert!(calls.contains(&format!("GET {base}/attempts?max=20")), "{calls:?}");
    assert!(calls.contains(&format!("GET {base}/activities?max=20")), "{calls:?}");
    assert!(!calls.iter().any(|call| call.ends_with("/cache")), "{calls:?}");
    assert!(trace.get("cacheUses").is_none() && !trace.to_string().contains("a cached value"));

    // One part alone, and a part that is not one.
    let state = session.json("delivery_record", json!({ "flowId": FLOW, "deliveryKey": KEY, "include": [] })).await;
    assert!(state.get("attempts").is_none() && state["record"].is_object());
    let refused = session.call("delivery_record", json!({ "flowId": FLOW, "deliveryKey": KEY, "include": ["cache"] })).await;
    assert!(refused.starts_with("Error: include names 'cache'"), "{refused}");
}

#[tokio::test(flavor = "multi_thread")]
async fn an_unknown_record_and_a_wrong_id_are_answered_in_words() {
    let estate = Estate::start(vec![]).await;
    let mut session = Session::open(&estate).await;

    // A pipeline id is not a flow id, and a name is neither: refused before the control plane is asked.
    let refused = session.call("delivery_record", json!({ "flowId": "wells", "deliveryKey": KEY })).await;
    assert!(refused.starts_with("Error: flowId 'wells' is not a GUID."), "{refused}");
    assert!(refused.contains("NOT the pipeline id"), "{refused}");
    assert!(estate.calls().is_empty());

    // A record the ledger does not hold is the control plane's own answer, not a trace of failed parts.
    let missing = session.call("delivery_record", json!({ "flowId": FLOW, "deliveryKey": KEY })).await;
    assert!(missing.starts_with("Error: ") && missing.contains("404"), "{missing}");
    assert_eq!(estate.calls().len(), 1, "nothing else is read once the record is unknown");
}

// ---- Flows ------------------------------------------------------------------------------------------------------------

#[tokio::test(flavor = "multi_thread")]
async fn a_flow_is_read_at_a_glance_and_a_part_that_fails_says_so_in_its_place() {
    let base = format!("/api/v1/delivery/flows/{PIPELINE}");
    let estate = Estate::start(vec![
        (route(format!("GET {base}/stats")), 200, json!({ "pipelineId": PIPELINE, "flowName": "wells", "total": 10, "failed": 2 })),
        (route(format!("GET {base}/interfaces")), 200, json!([{ "interface": "wellbores", "mapping": "wellbore@1.0.0" }])),
        (
            route(format!("GET {base}/target")),
            409,
            json!({ "title": "Interface needed", "detail": "Name one of the source's interfaces: wellbores, welllogs." }),
        ),
    ])
    .await;
    let mut session = Session::open(&estate).await;

    let flow = session.json("delivery_flow", json!({ "pipelineId": PIPELINE, "partition": "dev" })).await;

    assert_eq!(flow["stats"]["failed"], json!(2));
    assert_eq!(flow["interfaces"][0]["mapping"], json!("wellbore@1.0.0"));
    assert!(flow["target"]["error"].as_str().is_some_and(|e| e.contains("Name one of the source's interfaces")), "{flow}");
    assert_eq!(flow["links"]["page"], json!(format!("https://delivery.example.com/pipelines/{PIPELINE}")));
    let calls = estate.calls();
    assert!(calls.contains(&format!("GET {base}/stats?partition=dev")), "{calls:?}");
    assert!(calls.contains(&format!("GET {base}/interfaces?partition=dev")), "{calls:?}");

    // A pipeline that is no delivery flow is the tool's answer.
    let other = "99999999-2222-4333-8444-555555555555";
    let refused = session.call("delivery_flow", json!({ "pipelineId": other })).await;
    assert!(refused.starts_with("Error: ") && refused.contains("404"), "{refused}");
}

#[tokio::test(flavor = "multi_thread")]
async fn a_flows_records_are_listed_by_the_filter_given() {
    let path = format!("/api/v1/delivery/flows/{PIPELINE}/records");
    let estate = Estate::start(vec![(
        route(format!("GET {path}")),
        200,
        json!({ "items": [{ "deliveryKey": KEY, "flowId": FLOW, "status": "held", "lastError": "attempts used up" }], "total": 1 }),
    )])
    .await;
    let mut session = Session::open(&estate).await;

    let page = session
        .json(
            "delivery_flow_records",
            json!({ "pipelineId": PIPELINE, "status": "held", "deliveredBy": SUBMISSION, "drifted": false, "interface": "wellbores", "pageSize": 5 }),
        )
        .await;

    assert_eq!(
        estate.only("GET", &path).query,
        format!("status=held&deliveredBy={SUBMISSION}&interface=wellbores&pageSize=5")
    );
    assert_eq!(page["items"][0]["lastError"], json!("attempts used up"));
    assert_eq!(page["items"][0]["links"]["page"], json!(format!("https://delivery.example.com/delivery/records/{FLOW}/{KEY}")));

    let refused = session.call("delivery_flow_records", json!({ "pipelineId": PIPELINE, "runId": "last night" })).await;
    assert!(refused.starts_with("Error: runId 'last night' is not a GUID."), "{refused}");
}

#[tokio::test(flavor = "multi_thread")]
async fn submissions_are_listed_for_a_flow_or_read_one_with_its_parts() {
    let listing = format!("/api/v1/delivery/flows/{PIPELINE}/submissions");
    let one = format!("/api/v1/delivery/submissions/{SUBMISSION}");
    let estate = Estate::start(vec![
        (route(format!("GET {listing}")), 200, json!([{ "submissionId": SUBMISSION, "flowId": FLOW, "receivedUtc": "2026-10-01T00:00:00Z", "planned": 10 }])),
        (route(format!("GET {one}")), 200, json!({ "submission": { "submissionId": SUBMISSION, "planned": 10 }, "runIds": ["run-1"] })),
        (route(format!("GET {one}/batches")), 200, json!({ "items": [{ "submissionId": SUBMISSION, "index": 0, "status": "failed" }], "total": 1 })),
    ])
    .await;
    let mut session = Session::open(&estate).await;

    let listed = session.json("delivery_submissions", json!({ "pipelineId": PIPELINE })).await;
    assert_eq!(listed["items"][0]["planned"], json!(10));
    assert_eq!(
        listed["items"][0]["links"]["page"],
        json!(format!("https://delivery.example.com/delivery/submissions/{SUBMISSION}"))
    );
    assert_eq!(estate.only("GET", &listing).query, "max=25");

    let read = session
        .json("delivery_submissions", json!({ "submissionId": SUBMISSION, "include": ["batches"], "batchStatus": "failed" }))
        .await;
    assert_eq!(read["submission"]["runIds"], json!(["run-1"]));
    assert_eq!(read["batches"]["items"][0]["status"], json!("failed"));
    assert_eq!(estate.only("GET", &format!("{one}/batches")).query, "status=failed&pageSize=200");
    assert!(read.get("attempts").is_none(), "only what was asked for is read");

    let neither = session.call("delivery_submissions", json!({})).await;
    assert!(neither.starts_with("Error: Give pipelineId to list"), "{neither}");
}

#[tokio::test(flavor = "multi_thread")]
async fn the_audit_trail_is_filtered_and_one_activity_is_read_whole() {
    let estate = Estate::start(vec![
        (
            "GET /api/v1/delivery/activities",
            200,
            json!({ "items": [{ "activityId": 4, "flowId": FLOW, "kind": "delete", "actor": "user:alice", "outcome": "completed", "log": "y".repeat(900) }], "total": 1 }),
        ),
        ("GET /api/v1/delivery/activities/4", 200, json!({ "activityId": 4, "flowId": FLOW, "kind": "delete", "actor": "user:alice", "log": "y".repeat(900) })),
    ])
    .await;
    let mut session = Session::open(&estate).await;

    let trail = session
        .json(
            "delivery_activities",
            json!({ "kind": "delete", "actor": "user:alice", "idle": false, "since": "2026-10-01T00:00:00Z", "partition": "prod" }),
        )
        .await;
    assert_eq!(
        estate.only("GET", "/api/v1/delivery/activities").query,
        "partition=prod&kind=delete&actor=user%3Aalice&idle=false&since=2026-10-01T00%3A00%3A00Z&pageSize=25"
    );
    assert!(trail["items"][0]["log"].as_str().is_some_and(|log| log.contains("[truncated: 900 characters in all]")));
    assert_eq!(trail["items"][0]["links"]["page"], json!("https://delivery.example.com/delivery/activity"));

    let one = session.json("delivery_activities", json!({ "activityId": 4 })).await;
    assert_eq!(one["log"].as_str().map(str::len), Some(900), "one activity carries its log whole");
}

// ---- Documents --------------------------------------------------------------------------------------------------------

#[tokio::test(flavor = "multi_thread")]
async fn a_mapping_is_checked_against_its_template_without_reading_a_row() {
    let estate = Estate::start(vec![
        (
            "POST /api/v1/delivery/mapping-builder/coverage",
            200,
            json!({
                "kind": "osdu:wks:master-data--Wellbore:1.5.1", "version": "1.5.1",
                "variables": [
                    { "target": "osdu.data.FacilityName", "state": "Always", "direct": true, "required": true },
                    { "target": "osdu.data.NameAliases", "state": "Sometimes", "direct": true, "required": false },
                    { "target": "osdu.data.WellID", "state": "Empty", "direct": false, "required": true },
                    { "target": "osdu.data", "state": "Always", "direct": false, "required": true }
                ],
                "issues": [{ "severity": "warning", "message": "osdu.data.WellID is required and nothing fills it.", "target": "osdu.data.WellID" }]
            }),
        ),
        ("POST /api/v1/delivery/mapping-builder/shape", 200, json!({ "record": { "data": { "FacilityName": "{name}" } }, "parameters": [], "notes": [], "issues": [] })),
    ])
    .await;
    let mut session = Session::open(&estate).await;
    let yaml = "documentType: mapping\nname: wellbore\n";

    let check = session.json("delivery_check_mapping", json!({ "yaml": yaml, "shape": true, "parameters": { "system": "welldb" } })).await;

    assert_eq!(check["loads"], json!(true));
    assert_eq!(check["valid"], json!(true), "a warning does not make a mapping invalid");
    assert_eq!(check["requiredAndEmpty"], json!(["osdu.data.WellID"]));
    assert_eq!(check["filledOnEveryRow"], json!(["osdu.data.FacilityName"]));
    assert_eq!(check["filledOnSomeRows"], json!(["osdu.data.NameAliases"]));
    assert_eq!(check["templateVariables"], json!(4));
    assert_eq!(check["shape"]["record"]["data"]["FacilityName"], json!("{name}"));
    assert_eq!(estate.only("POST", "/api/v1/delivery/mapping-builder/coverage").body["yaml"], json!(yaml));
    assert_eq!(
        estate.only("POST", "/api/v1/delivery/mapping-builder/shape").body["parameters"],
        json!({ "system": "welldb" })
    );
    // Only the two checks of the document: no flow, no row, no cache is asked for.
    assert_eq!(estate.calls().len(), 2);

    let empty = session.call("delivery_check_mapping", json!({ "yaml": "  " })).await;
    assert!(empty.starts_with("Error: Give the mapping document's YAML text."), "{empty}");
}

#[tokio::test(flavor = "multi_thread")]
async fn a_document_that_does_not_load_is_said_not_to() {
    let estate = Estate::start(vec![(
        "POST /api/v1/delivery/mapping-builder/coverage",
        200,
        json!({ "kind": null, "version": null, "variables": [], "issues": [{ "severity": "error", "message": "mapping.yaml: unknown key 'recrod'." }] }),
    )])
    .await;
    let mut session = Session::open(&estate).await;

    let check = session.json("delivery_check_mapping", json!({ "yaml": "recrod: {}" })).await;
    assert_eq!(check["loads"], json!(false));
    assert_eq!(check["valid"], json!(false));
    assert_eq!(check["issues"][0]["message"], json!("mapping.yaml: unknown key 'recrod'."));
    assert!(check.get("shape").is_none());
}

#[tokio::test(flavor = "multi_thread")]
async fn a_scaffold_sends_the_builders_draft_back_exactly_as_it_came() {
    // The draft carries fields this server's link rules would recognise; none may be added to it on the way back.
    let draft = json!({
        "name": "wellbore", "version": "1.0.0", "templateKind": "osdu:wks:master-data--Wellbore:1.5.1", "templateVersion": "2f8a99cb38d32480",
        "entries": [{ "target": "osdu.data.FacilityName", "runId": "not-a-run", "pipelineId": "not-a-pipeline", "flowName": "x" }]
    });
    let estate = Estate::start(vec![
        ("POST /api/v1/delivery/mapping-builder/draft", 200, draft.clone()),
        (
            "POST /api/v1/delivery/mapping-builder/compose",
            200,
            json!({ "yaml": "documentType: mapping\nname: wellbore\n", "valid": false, "issues": [{ "severity": "error", "message": "osdu.data.FacilityName has no source column." }] }),
        ),
    ])
    .await;
    let mut session = Session::open(&estate).await;

    let scaffold = session
        .json(
            "delivery_scaffold_mapping",
            json!({ "repoId": REPO, "templatePath": " welldb/templates/osdu_wks_master-data--Wellbore_1.5.1.json ", "name": "wellbore", "mappingVersion": "1.0.0", "system": "welldb", "partition": "dev" }),
        )
        .await;

    assert_eq!(
        estate.only("POST", "/api/v1/delivery/mapping-builder/draft").body,
        json!({ "scope": "dev", "repoId": REPO, "templatePath": "welldb/templates/osdu_wks_master-data--Wellbore_1.5.1.json", "name": "wellbore", "mappingVersion": "1.0.0", "system": "welldb" })
    );
    // The template is named as the builder read it from the file, with the file it was read from.
    assert_eq!(
        scaffold["template"],
        json!({ "kind": "osdu:wks:master-data--Wellbore:1.5.1", "version": "2f8a99cb38d32480", "repoId": REPO, "path": "welldb/templates/osdu_wks_master-data--Wellbore_1.5.1.json" })
    );
    let composed = estate.only("POST", "/api/v1/delivery/mapping-builder/compose").body;
    assert_eq!(composed["draft"], draft, "the draft went back unchanged");
    assert_eq!(composed["scope"], json!("dev"));
    assert_eq!(scaffold["valid"], json!(false));
    assert!(scaffold["yaml"].as_str().is_some_and(|yaml| yaml.starts_with("documentType: mapping")));
    assert_eq!(scaffold["nextSteps"].as_array().map(Vec::len), Some(3));

    let refused = session
        .call("delivery_scaffold_mapping", json!({ "repoId": REPO, "templatePath": "t.json", "name": " ", "mappingVersion": "1.0.0", "system": "s" }))
        .await;
    assert_eq!(refused, "Error: name is required to scaffold a mapping.");
    let unnamed = session
        .call("delivery_scaffold_mapping", json!({ "repoId": " ", "templatePath": "t.json", "name": "n", "mappingVersion": "1.0.0", "system": "s" }))
        .await;
    assert!(unnamed.starts_with("Error: repoId is required"), "{unnamed}");
    let malformed = session
        .call("delivery_scaffold_mapping", json!({ "repoId": "welldb", "templatePath": "t.json", "name": "n", "mappingVersion": "1.0.0", "system": "s" }))
        .await;
    assert!(malformed.starts_with("Error:") && malformed.contains("repoId"), "{malformed}");
}

#[tokio::test(flavor = "multi_thread")]
async fn a_template_is_narrowed_to_the_variables_asked_about() {
    let estate = Estate::start(vec![(
        "GET /api/v1/delivery/templates/detail",
        200,
        json!({
            "kind": "osdu:wks:master-data--Wellbore:1.5.1", "version": "1.5.1",
            "variables": [
                { "path": "osdu.data.FacilityName", "type": "string", "required": true },
                { "path": "osdu.data.VerticalMeasurements[].VerticalMeasurement", "type": "number", "required": false },
                { "path": "osdu.data.VerticalMeasurements[].VerticalMeasurementPathID", "type": "string", "required": true }
            ]
        }),
    )])
    .await;
    let mut session = Session::open(&estate).await;

    let template = session
        .json(
            "delivery_templates",
            json!({ "repoId": REPO, "path": "welldb/templates/osdu_wks_master-data--Wellbore_1.5.1.json", "search": "verticalmeasurement", "requiredOnly": true, "partition": "dev" }),
        )
        .await;

    assert_eq!(
        estate.only("GET", "/api/v1/delivery/templates/detail").query,
        format!("repoId={REPO}&path=welldb%2Ftemplates%2Fosdu_wks_master-data--Wellbore_1.5.1.json&scope=dev")
    );
    assert_eq!(template["variablesInTemplate"], json!(3));
    assert_eq!(template["variablesShown"], json!(1));
    assert_eq!(template["variables"][0]["path"], json!("osdu.data.VerticalMeasurements[].VerticalMeasurementPathID"));
    assert_eq!(
        template["links"]["page"],
        json!(format!("https://delivery.example.com/delivery/templates?repoId={REPO}&path=welldb%2Ftemplates%2Fosdu_wks_master-data--Wellbore_1.5.1.json"))
    );

    let half = session.call("delivery_templates", json!({ "repoId": REPO })).await;
    assert!(half.starts_with("Error: Give both repoId and path"), "{half}");
    let pathless = session.call("delivery_templates", json!({ "path": "welldb/templates/x.json" })).await;
    assert!(pathless.starts_with("Error: Give both repoId and path"), "{pathless}");
}

#[tokio::test(flavor = "multi_thread")]
async fn a_schema_comparison_returns_the_changes_and_not_the_files() {
    let estate = Estate::start(vec![(
        "GET /api/v1/delivery/templates/osdu/compare",
        200,
        json!({
            "from": { "kind": "osdu:wks:work-product-component--WellLog:1.4.0", "fileText": "{ a very long schema }" },
            "to": { "kind": "osdu:wks:work-product-component--WellLog:1.5.0", "fileText": "{ another very long schema }" },
            "breaking": 1, "additive": 3, "wording": 0,
            "changes": [{ "path": "osdu.data.Curves[].CurveID", "change": "Changed", "impact": "Breaking" }],
            "referencedFiles": [{ "name": "AbstractCommonResources", "fromText": "long", "toText": "longer" }]
        }),
    )])
    .await;
    let mut session = Session::open(&estate).await;

    let comparison = session
        .json(
            "delivery_osdu_schema_compare",
            json!({ "fromRelease": "0.27", "fromKind": "osdu:wks:work-product-component--WellLog:1.4.0", "toRelease": "0.28", "toKind": "osdu:wks:work-product-component--WellLog:1.5.0" }),
        )
        .await;
    assert_eq!(comparison["breaking"], json!(1));
    assert_eq!(comparison["changes"][0]["impact"], json!("Breaking"));
    assert!(!comparison.to_string().contains("long schema") && !comparison.to_string().contains("longer"));
    assert_eq!(comparison["referencedFiles"][0]["name"], json!("AbstractCommonResources"));
}

// ---- The cache, as metadata --------------------------------------------------------------------------------------------

#[tokio::test(flavor = "multi_thread")]
async fn a_cache_change_is_returned_without_the_values_that_changed() {
    let estate = Estate::start(vec![(
        "GET /api/v1/delivery/cache/tags",
        200,
        json!({
            "items": [{
                "tagId": 31, "scope": "dev", "typeName": "wellbore", "itemId": "dev:master-data--Wellbore:W-1", "path": "data.FacilityName",
                "change": "changed", "oldValue": "Well One", "newValue": "Well 1H", "summary": "FacilityName went from Well One to Well 1H",
                "status": "pending", "affectedRecords": 40, "processed": 0, "waiting": 40,
                "waitingFlows": [{ "flowId": FLOW, "pipelineId": PIPELINE, "flowName": "welllogs", "records": 40 }]
            }],
            "page": 1, "pageSize": 25, "total": 1
        }),
    )])
    .await;
    let mut session = Session::open(&estate).await;

    let changes = session.json("delivery_cache_changes", json!({ "status": "pending", "partition": "dev" })).await;

    assert_eq!(estate.only("GET", "/api/v1/delivery/cache/tags").query, "status=pending&scope=dev&pageSize=25");
    let change = &changes["items"][0];
    assert_eq!(change["tagId"], json!(31));
    assert_eq!(change["path"], json!("data.FacilityName"));
    assert_eq!(change["affectedRecords"], json!(40));
    assert_eq!(change["waitingFlows"][0]["records"], json!(40));
    let text = changes.to_string();
    for data in ["Well One", "Well 1H", "oldValue", "newValue"] {
        assert!(!text.contains(data), "the answer carries {data}: {text}");
    }
    assert_eq!(changes["links"]["page"], json!("https://delivery.example.com/delivery/cache?partition=dev&tab=deliveries"));
}

#[tokio::test(flavor = "multi_thread")]
async fn a_cache_is_read_as_what_it_is_made_of_and_how_it_stands() {
    let estate = Estate::start(vec![
        ("GET /api/v1/delivery/caches", 200, json!([{ "scope": "dev", "versions": 12, "types": [{ "name": "wellbore", "items": 900 }] }])),
        (
            "GET /api/v1/delivery/cache/streams",
            200,
            json!({ "partition": "dev", "currentVersion": "v12", "types": [{ "type": "wellbore", "state": "failed", "stateReason": "the last refresh failed" }], "notices": [] }),
        ),
        ("GET /api/v1/delivery/cache/versions", 200, json!([{ "scope": "dev", "version": "v12", "current": true, "items": 900 }])),
        ("GET /api/v1/delivery/cache/history", 200, json!([{ "version": { "scope": "dev", "version": "v12" }, "changed": 3, "added": 1, "removed": 0, "types": [] }])),
    ])
    .await;
    let mut session = Session::open(&estate).await;

    let every = session.json("delivery_caches", json!({})).await;
    assert_eq!(every["items"][0]["types"][0]["items"], json!(900));
    assert_eq!(every["items"][0]["links"]["page"], json!("https://delivery.example.com/delivery/cache?partition=dev"));

    let health = session.json("delivery_caches", json!({ "partition": "dev" })).await;
    assert_eq!(health["types"][0]["state"], json!("failed"));
    assert_eq!(estate.only("GET", "/api/v1/delivery/cache/streams").query, "partition=dev");

    let versions = session.json("delivery_cache_versions", json!({ "partition": "dev" })).await;
    assert_eq!(versions["items"][0]["version"], json!("v12"));
    assert_eq!(estate.only("GET", "/api/v1/delivery/cache/versions").query, "scope=dev");

    let history = session.json("delivery_cache_versions", json!({ "partition": "dev", "history": true, "type": "wellbore" })).await;
    assert_eq!(history["items"][0]["changed"], json!(3));
    assert_eq!(estate.only("GET", "/api/v1/delivery/cache/history").query, "scope=dev&type=wellbore");

    // No tool of this server reads the cached records themselves, or what differs between two versions of them.
    let calls = estate.calls();
    assert!(!calls.iter().any(|call| call.contains("/cache/items") || call.contains("/cache/diff") || call.contains("/cache/gaps")), "{calls:?}");
}

#[tokio::test(flavor = "multi_thread")]
async fn configuration_is_listed_as_set_or_resolved_for_a_repository() {
    let repo = "33333333-4444-4555-8666-777777777777";
    let effective = format!("/api/v1/delivery/config/effective/{repo}");
    let estate = Estate::start(vec![
        ("GET /api/v1/delivery/config", 200, json!([{ "name": "OSDU_URL", "value": "${keyvault:osdu-url}", "partition": null }])),
        (route(format!("GET {effective}")), 200, json!({ "OSDU_URL": "${keyvault:osdu-url}", "OSDU_DATA_PARTITION": "prod" })),
        ("GET /api/v1/delivery/partitions", 200, json!([{ "name": "prod", "registered": true, "isDefault": false, "pendingChanges": 2 }])),
    ])
    .await;
    let mut session = Session::open(&estate).await;

    let set = session.json("delivery_config", json!({})).await;
    assert_eq!(set[0]["value"], json!("${keyvault:osdu-url}"), "a value is a reference, shown as written");

    let resolved = session.json("delivery_config", json!({ "repoId": repo, "partition": "prod" })).await;
    assert_eq!(resolved["OSDU_DATA_PARTITION"], json!("prod"));
    assert_eq!(estate.only("GET", &effective).query, "partition=prod");

    let partitions = session.json("delivery_partitions", json!({})).await;
    assert_eq!(partitions["items"][0]["pendingChanges"], json!(2));
    assert_eq!(partitions["items"][0]["links"]["cache"], json!("https://delivery.example.com/delivery/cache?partition=prod"));
    assert_eq!(partitions["links"]["page"], json!("https://delivery.example.com/delivery/partitions"));
}

// ---- Assertions and dimensions ------------------------------------------------------------------------------------------

#[tokio::test(flavor = "multi_thread")]
async fn an_assertion_report_counts_its_examples_and_returns_none_of_them() {
    let estate = Estate::start(vec![
        (
            "GET /api/v1/delivery/assertion-runs/42",
            200,
            json!({
                "run": { "assertionRunId": 42, "flowId": FLOW, "status": "failed", "failed": 1 },
                "pipelineId": PIPELINE,
                "results": [{
                    "test": "wellbore-names", "outcome": "failed", "matched": 900, "evaluated": 900,
                    "assertions": [{
                        "index": 0, "label": "has a name", "outcome": "failed", "expected": "FacilityName is not empty", "checked": 900, "failing": 2,
                        "examples": [
                            { "id": "dev:master-data--Wellbore:W-1", "value": "a confidential value", "reason": "empty" },
                            { "id": "dev:master-data--Wellbore:W-2", "value": "another value", "reason": "empty" }
                        ]
                    }]
                }]
            }),
        ),
        (route(format!("GET /api/v1/delivery/flows/{PIPELINE}/assertion-matrix")), 200, json!({ "runs": [], "tests": [] })),
    ])
    .await;
    let mut session = Session::open(&estate).await;

    let report = session.json("delivery_assertion_runs", json!({ "assertionRunId": 42 })).await;
    let assertion = &report["results"][0]["assertions"][0];
    assert_eq!(assertion["failing"], json!(2));
    assert_eq!(assertion["exampleCount"], json!(2));
    assert!(assertion.get("examples").is_none());
    let text = report.to_string();
    for data in ["a confidential value", "another value", "Wellbore:W-1"] {
        assert!(!text.contains(data), "the report carries {data}");
    }
    assert_eq!(report["links"]["page"], json!("https://delivery.example.com/delivery/assertions/runs/42"));

    session.json("delivery_assertion_runs", json!({ "pipelineId": PIPELINE, "view": "matrix", "partition": "dev" })).await;
    assert_eq!(
        estate.only("GET", &format!("/api/v1/delivery/flows/{PIPELINE}/assertion-matrix")).query,
        "partition=dev&runs=20"
    );
    let refused = session.call("delivery_assertion_runs", json!({ "pipelineId": PIPELINE, "view": "table" })).await;
    assert_eq!(refused, "Error: view 'table' is not one of runs, matrix.");
}

#[tokio::test(flavor = "multi_thread")]
async fn a_dimension_is_read_as_its_definition_and_its_builds_never_its_values() {
    let estate = Estate::start(vec![
        ("GET /api/v1/delivery/dimensions", 200, json!({ "partition": "dev", "totals": { "dimensions": 1 }, "flows": [{ "name": "dims", "dimensions": [{ "dimensionId": 3, "name": "Operator", "values": 40, "keys": 55 }] }] })),
        ("GET /api/v1/delivery/dimensions/3", 200, json!({ "flowName": "dims", "pipelineId": PIPELINE, "dimension": { "dimensionId": 3, "name": "Operator", "values": 40 } })),
        ("GET /api/v1/delivery/dimensions/3/builds", 200, json!([{ "buildId": 8, "dimensionId": 3, "status": "failed", "error": "search timed out" }])),
    ])
    .await;
    let mut session = Session::open(&estate).await;

    let board = session.json("delivery_dimensions", json!({ "partition": "dev" })).await;
    assert_eq!(board["flows"][0]["dimensions"][0]["values"], json!(40), "how many values, not which");
    assert_eq!(
        board["flows"][0]["dimensions"][0]["links"]["page"],
        json!("https://delivery.example.com/delivery/dimensions?d=3")
    );

    let one = session.json("delivery_dimensions", json!({ "dimensionId": 3, "maxBuilds": 5 })).await;
    assert_eq!(one["builds"][0]["error"], json!("search timed out"));
    assert_eq!(estate.only("GET", "/api/v1/delivery/dimensions/3/builds").query, "max=5");

    let calls = estate.calls();
    assert!(
        !calls.iter().any(|call| call.contains("/values") || call.contains("/keys") || call.contains("/table") || call.contains("/export")),
        "{calls:?}"
    );
}

// ---- The operator's actions ----------------------------------------------------------------------------------------------

#[tokio::test(flavor = "multi_thread")]
async fn records_are_released_one_by_key_or_all_held_and_never_by_omission() {
    let flow = format!("/api/v1/delivery/flows/{PIPELINE}/release");
    let record = format!("/api/v1/delivery/records/{FLOW}/{KEY}/release");
    let estate = Estate::start(vec![
        (route(format!("POST {flow}")), 200, json!({ "released": 2 })),
        (route(format!("POST {record}")), 200, json!({ "released": 1 })),
    ])
    .await;
    let mut session = Session::open(&estate).await;

    let one = session.json("delivery_release_records", json!({ "flowId": FLOW, "deliveryKey": KEY })).await;
    assert_eq!(one["released"], json!(1));

    // Leaving the keys out does not mean every held record: that has to be said.
    let unsaid = session.call("delivery_release_records", json!({ "pipelineId": PIPELINE })).await;
    assert!(unsaid.starts_with("Error: Name the delivery keys to release, or say allHeld: true"), "{unsaid}");
    assert_eq!(estate.calls().len(), 1);

    session.json("delivery_release_records", json!({ "pipelineId": PIPELINE, "keys": [KEY], "interface": "well logs", "partition": "dev" })).await;
    let by_key = estate.seen().into_iter().last().expect("a request");
    assert_eq!((by_key.path.as_str(), by_key.query.as_str()), (flow.as_str(), "interface=well%20logs&partition=dev"));
    assert_eq!(by_key.body, json!({ "keys": [KEY] }));

    session.json("delivery_release_records", json!({ "pipelineId": PIPELINE, "allHeld": true })).await;
    assert_eq!(estate.seen().into_iter().last().expect("a request").body, json!({}));

    let both = session.call("delivery_release_records", json!({ "pipelineId": PIPELINE, "keys": [KEY], "allHeld": true })).await;
    assert_eq!(both, "Error: Give keys or allHeld, not both.");
}

#[tokio::test(flavor = "multi_thread")]
async fn a_redelivery_a_verify_and_a_sync_go_through_the_ledgers_own_endpoints() {
    let record = format!("/api/v1/delivery/records/{FLOW}/{KEY}");
    let sync = format!("/api/v1/delivery/flows/{PIPELINE}/sync");
    let estate = Estate::start(vec![
        (route(format!("POST {record}/redeliver")), 202, json!({ "marked": 1, "runId": "run-7" })),
        (route(format!("POST {record}/verify")), 202, json!({ "runId": "run-8", "status": "queued" })),
        (route(format!("POST {record}/sync")), 202, json!({ "runId": "run-9", "status": "queued" })),
        (route(format!("POST {sync}")), 202, json!({ "runId": "run-10", "status": "queued" })),
    ])
    .await;
    let mut session = Session::open(&estate).await;

    let sent = session.json("delivery_redeliver_record", json!({ "flowId": FLOW, "deliveryKey": KEY, "scope": "record" })).await;
    assert_eq!(sent["marked"], json!(1));
    assert_eq!(sent["links"]["page"], json!("https://delivery.example.com/runs/run-7"), "the run it queued is linked");
    assert_eq!(
        estate.only("POST", &format!("{record}/redeliver")).body,
        json!({ "scope": "record", "run": true, "pool": null })
    );

    let verified = session.json("delivery_verify_record", json!({ "flowId": FLOW, "deliveryKey": KEY })).await;
    assert_eq!(verified["runId"], json!("run-8"));

    session.json("delivery_sync_with_source", json!({ "flowId": FLOW, "deliveryKey": KEY })).await;
    estate.only("POST", &format!("{record}/sync"));

    // A sync by filter has to say how many records the filter matched when it was looked at.
    let unguarded = session
        .call("delivery_sync_with_source", json!({ "pipelineId": PIPELINE, "filter": { "status": "delivered" } }))
        .await;
    assert!(unguarded.starts_with("Error: A sync by filter needs `expected`"), "{unguarded}");

    session
        .json(
            "delivery_sync_with_source",
            json!({ "pipelineId": PIPELINE, "filter": { "status": "delivered", "deliveredBy": SUBMISSION }, "expected": 12 }),
        )
        .await;
    let by_filter = estate.only("POST", &sync).body;
    assert_eq!(by_filter["expected"], json!(12));
    assert_eq!(by_filter["filter"]["deliveredBy"], json!(SUBMISSION));
    assert!(by_filter.get("keys").is_none());

    // Every one of them carried the caller's own credential.
    assert!(estate.seen().iter().all(|request| request.authorization == "Bearer operator-token"));
}

#[tokio::test(flavor = "multi_thread")]
async fn a_cache_decision_and_a_probe_say_what_they_did() {
    let probe = format!("/api/v1/delivery/flows/{PIPELINE}/probe");
    let estate = Estate::start(vec![
        ("POST /api/v1/delivery/cache/tags/decide", 200, json!({ "decided": 2, "approved": true })),
        (
            route(format!("POST {probe}")),
            200,
            json!({ "flow": "welldb-welllog-03-header-delivery", "reachable": false, "status": 401, "detail": "Unauthorized", "path": "/api/storage/v2/info" }),
        ),
    ])
    .await;
    let mut session = Session::open(&estate).await;

    let decided = session.json("delivery_decide_cache_changes", json!({ "tagIds": [31, 32], "approve": true })).await;
    assert_eq!(decided["decided"], json!(2));
    assert_eq!(estate.only("POST", "/api/v1/delivery/cache/tags/decide").body, json!({ "tagIds": [31, 32], "approve": true }));
    let none = session.call("delivery_decide_cache_changes", json!({ "tagIds": [], "approve": true })).await;
    assert!(none.starts_with("Error: Name at least one tag"), "{none}");

    // The probe is the control plane's own answer: nothing is queued, and no task is polled.
    let probed = session.json("delivery_probe_target", json!({ "pipelineId": PIPELINE, "partition": "dev" })).await;
    assert_eq!(probed["reachable"], json!(false));
    assert_eq!(probed["status"], json!(401));
    assert_eq!(estate.only("POST", &probe).query, "partition=dev");
    assert!(estate.seen().iter().all(|request| !request.path.starts_with("/api/v1/datasources/tasks")));
}

#[tokio::test(flavor = "multi_thread")]
async fn no_tool_of_the_server_reads_or_removes_what_osdu_holds() {
    let estate = Estate::start(vec![]).await;
    let mut session = Session::open(&estate).await;
    let listed = names(&session.tools().await);

    // The endpoints that return data, or take records away, have no tool here. Named, so that adding one is a
    // decision made against this list and not an oversight.
    for absent in [
        "delivery_read_osdu", "delivery_read_source_row", "delivery_preview", "delivery_check_values", "delivery_scope_values",
        "delivery_cache_items", "delivery_cache_diff", "delivery_cache_gaps",
        "delivery_dimension_values", "delivery_dimension_keys", "delivery_dimension_table", "delivery_dimension_search",
        "delivery_remove_records", "delivery_preview_removal",
        "delivery_explore", "delivery_explore_types", "delivery_explore_search", "delivery_explore_fields", "delivery_explore_read",
    ] {
        assert!(!listed.contains(absent), "{absent} is offered");
    }

    // And no source of this crate names the routes behind them.
    let sources = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("src/tools");
    let data_routes = [
        "/read\"", "/source\"", "/preview", "/scope-values", "/check-values", "/osdu/read", "/osdu/validate", "/cache/items", "/cache/diff",
        "/cache/gaps", "/values", "/keys\"", "/table\"", "/export", "/dimensions/search", "/filter\"", "records/remove", "/delete\"",
        "/ledger/prune", "/explorer/",
    ];
    for entry in std::fs::read_dir(&sources).expect("the tools folder") {
        let path = entry.expect("an entry").path();
        let text = std::fs::read_to_string(&path).expect("a source file");
        // The tests of a source file may name a route to say it is not called; the code above them may not.
        let code = text.split("#[cfg(test)]").next().unwrap_or_default();
        for route in data_routes {
            assert!(!code.contains(route), "{} calls a route that returns or removes data: {route}", path.display());
        }
    }
}
