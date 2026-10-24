//! Assertion flows: the tests an estate runs against what OSDU holds, how they stand, and what each run found, as
//! outcomes and counts. The records a test quotes as examples are counted here, never returned.

use serde::Deserialize;
use serde_json::{json, Map, Value};
use sqlflow_mcp::rmcp::handler::server::wrapper::Parameters;
use sqlflow_mcp::rmcp::{self, schemars, tool, tool_router};
use sqlflow_mcp::{json_str, GuiLinks};

use super::{DeliveryTools, PIPELINE_HINT};
use crate::links::flow_page;
use crate::support::{count_instead, optional_guid, put_links, refuse, relinked, text, Query};

#[derive(Debug, Deserialize, schemars::JsonSchema)]
pub struct AssertionsInput {
    /// One assertion flow's board: its pipeline id, `id` in list_pipelines(kind="assertion"). Omit for every flow.
    #[serde(rename = "pipelineId")]
    pub pipeline_id: Option<String>,
    /// The partition to read the board in. Omit for the one a run of the flow would test.
    pub partition: Option<String>,
}

#[derive(Debug, Deserialize, schemars::JsonSchema)]
pub struct AssertionRunsInput {
    /// Read ONE run's report: the `assertionRunId` of a listed run. Every other argument is ignored.
    #[serde(rename = "assertionRunId")]
    pub assertion_run_id: Option<i64>,
    /// List one assertion flow's runs: its pipeline id.
    #[serde(rename = "pipelineId")]
    pub pipeline_id: Option<String>,
    /// With pipelineId: the partition. Omit for the one a run of the flow would test.
    pub partition: Option<String>,
    /// With pipelineId: "runs" (default) or "matrix" (each test's outcome in each recent run).
    pub view: Option<String>,
    /// With pipelineId: most runs (default 20; max 500 for runs, 200 for the matrix).
    pub max: Option<i64>,
}

#[tool_router(router = assertions_router, vis = "pub(crate)")]
impl DeliveryTools {
    #[tool(
        description = "The assertion board: the tests an estate runs against what OSDU holds, and how they stand. \
`totals` gives tests passed, failed, warned, errored and not run, with the pass rate. Per test: the OSDU kind and \
query it reads, its assertions (type, severity, what is expected), whether it fits its kind's template, its latest \
outcome with how many records matched and were evaluated, and its outcome over recent runs. Use it for 'are the data \
quality tests passing', 'what is failing in prod', 'which tests never ran'. Omit pipelineId for every assertion flow. \
Tests run by running the assertion flow with trigger_run."
    )]
    async fn delivery_assertions(&self, Parameters(i): Parameters<AssertionsInput>) -> String {
        let pipeline = match optional_guid("pipelineId", i.pipeline_id.as_deref(), PIPELINE_HINT) {
            Ok(pipeline) => pipeline,
            Err(refused) => return refused,
        };
        let partition = text(i.partition);
        let query = Query::new().text("partition", partition.clone());
        let board = match pipeline {
            Some(pipeline) => {
                let links = json!({
                    "page": flow_page(self.ctx.links(), &pipeline, None, partition.as_deref(), &[("tab", "tests")]),
                    "flow": self.ctx.links().pipeline(&pipeline),
                });
                self.ctx
                    .get_about(
                        &format!("/api/v1/delivery/flows/{pipeline}/assertions"),
                        query.pairs(),
                        ("pipelineId", &pipeline),
                        links,
                    )
                    .await
            }
            None => {
                let links = json!({ "page": self.ctx.links().route("/delivery/assertions") });
                self.ctx
                    .get_about("/api/v1/delivery/assertions", query.pairs(), ("listing", "assertions"), links)
                    .await
            }
        };
        relinked(board, |board| link_board_tests(board, self.ctx.links()))
    }

    #[tool(
        description = "Assertion runs and their reports. With pipelineId, lists an assertion flow's runs newest first: \
report number (assertionRunId), status, who ran it, and how many tests passed, failed, warned, errored and were \
skipped; view=\"matrix\" returns each test's outcome in each recent run, which shows when a test started failing. \
With assertionRunId, returns one run's report: per test and per assertion what was expected, what was found, how \
many records were checked and how many failed. Use it for 'what failed in last night's test run', 'when did this \
test start failing'. The records a test quotes as examples are counted (exampleCount), not returned."
    )]
    async fn delivery_assertion_runs(&self, Parameters(i): Parameters<AssertionRunsInput>) -> String {
        if let Some(run) = i.assertion_run_id {
            if run <= 0 {
                return refuse("assertionRunId is the positive report number a listed run carries.");
            }
            return match self.ctx.read(&format!("/api/v1/delivery/assertion-runs/{run}"), &[]).await {
                Ok(mut report) => {
                    count_instead(&mut report, "examples", "exampleCount");
                    link_report_results(&mut report, self.ctx.links());
                    let mut links = Map::new();
                    links.insert("page".to_string(), json!(self.ctx.links().route(&format!("/delivery/assertions/runs/{run}"))));
                    put_links(&mut report, links);
                    json_str(&report)
                }
                Err(error) => refuse(format!("{error:#}")),
            };
        }

        let pipeline = match optional_guid("pipelineId", i.pipeline_id.as_deref(), PIPELINE_HINT) {
            Ok(Some(pipeline)) => pipeline,
            Ok(None) => {
                return refuse(
                    "Give pipelineId to list an assertion flow's runs, or assertionRunId to read one run's report. \
                     Find the flow with list_pipelines(kind=\"assertion\") or delivery_assertions.",
                )
            }
            Err(refused) => return refused,
        };
        let partition = text(i.partition);
        // The flow's Reports tab lists its runs, and its History tab lays its tests against them.
        let links = |tab: &str| {
            json!({
                "page": flow_page(self.ctx.links(), &pipeline, None, partition.as_deref(), &[("tab", tab)]),
                "flow": self.ctx.links().pipeline(&pipeline),
            })
        };
        let view = text(i.view).map(|v| v.to_ascii_lowercase()).unwrap_or_else(|| "runs".to_string());
        match view.as_str() {
            "runs" => {
                let query = Query::new()
                    .text("partition", partition.clone())
                    .value("max", Some(i.max.unwrap_or(20).clamp(1, 500)));
                self.ctx
                    .get_about(
                        &format!("/api/v1/delivery/flows/{pipeline}/assertion-runs"),
                        query.pairs(),
                        ("pipelineId", &pipeline),
                        links("reports"),
                    )
                    .await
            }
            "matrix" => {
                let query = Query::new()
                    .text("partition", partition.clone())
                    .value("runs", Some(i.max.unwrap_or(20).clamp(1, 200)));
                let matrix = self
                    .ctx
                    .get_about(
                        &format!("/api/v1/delivery/flows/{pipeline}/assertion-matrix"),
                        query.pairs(),
                        ("pipelineId", &pipeline),
                        links("history"),
                    )
                    .await;
                relinked(matrix, |matrix| {
                    for row in matrix.get_mut("tests").and_then(Value::as_array_mut).into_iter().flatten() {
                        if let Some(test) = row.get("test").and_then(Value::as_str).map(str::to_string) {
                            link_test(row, self.ctx.links(), &pipeline, partition.as_deref(), &test, None);
                        }
                    }
                })
            }
            other => refuse(format!("view '{other}' is not one of runs, matrix.")),
        }
    }
}

