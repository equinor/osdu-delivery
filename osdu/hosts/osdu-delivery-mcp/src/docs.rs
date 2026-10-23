//! The OSDU Delivery documentation, indexed for the doc tools beside SQLFlow's reference corpus.
//!
//! OSDU Delivery extends SQLFlow, and so does its documentation: SQLFlow's corpus documents the platform, and this
//! one documents only what the extension adds. It is written in SQLFlow's reference format (`osdu/docs/reference`,
//! one topic per page, each with frontmatter naming its id, title, type, summary, keywords, related pages and the
//! code it was verified against), with its wiki (`osdu/docs/wiki`: decisions, patterns, narratives, incidents and
//! maps) beside it in SQLFlow's wiki format. SQLFlow's `build_manifest.py` writes `osdu/docs/reference/manifest.json`
//! from the frontmatter of both trees, and `build.rs` compiles every page it lists into the binary, so the search ranks
//! these pages on what their authors wrote for it, exactly as it ranks SQLFlow's.

use serde::Deserialize;
use sqlflow_mcp::{DocMeta, DocPage};

// Generated: `pub static DOC_BODIES: &[(&str, &str)]`, every page the manifest lists, by id.
include!(concat!(env!("OUT_DIR"), "/delivery_doc_bodies.rs"));

const MANIFEST_JSON: &str = include_str!("../../../docs/reference/manifest.json");

macro_rules! osdu_doc {
    ($file:literal) => {
        include_str!(concat!("../../../docs/", $file))
    };
}

/// One entry of the manifest: the page's metadata, and which tree its path is relative to.
#[derive(Deserialize)]
struct Entry {
    #[serde(default)]
    corpus: Option<String>,
    #[serde(flatten)]
    meta: DocMeta,
}

#[derive(Deserialize)]
struct Manifest {
    docs: Vec<Entry>,
}

/// The folder, relative to the repository root, a manifest entry's path is relative to.
fn root_of(corpus: Option<&str>) -> &'static str {
    match corpus {
        Some("wiki") => "osdu/docs/wiki/",
        _ => "osdu/docs/reference/",
    }
}

/// Every page of the OSDU Delivery documentation, with its body, shown under its path from the repository root.
pub fn pages() -> Vec<DocPage> {
    let manifest: Manifest = serde_json::from_str(MANIFEST_JSON)
        .expect("osdu/docs/reference/manifest.json is the manifest build_manifest.py writes, which build.rs read");
    manifest
        .docs
        .into_iter()
        .map(|entry| {
            let mut meta = entry.meta;
            let body = DOC_BODIES
                .iter()
                .find(|(id, _)| *id == meta.id)
                .map(|(_, body)| *body)
                .expect("build.rs embeds a body for every page the manifest lists");
            meta.path = format!("{}{}", root_of(entry.corpus.as_deref()), meta.path);
            DocPage { meta, body }
        })
        .collect()
}

