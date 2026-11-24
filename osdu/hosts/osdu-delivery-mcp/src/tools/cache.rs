//! The partitions the estate delivers to, the cache each keeps (as metadata: what it is made of and how it stands,
//! never the cached values), and the central configuration runs are given.

use serde::Deserialize;
use serde_json::json;
use sqlflow_mcp::rmcp::handler::server::wrapper::Parameters;
use sqlflow_mcp::rmcp::{self, schemars, tool, tool_router};
use sqlflow_mcp::{encode, json_str, EmptyInput};

use super::DeliveryTools;
use crate::support::{drop_fields, optional_guid, refuse, text, Query};

/// The fields of a cache change that carry the cached values themselves: the value before and after, and the
/// sentence that puts the two into words.
const CHANGE_VALUES: [&str; 3] = ["oldValue", "newValue", "summary"];

#[derive(Debug, Deserialize, schemars::JsonSchema)]
pub struct CachesInput {
    /// One partition, to read its cache's health. Omit to list every partition's cache.
    pub partition: Option<String>,
    /// Without a partition: the caches one repository's cache flows fill (its id).
    #[serde(rename = "repoId")]
    pub repo_id: Option<String>,
}

#[derive(Debug, Deserialize, schemars::JsonSchema)]
pub struct CacheVersionsInput {
    /// The partition whose cache to read.
    pub partition: String,
    /// true returns per version how many records it changed, added and removed, and which types moved.
    pub history: Option<bool>,
    /// With history: narrow the counts to one cached type.
    #[serde(rename = "type")]
    pub type_name: Option<String>,
}

#[derive(Debug, Deserialize, schemars::JsonSchema)]
pub struct CacheChangesInput {
    /// One state: pending, approved, rolling, rejected or applied.
    pub status: Option<String>,
    /// One partition. Omit for all.
    pub partition: Option<String>,
    pub page: Option<i64>,
    /// Default 25, max 200.
    #[serde(rename = "pageSize")]
    pub page_size: Option<i64>,
}

#[derive(Debug, Deserialize, schemars::JsonSchema)]
pub struct ConfigInput {
    /// Resolve the effective values for one repository's flows (its id). Omit to list every property as set.
    #[serde(rename = "repoId")]
    pub repo_id: Option<String>,
    /// With repoId: resolve as a run bound to this partition would.
    pub partition: Option<String>,
}

