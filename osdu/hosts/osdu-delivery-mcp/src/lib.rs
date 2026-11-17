//! The OSDU Delivery MCP server: SQLFlow's MCP server composed with the delivery module.
//!
//! SQLFlow's server is a library a host composes (`sqlflow/tools/sqlflow-mcp`), as its control plane and its CLI are.
//! This crate is that host for OSDU Delivery. [`module`] is what the delivery module adds: the `delivery_*` tools over
//! the module's endpoints, its section of the instructions, the product's documentation, the GUI routes of its rows,
//! and the key census of the flow kinds and documents it adds. [`host`] is the program: its name, where it keeps its
//! sign-in, and the tools of SQLFlow's it does not offer.
//!
//! The server answers from metadata. A delivery tool returns the ledger's bookkeeping (states, ids, counts, history,
//! errors) and the module's definitions (flows, mappings, templates, what a cache is made of), never record content,
//! a source row, a cached value or a dimension's values. The one read of data is a `!` question's: SQLFlow's query
//! tools run a single read-only SELECT over the columns an approved AI context opens, which the control plane checks
//! on prepare and on run. The tools of SQLFlow's that read rows for anything else are left out ([`WITHHELD`]).

pub mod docs;
pub mod instructions;
pub mod links;
mod support;
pub mod tools;

use sqlflow_mcp::{McpHost, McpModule};

use crate::tools::DeliveryTools;

/// The program's name: what `--version` prints, what the server reports itself as, and the label of the access token
/// it mints, as the control plane's token list shows it.
pub const SERVER_NAME: &str = "osdu-delivery-mcp";

/// The name `install` registers the server under in a client (`claude mcp add osdu-delivery ...`).
pub const REGISTRATION: &str = "osdu-delivery";

/// The module's name, as the control plane's module is named.
pub const MODULE_NAME: &str = "delivery";

/// The tools of SQLFlow's own server this product does not offer: every one that reaches a datasource or reads a
/// source's content for anything but a `!` question. OSDU Delivery's assistant answers from metadata, and from data
/// only for a `!` question, through SQLFlow's query tools (`prepare_query`, `run_query`, `auto_run_trusted_match`),
/// which read nothing an approved AI context does not open; profiling a table's rows, a sample file or a warehouse's
/// health is not its to do.
pub const WITHHELD: &[&str] = &[
    // Profile a table's rows on a node: its keys, its duplicates, its difference from another estate's table.
    "detect_unique_key",
    "check_duplicate_keys",
    "compare_baseline",
    // Describes the data-operations surface the three above belong to.
    "dataops_capabilities",
    // Runs probes against a datasource's management views.
    "analyze_warehouse_health",
    // Read a sample file, or introspect and sample a source table, to generate a flow from it.
    "discover_source",
    "scaffold_ingestion_flow",
];

/// The verbs the delivery module adds to the `sqlflow` command line that work with a sign-in alone, as `get_environment`
/// shows them after SQLFlow's. The ledger verbs (`records`, `assertions`) read the module's database (`--db`), which a
/// person's assistant does not hold; the delivery tools answer those through the control plane.
const CLI_USES: &[(&str, &str)] = &[
    (
        "sqlflow check <flow.yaml>",
        "the delivery preflight: the flow's documents and its mapping against the template it fills (a mapping that reads a cache needs --db)",
    ),
    ("sqlflow template folders", "the working copy's template folders and the template each file holds, read from disk"),
];

/// The delivery module: its tools, its instructions, the product's documentation, its GUI links and its key census.
pub fn module() -> McpModule {
    let mut module = McpModule::new(MODULE_NAME)
        .tools(|ctx| (DeliveryTools::new(ctx), DeliveryTools::router()))
        .instructions(instructions::INSTRUCTIONS)
        .cli_uses(CLI_USES.iter().copied())
        .docs(docs::pages())
        .link_rule(links::delivery_links);
    for census in docs::CENSUS {
        module = module.census(*census);
    }
    module
}

/// The OSDU Delivery MCP server: SQLFlow's, with the delivery module, under the product's own name and with a
/// sign-in of its own, so it can be installed beside `sqlflow-mcp` and pointed at another control plane.
pub fn host() -> McpHost {
    McpHost::new(SERVER_NAME, env!("CARGO_PKG_VERSION"))
        .registered_as(REGISTRATION)
        .state_prefix(SERVER_NAME)
        .introduced_as(instructions::INTRODUCTION)
        .without_tools(WITHHELD.iter().copied())
        .with_module(module())
}
