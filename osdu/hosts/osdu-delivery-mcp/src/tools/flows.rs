//! A flow's side of the ledger: what it delivered and where to, its records, its submissions, and the audit trail.

use serde::Deserialize;
use serde_json::{json, Map, Value};
use sqlflow_mcp::rmcp::handler::server::wrapper::Parameters;
use sqlflow_mcp::rmcp::{self, schemars, tool, tool_router};

use super::{DeliveryTools, PIPELINE_HINT};
use crate::links::flow_page;
use crate::support::{clip_field, composed, drop_fields, guid, optional_guid, refuse, section, text, Query, LOG_LIMIT, LOG_PREVIEW};

#[derive(Debug, Deserialize, schemars::JsonSchema)]
pub struct FlowInput {
    /// The delivery flow's pipeline id: `id` in list_pipelines(kind="delivery").
    #[serde(rename = "pipelineId")]
    pub pipeline_id: String,
    /// One interface of a source that declares several. Omit to read the source as a whole.
    pub interface: Option<String>,
    /// One partition of a flow that names its partitions. Omit to add them all up.
    pub partition: Option<String>,
}

#[derive(Debug, Deserialize, schemars::JsonSchema)]
pub struct RunLookupInput {
    /// A run id, or the correlation-id a platform's log shows: osdu-delivery-wl-dim-<run id>. A pasted log line works too.
    pub id: String,
}

#[derive(Debug, Deserialize, schemars::JsonSchema)]
pub struct FlowRecordsInput {
    /// The delivery flow's pipeline id: `id` in list_pipelines(kind="delivery").
    #[serde(rename = "pipelineId")]
    pub pipeline_id: String,
    /// Required for a source with several interfaces: records are kept per interface.
    pub interface: Option<String>,
    /// Required for a flow that names several partitions.
    pub partition: Option<String>,
    /// One custody state: pending, delivering, delivered, held, failed, deleted or waiting.
    pub status: Option<String>,
    /// A term over the OSDU id, source key and label.
    pub search: Option<String>,
    /// "prefix" (default, indexed) or "contains" (refused on a listing too broad to scan).
    pub mode: Option<String>,
    /// Records this submission last PLANNED.
    #[serde(rename = "submissionId")]
    pub submission_id: Option<String>,
    /// Records this submission DELIVERED.
    #[serde(rename = "deliveredBy")]
    pub delivered_by: Option<String>,
    /// Records this platform run touched.
    #[serde(rename = "runId")]
    pub run_id: Option<String>,
    /// true keeps only records a verify found drifted.
    pub drifted: Option<bool>,
    pub page: Option<i64>,
    /// Default 25, max 200.
    #[serde(rename = "pageSize")]
    pub page_size: Option<i64>,
}

#[derive(Debug, Deserialize, schemars::JsonSchema)]
pub struct FlowIssuesInput {
    /// The delivery flow's pipeline id: `id` in list_pipelines(kind="delivery").
    #[serde(rename = "pipelineId")]
    pub pipeline_id: String,
    /// Read ONE issue with the files its records came from: the `issue` a listing names.
    pub issue: Option<String>,
    /// Required for a source with several interfaces: issues are kept per interface.
    pub interface: Option<String>,
    /// Required for a flow that names several partitions.
    pub partition: Option<String>,
    /// Most issues, the most records first (default 100).
    pub max: Option<i64>,
}

#[derive(Debug, Deserialize, schemars::JsonSchema)]
pub struct SubmissionsInput {
    /// List a flow's submissions: its pipeline id. Give this OR submissionId.
    #[serde(rename = "pipelineId")]
    pub pipeline_id: Option<String>,
    /// Read one submission: its id from a listing, a record's `lastSubmissionId`, or an activity.
    #[serde(rename = "submissionId")]
    pub submission_id: Option<String>,
    /// With pipelineId: required for a source with several interfaces.
    pub interface: Option<String>,
    /// With pipelineId: required for a flow that names several partitions.
    pub partition: Option<String>,
    /// With submissionId: also read "batches" (work batches and how far each drain got) and/or "attempts".
    pub include: Option<Vec<String>>,
    /// Most submissions (default 25, max 1000), or with include "attempts" most attempts (default 100, max 5000).
    pub max: Option<i64>,
    /// With include "batches": one state, queued, running, done or failed.
    #[serde(rename = "batchStatus")]
    pub batch_status: Option<String>,
}