#[tool_router(router = cache_router, vis = "pub(crate)")]
impl DeliveryTools {
    #[tool(
        description = "The OSDU data partitions the estate knows. Per partition: whether it is registered and the \
default, what it is for, its cache's current version with type and record counts, the cache flows that fill it, the \
delivery flows that deliver to it, and how many cache changes await a decision. Start here when a question names an \
environment ('what do we deliver to prod', 'is the dev cache fresh'): a partition's name is the `partition` argument \
of every other delivery tool."
    )]
    async fn delivery_partitions(&self, Parameters(_): Parameters<EmptyInput>) -> String {
        let links = json!({ "page": self.ctx.links().route("/delivery/partitions") });
        self.ctx
            .get_about("/api/v1/delivery/partitions", &[], ("listing", "partitions"), links)
            .await
    }

    #[tool(
        description = "The partition caches (the reference data deliveries resolve ids against), as metadata. \
Without a partition: every cache with its cache flows and schedules, the types they declare (origin, paths kept, \
record count, whether a change needs approval), the current version and retentionDays. With a partition: that \
cache's health per type (the flows its content passes through with each one's last run, the delivery flows reading \
it, and fresh, failed or missing with why), plus `notices`. Use it for 'is the cache fresh', 'why is this type \
missing', 'what reads this type'. Never returns cached values. To refresh a cache, run its cache flow with \
trigger_run."
    )]
    async fn delivery_caches(&self, Parameters(i): Parameters<CachesInput>) -> String {
        if let Some(partition) = text(i.partition) {
            let query = Query::new().text("partition", Some(partition.clone()));
            let links = json!({ "page": self.cache_page(&partition, None) });
            return self
                .ctx
                .get_about("/api/v1/delivery/cache/streams", query.pairs(), ("partition", &partition), links)
                .await;
        }
        let repo = match optional_guid("repoId", i.repo_id.as_deref(), "A repository id is the `id` of a repo in list_repos.") {
            Ok(repo) => repo,
            Err(refused) => return refused,
        };
        let query = Query::new().text("repoId", repo);
        let links = json!({ "page": self.ctx.links().route("/delivery/cache") });
        self.ctx
            .get_about("/api/v1/delivery/caches", query.pairs(), ("listing", "caches"), links)
            .await
    }

    #[tool(
        description = "The versions of one partition's cache, newest first: when each was captured, the cache flow and \
run that wrote it, who asked, whether it is current, the record count per type with whether the type was added, \
changed or unchanged, and prunedUtc once the retention pruned its records (it is then still listed, not readable). \
history=true returns instead how many records each version changed, added and removed, and \
which types it moved. Use it for 'when did the cache last change', 'which refresh moved the wellbores', 'which \
version did a delivery render against' (a submission's render context names it). Counts only: no cached value is \
returned."
    )]
    async fn delivery_cache_versions(&self, Parameters(i): Parameters<CacheVersionsInput>) -> String {
        let Some(partition) = text(Some(i.partition)) else {
            return refuse("Name the partition whose cache to read; delivery_partitions lists them.");
        };
        let links = json!({ "page": self.cache_page(&partition, Some("history")) });
        if i.history.unwrap_or(false) {
            let query = Query::new().text("scope", Some(partition.clone())).text("type", i.type_name);
            return self
                .ctx
                .get_about("/api/v1/delivery/cache/history", query.pairs(), ("partition", &partition), links)
                .await;
        }
        let query = Query::new().text("scope", Some(partition.clone()));
        self.ctx
            .get_about("/api/v1/delivery/cache/versions", query.pairs(), ("partition", &partition), links)
            .await
    }

    #[tool(
        description = "Cache changes and what they reach: one row per change a refresh found in a cached value that \
delivered records were built from. Each gives the partition, the cached type and path that moved, how many delivered \
records it affects, how many are already marked for redelivery and how many still wait (and in which flows), and its \
state: pending (awaiting a decision), approved, rolling, rejected or applied. Use it for 'what awaits approval', \
'what will this change redeliver', 'why were these records delivered again'. The values before and after are not \
returned. A pending change is decided with delivery_decide_cache_changes."
    )]
    async fn delivery_cache_changes(&self, Parameters(i): Parameters<CacheChangesInput>) -> String {
        let partition = text(i.partition);
        let query = Query::new()
            .text("status", i.status)
            .text("scope", partition.clone())
            .page(i.page, i.page_size);
        let page = match &partition {
            Some(partition) => self.cache_page(partition, Some("deliveries")),
            None => self.ctx.links().route("/delivery/cache?tab=deliveries"),
        };
        match self.ctx.read("/api/v1/delivery/cache/tags", query.pairs()).await {
            Ok(mut changes) => {
                drop_fields(&mut changes, &CHANGE_VALUES);
                if let Some(map) = changes.as_object_mut() {
                    map.insert("links".to_string(), json!({ "page": page }));
                }
                json_str(&changes)
            }
            Err(error) => refuse(format!("{error:#}")),
        }
    }

    #[tool(
        description = "The central configuration the control plane supplies to runs: the values flows' ${env:NAME} \
references resolve to (OSDU endpoint, data partition, ACL groups, legal tag, token endpoint), set for the control \
plane, a repository or a partition. Without arguments, lists every property as it was set, with who set it and when. \
With repoId, returns the effective values a run of that repository's flows gets (with partition, as a run bound to it \
does). A value is a plain value or a ${env:...} or ${keyvault:...} reference, never a secret. Use it for 'which OSDU \
does this flow really deliver to', 'which legal tag do prod deliveries carry'."
    )]
    async fn delivery_config(&self, Parameters(i): Parameters<ConfigInput>) -> String {
        let repo = match optional_guid("repoId", i.repo_id.as_deref(), "A repository id is the `id` of a repo in list_repos.") {
            Ok(repo) => repo,
            Err(refused) => return refused,
        };
        match repo {
            Some(repo) => {
                let query = Query::new().text("partition", i.partition);
                let links = json!({ "page": self.ctx.links().repo(&repo) });
                self.ctx
                    .get_about(
                        &format!("/api/v1/delivery/config/effective/{repo}"),
                        query.pairs(),
                        ("repoId", &repo),
                        links,
                    )
                    .await
            }
            None => self.ctx.get("/api/v1/delivery/config", &[]).await,
        }
    }
}

impl DeliveryTools {
    /// The cache page opened on one partition, and on one of its tabs when named.
    pub(crate) fn cache_page(&self, partition: &str, tab: Option<&str>) -> String {
        let tab = tab.map(|t| format!("&tab={}", encode(t))).unwrap_or_default();
        self.ctx
            .links()
            .route(&format!("/delivery/cache?partition={}{tab}", encode(partition)))
    }
}