/// Gives a test (or one assertion of it, `check` its index) its sheet on its flow's Tests tab. A test's row names no
/// flow; the board, report or matrix around it does.
fn link_test(row: &mut Value, links: &GuiLinks, pipeline: &str, partition: Option<&str>, test: &str, check: Option<i64>) {
    let index = check.map(|index| index.to_string());
    let mut narrowed = vec![("tab", "tests"), ("test", test)];
    if let Some(index) = index.as_deref() {
        narrowed.push(("check", index));
    }
    let mut page = Map::new();
    page.insert("page".to_string(), json!(flow_page(links, pipeline, None, partition, &narrowed)));
    put_links(row, page);
}

/// Links every test of a board, each under the flow that holds it, in the board's partition.
fn link_board_tests(board: &mut Value, links: &GuiLinks) {
    let partition = board.get("partition").and_then(Value::as_str).map(str::to_string);
    for flow in board.get_mut("flows").and_then(Value::as_array_mut).into_iter().flatten() {
        let Some(pipeline) = flow.get("pipelineId").and_then(Value::as_str).map(str::to_string) else {
            continue;
        };
        for test in flow.get_mut("tests").and_then(Value::as_array_mut).into_iter().flatten() {
            if let Some(name) = test.get("name").and_then(Value::as_str).map(str::to_string) {
                link_test(test, links, &pipeline, partition.as_deref(), &name, None);
            }
        }
    }
}

/// Links every result of a run's report to its test, and every assertion of it to the assertion on the test's sheet,
/// under the flow and in the partition the run tested.
fn link_report_results(report: &mut Value, links: &GuiLinks) {
    let Some(pipeline) = report.get("pipelineId").and_then(Value::as_str).map(str::to_string) else {
        return;
    };
    let partition = report.pointer("/run/partition").and_then(Value::as_str).map(str::to_string);
    for result in report.get_mut("results").and_then(Value::as_array_mut).into_iter().flatten() {
        let Some(test) = result.get("test").and_then(Value::as_str).map(str::to_string) else {
            continue;
        };
        for outcome in result.get_mut("assertions").and_then(Value::as_array_mut).into_iter().flatten() {
            if let Some(index) = outcome.get("index").and_then(Value::as_i64) {
                link_test(outcome, links, &pipeline, partition.as_deref(), &test, Some(index));
            }
        }
        link_test(result, links, &pipeline, partition.as_deref(), &test, None);
    }
}