#[derive(Debug, Deserialize, schemars::JsonSchema)]
pub struct RetrievalsInput {
    /// The retrieval flow's pipeline id: `id` in list_pipelines(kind="retrieval").
    #[serde(rename = "pipelineId")]
    pub pipeline_id: String,
    /// Default 25, max 1000.
    pub max: Option<i64>,
}

#[derive(Debug, Deserialize, schemars::JsonSchema)]
pub struct ActivitiesInput {
    /// Read ONE activity whole, with its run log. Every other argument is ignored.
    #[serde(rename = "activityId")]
    pub activity_id: Option<i64>,
    /// One flow's trail: its pipeline id.
    #[serde(rename = "pipelineId")]
    pub pipeline_id: Option<String>,
    /// With pipelineId: required for a source with several interfaces.
    pub interface: Option<String>,
    /// The partition whose trail to read. Omit for every partition.
    pub partition: Option<String>,
    #[serde(rename = "submissionId")]
    pub submission_id: Option<String>,
    #[serde(rename = "runId")]
    pub run_id: Option<String>,
    /// Exact, lowercase: deliver, intake, drain, verify, sync, release, redeliver, delete or remove-dimension.
    pub kind: Option<String>,
    /// Exact, as the trail writes it: "user:alice", "schedule:hourly-wells", "cli:bob@host".
    pub actor: Option<String>,
    /// Exact, lowercase: completed, failed or cancelled.
    pub outcome: Option<String>,
    /// false hides runs that changed nothing; true lists only them.
    pub idle: Option<bool>,
    /// Started at or after this instant (ISO-8601).
    pub since: Option<String>,
    /// Started before this instant (ISO-8601).
    pub until: Option<String>,
    pub page: Option<i64>,
    /// Default 25, max 200.
    #[serde(rename = "pageSize")]
    pub page_size: Option<i64>,
}