/// The key census of each flow kind and document the module adds, as the editor tooling reads them
/// (`osdu/docs/census`).
pub const CENSUS: &[&str] = &[
    osdu_doc!("census/keys.delivery.json"),
    osdu_doc!("census/keys.retrieval.json"),
    osdu_doc!("census/keys.cache.json"),
    osdu_doc!("census/keys.assertion.json"),
    osdu_doc!("census/keys.dimension.json"),
    osdu_doc!("census/keys.inventory.json"),
    osdu_doc!("census/keys.removal.json"),
    osdu_doc!("census/keys.mapping.json"),
    osdu_doc!("census/keys.dictionary.json"),
];

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::{HashMap, HashSet};
    use std::path::{Path, PathBuf};
    use yaml_rust2::{Yaml, YamlLoader};

    const REFERENCE_TYPES: &[&str] = &["cli-command", "flow-reference", "concept", "guide", "table"];

    /// The folder of `osdu/docs/reference` a table page lives in, one page per table of the osdu schema.
    const TABLES_FOLDER: &str = "osdu/docs/reference/tables/";

    /// A wiki page's type, and the folder of `osdu/docs/wiki` a page of that type lives in, as SQLFlow's wiki has them.
    const WIKI_TYPES: &[(&str, &str)] = &[
        ("decision", "decisions"),
        ("incident", "incidents"),
        ("map", "maps"),
        ("narrative", "narratives"),
        ("pattern", "patterns"),
    ];

    /// The files of an indexed tree that are not pages, as `build_manifest.py` skips them: the navigational and log
    /// surfaces, which carry no frontmatter.
    const NOT_PAGES: &[&str] = &["README.md", "index.md", "log.md"];

    /// Words no page may hold: the em dash the repository forbids, and the names of the one estate the patterns
    /// were learned from, which the documentation describes generically.
    const FORBIDDEN: &[&str] = &["\u{2014}", "recall", "equinor", "petrodb", "stat_comp", "norway_welldb", "statoil"];

    fn repo_root() -> PathBuf {
        Path::new(env!("CARGO_MANIFEST_DIR")).join("../../..").canonicalize().expect("the repository root")
    }

    fn markdown_under(dir: &Path, found: &mut Vec<PathBuf>) {
        for entry in std::fs::read_dir(dir).expect("a readable docs folder") {
            let path = entry.expect("a directory entry").path();
            if path.is_dir() {
                markdown_under(&path, found);
            } else if path.extension().is_some_and(|ext| ext == "md")
                && path.file_name().and_then(|name| name.to_str()).is_some_and(|name| !NOT_PAGES.contains(&name))
            {
                found.push(path);
            }
        }
    }

    /// A page's frontmatter, parsed as the manifest builder parses it.
    fn frontmatter(text: &str, file: &Path) -> Yaml {
        let text = text.replace("\r\n", "\n");
        let rest = text.strip_prefix("---\n").unwrap_or_else(|| panic!("{} has no frontmatter", file.display()));
        let end = rest.find("\n---\n").unwrap_or_else(|| panic!("{}'s frontmatter is not closed", file.display()));
        let docs = YamlLoader::load_from_str(&rest[..end + 1])
            .unwrap_or_else(|e| panic!("{}'s frontmatter is not YAML: {e}", file.display()));
        docs.into_iter().next().unwrap_or_else(|| panic!("{}'s frontmatter is empty", file.display()))
    }

    fn strings(value: &Yaml) -> Vec<String> {
        value.as_vec().map(|items| items.iter().filter_map(|i| i.as_str().map(str::to_string)).collect()).unwrap_or_default()
    }

    #[test]
    fn the_manifest_lists_every_page_as_its_frontmatter_says() {
        let root = repo_root();
        let pages = pages();
        let by_path: HashMap<&str, &DocPage> = pages.iter().map(|p| (p.meta.path.as_str(), p)).collect();

        let mut files = Vec::new();
        markdown_under(&root.join("osdu/docs/reference"), &mut files);
        markdown_under(&root.join("osdu/docs/wiki"), &mut files);

        let mut seen = HashSet::new();
        for file in &files {
            let relative = file.strip_prefix(&root).expect("under the root").to_string_lossy().replace('\\', "/");
            let page = by_path.get(relative.as_str()).unwrap_or_else(|| {
                panic!("{relative} is not in osdu/docs/reference/manifest.json; regenerate it (osdu/docs/reference/README.md)")
            });
            seen.insert(relative.clone());
            let text = std::fs::read_to_string(file).expect("a readable page");
            let front = frontmatter(&text, file);
            let stale = format!("osdu/docs/reference/manifest.json is older than {relative}; regenerate it");
            assert_eq!(front["id"].as_str(), Some(page.meta.id.as_str()), "{stale}");
            assert_eq!(front["title"].as_str(), Some(page.meta.title.as_str()), "{stale}");
            assert_eq!(front["type"].as_str(), Some(page.meta.doc_type.as_str()), "{stale}");
            assert_eq!(front["summary"].as_str(), Some(page.meta.summary.as_str()), "{stale}");
            assert_eq!(strings(&front["keywords"]), page.meta.keywords, "{stale}");
            assert_eq!(strings(&front["related"]), page.meta.related, "{stale}");
            assert_eq!(front["cliCommand"].as_str(), page.meta.cli_command.as_deref(), "{stale}");
            assert_eq!(front["yamlPath"].as_str(), page.meta.yaml_path.as_deref(), "{stale}");
            assert_eq!(text, page.body, "{relative} changed after the binary embedded it");
        }
        let missing: Vec<&str> = pages.iter().map(|p| p.meta.path.as_str()).filter(|p| !seen.contains(*p)).collect();
        assert!(missing.is_empty(), "the manifest lists pages that are not files: {missing:?}");
    }

    #[test]
    fn every_page_is_one_topic_of_ours_with_what_the_search_ranks_on() {
        let pages = pages();
        assert!(pages.len() >= 80, "expected the whole corpus, found {} pages", pages.len());
        let mut ids = HashSet::new();
        for page in &pages {
            let id = &page.meta.id;
            assert!(ids.insert(id.clone()), "the id {id} is used twice");
            assert!(id.starts_with("delivery-"), "{id} could collide with one of SQLFlow's ids");
            if let Some(rest) = page.meta.path.strip_prefix("osdu/docs/wiki/") {
                let folder = rest.split('/').next().unwrap_or_default();
                assert!(
                    WIKI_TYPES.contains(&(page.meta.doc_type.as_str(), folder)),
                    "{id} is a {} page in osdu/docs/wiki/{folder}",
                    page.meta.doc_type
                );
                assert!(id.starts_with("delivery-wiki-"), "{id} is a wiki page, so its id starts delivery-wiki-");
            } else {
                assert!(REFERENCE_TYPES.contains(&page.meta.doc_type.as_str()), "{id} has the type {}", page.meta.doc_type);
                assert_eq!(
                    page.meta.path.starts_with(TABLES_FOLDER),
                    page.meta.doc_type == "table",
                    "{id}: a table page is of type table and lives in {TABLES_FOLDER}, and only a table page does"
                );
            }
            assert!(!page.meta.summary.trim().is_empty() && page.meta.summary.chars().count() <= 160, "{id}'s summary");
            assert!(page.meta.keywords.len() >= 3, "{id} has {} keywords", page.meta.keywords.len());
            assert!(page.body.len() <= 60_000, "{id} is {} bytes: one topic per page", page.body.len());
        }
    }

    #[test]
    fn related_pages_and_links_lead_somewhere() {
        let root = repo_root();
        let pages = pages();
        let ours: HashSet<&str> = pages.iter().map(|p| p.meta.id.as_str()).collect();
        let sqlflow: serde_json::Value = serde_json::from_str(
            &std::fs::read_to_string(root.join("sqlflow/docs/reference/manifest.json")).expect("SQLFlow's manifest"),
        )
        .expect("SQLFlow's manifest is JSON");
        let theirs: HashSet<&str> =
            sqlflow["docs"].as_array().expect("docs").iter().filter_map(|d| d["id"].as_str()).collect();

        for page in &pages {
            for related in &page.meta.related {
                assert!(
                    ours.contains(related.as_str()) || theirs.contains(related.as_str()),
                    "{} names the related page {related}, which no manifest holds",
                    page.meta.id
                );
            }
            // Every relative Markdown link resolves to a file of the repository.
            let folder = root.join(&page.meta.path).parent().expect("a page's folder").to_path_buf();
            let mut rest = page.body;
            while let Some(start) = rest.find("](") {
                rest = &rest[start + 2..];
                let end = rest.find(')').unwrap_or(rest.len());
                let target = rest[..end].trim();
                rest = &rest[end..];
                if target.is_empty()
                    || target.starts_with('#')
                    || target.contains("://")
                    || target.starts_with("mailto:")
                    || target.contains(' ')
                {
                    continue;
                }
                let file = target.split('#').next().unwrap_or(target);
                assert!(
                    folder.join(file).exists(),
                    "{} links {target}, which is not a file of the repository",
                    page.meta.path
                );
            }
        }
    }

    #[test]
    fn pages_are_generic_and_follow_the_writing_rules() {
        for page in pages() {
            let lower = page.body.to_lowercase();
            for word in FORBIDDEN {
                assert!(!lower.contains(word), "{} contains {word:?}", page.meta.path);
            }
        }
    }

    #[test]
    fn every_census_is_registered_and_names_the_kind_it_describes() {
        let root = repo_root();
        let files = std::fs::read_dir(root.join("osdu/docs/census"))
            .expect("the census folder")
            .filter_map(|entry| entry.ok().map(|e| e.path()))
            .filter(|path| path.extension().is_some_and(|ext| ext == "json"))
            .count();
        assert_eq!(CENSUS.len(), files, "a census file in osdu/docs/census is not registered in CENSUS");

        let mut kinds = HashSet::new();
        for census in CENSUS {
            let parsed: serde_json::Value = serde_json::from_str(census).expect("a census file is json");
            let kind = parsed["flowType"].as_str().or(parsed["documentType"].as_str()).expect("a kind").to_string();
            assert!(kinds.insert(kind.clone()), "{kind} is described twice");
        }
        for kind in ["delivery", "retrieval", "cache", "assertion", "dimension", "inventory", "removal", "mapping", "dictionary"] {
            assert!(kinds.contains(kind), "no census describes {kind}");
        }
    }
}