#[tool_router(router = flows_router, vis = "pub(crate)")]
impl DeliveryTools {
    #[tool(
        description = "What keeps a delivery flow's records blocked, grouped by cause: each issue's error pattern, how many \
records it holds and fails and whether it lies in the set or in rows of their own, the most records first; with issue, \
also the files they came from. Use it for 'why are records failing', 'what do I fix first'. The values an error names \
are left out. Follow with delivery_flow_records for the records behind a count."
    )]
    async fn delivery_flow_issues(&self, Parameters(i): Parameters<FlowIssuesInput>) -> String {
        let pipeline = match guid("pipelineId", &i.pipeline_id, PIPELINE_HINT) {
            Ok(pipeline) => pipeline,
            Err(refused) => return refused,
        };
        let (interface, partition, issue) = (text(i.interface), text(i.partition), text(i.issue));
        let base = format!("/api/v1/delivery/flows/{pipeline}/issues");
        let scope = Query::new().text("interface", interface.clone()).text("partition", partition.clone());
        let read = match issue {
            Some(ref issue) => {
                let path = format!("{base}/{}", sqlflow_mcp::encode(issue));
                let query = scope.value("samples", Some(0));
                self.ctx.read(&path, query.pairs()).await
            }
            None => {
                let query = scope.value("max", i.max);
                self.ctx.read(&base, query.pairs()).await
            }
        };
        let mut answer = match read {
            Ok(answer) => answer,
            Err(error) => return refuse(format!("{error:#}")),
        };
        drop_fields(&mut answer, &["values", "samples", "example"]);
        let mut narrowed = vec![("tab", "issues")];
        if let Some(ref issue) = issue {
            narrowed.push(("issue", issue.as_str()));
        }
        let mut links = Map::new();
        links.insert(
            "page".to_string(),
            json!(flow_page(self.ctx.links(), &pipeline, interface.as_deref(), partition.as_deref(), &narrowed)),
        );
        crate::support::put_links(&mut answer, links);
        sqlflow_mcp::json_str(&answer)
    }

    #[tool(
        description = "A delivery flow at a glance. `stats`: records per custody state (pending, delivering, delivered, \
held, failed, deleted, waiting), drifted, delivered in the last 24 hours, last delivery, last verify, last \
submission. `interfaces`: per interface its mapping, OSDU kind, route, record table, key columns, run parameters, the \
order a run takes them in and what each waits for. `target`: the OSDU endpoint and data partition it declares. Use it \
for 'how is this flow doing', 'where does it deliver', 'what does a run of it need'. pipelineId comes from \
list_pipelines(kind=\"delivery\"). Follow with delivery_flow_records(status=\"failed\") for the records behind a count."
    )]
    async fn delivery_flow(&self, Parameters(i): Parameters<FlowInput>) -> String {
        let pipeline = match guid("pipelineId", &i.pipeline_id, PIPELINE_HINT) {
            Ok(pipeline) => pipeline,
            Err(refused) => return refused,
        };
        let base = format!("/api/v1/delivery/flows/{pipeline}");
        let scoped = Query::new().text("interface", i.interface.clone()).text("partition", i.partition.clone());
        let in_partition = Query::new().text("partition", i.partition.clone());

        // The stats say whether the pipeline is a delivery flow at all; a problem there is the tool's answer.
        let stats = match self.ctx.read(&format!("{base}/stats"), scoped.pairs()).await {
            Ok(stats) => stats,
            Err(error) => return refuse(format!("{error:#}")),
        };
        let interfaces_path = format!("{base}/interfaces");
        let target_path = format!("{base}/target");
        let (interfaces, target) = tokio::join!(
            self.ctx.read(&interfaces_path, in_partition.pairs()),
            self.ctx.read(&target_path, scoped.pairs()),
        );

        let (interface, partition) = (text(i.interface), text(i.partition));
        let mut links = Map::new();
        links.insert(
            "page".to_string(),
            json!(flow_page(self.ctx.links(), &pipeline, interface.as_deref(), partition.as_deref(), &[])),
        );
        composed(
            vec![("stats", stats), ("interfaces", section(interfaces)), ("target", section(target))],
            links,
            "stats counts the flow's records by custody state, for the interface and partition asked for or for the \
             whole. target needs one interface and one partition: for a source of several interfaces, or a flow that \
             names several partitions, it carries an `error` saying which to name. The endpoint and partition are \
             reported as the flow declares them, references and all; no credential is part of them.",
        )
    }

    #[tool(
        description = "List one delivery flow's records, filtered by custody state, by the submission that planned or \
delivered them, by run, by drift, or by a search term. Each row has the record's OSDU id and version, attempt count, \
next attempt time, last error, and source file and row. Use it for 'which records failed and why' (status=failed, \
read lastError), 'what is held', 'what did last night's run deliver' (deliveredBy or runId), 'what drifted'. \
totalCapped=true means more match than were counted: narrow the filter. When the flow is unknown use \
delivery_find_records; for one record's full history use delivery_record."
    )]
    async fn delivery_flow_records(&self, Parameters(i): Parameters<FlowRecordsInput>) -> String {
        let pipeline = match guid("pipelineId", &i.pipeline_id, PIPELINE_HINT) {
            Ok(pipeline) => pipeline,
            Err(refused) => return refused,
        };
        let ids = (
            optional_guid("submissionId", i.submission_id.as_deref(), "A submission id comes from delivery_submissions."),
            optional_guid("deliveredBy", i.delivered_by.as_deref(), "A submission id comes from delivery_submissions."),
            optional_guid("runId", i.run_id.as_deref(), "A run id comes from list_runs."),
        );
        let (submission, delivered_by, run) = match ids {
            (Ok(submission), Ok(delivered_by), Ok(run)) => (submission, delivered_by, run),
            (Err(refused), _, _) | (_, Err(refused), _) | (_, _, Err(refused)) => return refused,
        };
        // The flow's Records tab narrowed as far as it can be: to the interface and partition, and to the submission or
        // run asked about. Its search and state are chosen on the tab itself.
        let (interface, partition) = (text(i.interface), text(i.partition));
        let mut narrowed = vec![("tab", "records")];
        for (name, value) in [("submission", &submission), ("delivered", &delivered_by), ("run", &run)] {
            if let Some(value) = value {
                narrowed.push((name, value.as_str()));
            }
        }
        let links = json!({
            "page": flow_page(self.ctx.links(), &pipeline, interface.as_deref(), partition.as_deref(), &narrowed),
        });
        let query = Query::new()
            .text("search", i.search)
            .text("mode", i.mode)
            .text("status", i.status)
            .text("submissionId", submission.clone())
            .text("deliveredBy", delivered_by.clone())
            .text("runId", run.clone())
            .value("drifted", i.drifted.filter(|d| *d))
            .text("interface", interface)
            .text("partition", partition)
            .page(i.page, i.page_size);
        self.ctx
            .get_about(
                &format!("/api/v1/delivery/flows/{pipeline}/records"),
                query.pairs(),
                ("pipelineId", &pipeline),
                links,
            )
            .await
    }

    #[tool(
        description = "One run from the id you hold: a run id, or the correlation-id in a platform's log. Returns the run, \
every run of its execution (a split delivery's coordinating run and its parts, each with its own id), the plans it \
registered with their counts, and, when no run has the id, why. Use it for 'what is this id in the logs', 'which run \
sent this request'. get_run has a run's scope and failed statement; delivery_flow_records(runId) its records."
    )]
    async fn delivery_run(&self, Parameters(i): Parameters<RunLookupInput>) -> String {
        let query = Query::new().text("id", Some(i.id));
        self.ctx.get("/api/v1/delivery/runs/lookup", query.pairs()).await
    }

    #[tool(
        description = "Submissions: each plan a delivery flow made over its ingestion tables, and what became of it. \
With pipelineId, lists them newest first: what was selected (incremental, full or keys, the source window, the run \
parameters), the counts (planned, delivered, held, failed, skipped unchanged, awaiting approval, waiting), status, \
run and error. With submissionId, reads one with every run that worked on it; add include=[\"batches\"] for its work \
batches or [\"attempts\"] for its delivery tries. Use it for 'what did the last run do', 'why is this submission \
still running', 'which batch is stuck'. For the records of a submission use \
delivery_flow_records(submissionId or deliveredBy)."
    )]
    async fn delivery_submissions(&self, Parameters(i): Parameters<SubmissionsInput>) -> String {
        let named = (
            optional_guid("pipelineId", i.pipeline_id.as_deref(), PIPELINE_HINT),
            optional_guid("submissionId", i.submission_id.as_deref(), "A submission id comes from a listing by pipelineId."),
        );
        let (pipeline, submission) = match named {
            (Ok(pipeline), Ok(submission)) => (pipeline, submission),
            (Err(refused), _) | (_, Err(refused)) => return refused,
        };

        let Some(submission) = submission else {
            let Some(pipeline) = pipeline else {
                return refuse(
                    "Give pipelineId to list a flow's submissions, or submissionId to read one. Find the flow with \
                     list_pipelines(kind=\"delivery\").",
                );
            };
            let (interface, partition) = (text(i.interface), text(i.partition));
            let links = json!({
                "page": flow_page(
                    self.ctx.links(), &pipeline, interface.as_deref(), partition.as_deref(), &[("tab", "submissions")]
                ),
            });
            let query = Query::new()
                .value("max", Some(i.max.unwrap_or(25).clamp(1, 1000)))
                .text("interface", interface)
                .text("partition", partition);
            return self
                .ctx
                .get_about(
                    &format!("/api/v1/delivery/flows/{pipeline}/submissions"),
                    query.pairs(),
                    ("pipelineId", &pipeline),
                    links,
                )
                .await;
        };

        let wanted: Vec<String> = i
            .include
            .unwrap_or_default()
            .iter()
            .map(|s| s.trim().to_ascii_lowercase())
            .filter(|s| !s.is_empty())
            .collect();
        if let Some(unknown) = wanted.iter().find(|s| !["batches", "attempts"].contains(&s.as_str())) {
            return refuse(format!("include names '{unknown}'; a submission's parts are batches and attempts."));
        }

        let base = format!("/api/v1/delivery/submissions/{submission}");
        let detail = match self.ctx.read(&base, &[]).await {
            Ok(detail) => detail,
            Err(error) => return refuse(format!("{error:#}")),
        };
        let mut sections: Vec<(&str, Value)> = vec![("submission", detail)];
        if wanted.iter().any(|s| s == "batches") {
            let query = Query::new().text("status", i.batch_status).value("pageSize", Some(200));
            sections.push(("batches", section(self.ctx.read(&format!("{base}/batches"), query.pairs()).await)));
        }
        if wanted.iter().any(|s| s == "attempts") {
            let query = Query::new().value("max", Some(i.max.unwrap_or(100).clamp(1, 5000)));
            sections.push(("attempts", section(self.ctx.read(&format!("{base}/attempts"), query.pairs()).await)));
        }
        let mut links = Map::new();
        links.insert(
            "page".to_string(),
            json!(self.ctx.links().route(&format!("/delivery/submissions/{submission}"))),
        );
        composed(
            sections,
            links,
            "submission.submission is the plan and its counts; submission.runIds are the platform runs that worked on \
             it (get_run reads each). batches lists up to 200 work batches. A part that could not be read carries an \
             `error` instead of its content.",
        )
    }

    #[tool(
        description = "A retrieval flow's runs, newest first: the OSDU kinds and query each searched, the window it \
covered, where its files and manifest went, how many records, files and bytes it wrote, its status, who asked, its \
run and its error. Use it for 'what did the last retrieval bring back and where is it'. pipelineId comes from \
list_pipelines(kind=\"retrieval\")."
    )]
    async fn delivery_retrievals(&self, Parameters(i): Parameters<RetrievalsInput>) -> String {
        let pipeline = match guid("pipelineId", &i.pipeline_id, PIPELINE_HINT) {
            Ok(pipeline) => pipeline,
            Err(refused) => return refused,
        };
        let query = Query::new().value("max", Some(i.max.unwrap_or(25).clamp(1, 1000)));
        let links = json!({ "page": self.ctx.links().pipeline(&pipeline) });
        self.ctx
            .get_about(
                &format!("/api/v1/delivery/flows/{pipeline}/retrievals"),
                query.pairs(),
                ("pipelineId", &pipeline),
                links,
            )
            .await
    }

    #[tool(
        description = "The delivery audit trail, newest first: who did what, when, with which parameters, and how it \
ended. One row per thing a run did to the ledger (deliver, intake, drain, verify, sync) and per operator action \
(release, redeliver, delete), with its actor, flow, partition, outcome, summary, and the submission, record and run \
it belongs to. Filter by flow, kind, actor, outcome, time window, submission or run; idle=false hides runs that \
changed nothing. Use it for 'who deleted these records', 'what ran against prod yesterday', 'did anything fail since \
Monday'. Pass activityId alone to read one activity with its run log. One record's own trail is in delivery_record."
    )]
    async fn delivery_activities(&self, Parameters(i): Parameters<ActivitiesInput>) -> String {
        if let Some(activity_id) = i.activity_id {
            if activity_id <= 0 {
                return refuse("activityId is the positive number a listed activity carries.");
            }
            return match self.ctx.read(&format!("/api/v1/delivery/activities/{activity_id}"), &[]).await {
                Ok(mut activity) => {
                    clip_field(&mut activity, "log", LOG_LIMIT);
                    sqlflow_mcp::json_str(&activity)
                }
                Err(error) => refuse(format!("{error:#}")),
            };
        }

        let ids = (
            optional_guid("pipelineId", i.pipeline_id.as_deref(), PIPELINE_HINT),
            optional_guid("submissionId", i.submission_id.as_deref(), "A submission id comes from delivery_submissions."),
            optional_guid("runId", i.run_id.as_deref(), "A run id comes from list_runs."),
        );
        let (pipeline, submission, run) = match ids {
            (Ok(pipeline), Ok(submission), Ok(run)) => (pipeline, submission, run),
            (Err(refused), _, _) | (_, Err(refused), _) | (_, _, Err(refused)) => return refused,
        };
        let query = Query::new()
            .text("pipelineId", pipeline)
            .text("interface", i.interface)
            .text("partition", i.partition)
            .text("submissionId", submission)
            .text("runId", run)
            .text("kind", i.kind)
            .text("actor", i.actor)
            .text("outcome", i.outcome)
            .value("idle", i.idle)
            .text("since", i.since)
            .text("until", i.until)
            .page(i.page, i.page_size);
        match self.ctx.read("/api/v1/delivery/activities", query.pairs()).await {
            Ok(mut page) => {
                clip_field(&mut page, "log", LOG_PREVIEW);
                sqlflow_mcp::json_str(&page)
            }
            Err(error) => refuse(format!("{error:#}")),
        }
    }
}
