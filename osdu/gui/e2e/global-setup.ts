import { execFileSync } from "node:child_process";
import { cpSync, existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { basename, join, resolve } from "node:path";
import { E2E, databaseOf } from "../playwright.config";

/**
 * Builds the e2e fixture: a real local git repository holding the sample delivery estate, which the suite registers as a
 * repo source through the GUI. The control plane then syncs it exactly as it would a customer's remote, so pipelines,
 * runs, schedules, mappings and the delivery pages are all exercised against real documents flowing through the
 * product's own path.
 *
 * The estate is the whole chain, not the delivery flows alone: a record reaches a delivery flow through its ingestion
 * tables, which the pre and ingestion flows load from the sample files. The seed spec runs those flows through the CLI
 * host, so the tables a plan reads are made the way production makes them.
 *
 * It is the sample estate (`osdu/samples/welldb`: the well log chain, its lookup tables and their cache flow) with the
 * documents the suites keep beside it as fixtures (`osdu/tests/SqlFlow.Delivery.Tests/Fixtures/documents`): the wellbore
 * flows, a source that delivers several kinds as interfaces, a retrieval flow, the Wellbore, Document and
 * WellboreTrajectory mappings, and the cache flow of the reference data those mappings read. The sample well log chain is
 * the one the specs run; the fixture flows are synced and read, never run, since their source files are not kept. So the
 * catalog holds more than one kind, a source with interfaces and a reference cache, as a real estate does, the same way
 * the control plane suites lay the two out (`SampleEstate`).
 *
 * The repository is laid out per source: one top-level folder for the source, holding its flows, the mappings they pin,
 * the template folder holding the templates those mappings fill, the cache they resolve against and the drop-off folder
 * the pre flows load from. That folder is what the catalog and the GUI call a project, so the Repos page shows one
 * project per source. The cache folder holds everything static: the cache flows, the lookup tables' files in
 * `cache/data`, and the pre and ingestion flows that load those files.
 *
 * The template folder is the sample's own (`osdu/samples/welldb/templates`) with the fixture mappings' templates beside
 * it, which the estate's documents read, a sync records, and the Templates page, the builder and the explorer read from
 * the synced repository's files. Cache versions are catalog objects, so the seed spec
 * imports the sample records as the cache's first version, and then refreshes the lookups cache flow, whose dictionary
 * and ingestion table types need no OSDU, as a node would.
 *
 * Runs use the plan operation, which renders records against the templates, the cache and the ledger without touching an
 * OSDU target.
 */
export default function globalSetup(): void {
  const here = import.meta.dirname;
  const fixturesDir = resolve(here, ".fixtures");
  // One repository per estate, named after its catalog database. A catalog's repo source keeps syncing the folder it was
  // registered with, and every run rewrites the repository with its own database names, so two estates sharing one
  // folder would each end up syncing flows that point at the other's tables.
  const repoDir = join(fixturesDir, `${databaseOf(E2E.catalogDb)}-repo`);
  const samplesDir = resolve(here, "..", "..", SAMPLES);
  const fixtureDocuments = resolve(here, "..", "..", FIXTURE_DOCUMENTS);

  // The repository holds one folder per source, which is what the catalog and the GUI call a project: everything the
  // welldb source needs (its flows, the mappings they pin, the cache they read and the drop-off folder the pre flows
  // load from) sits under `welldb/`, and a second source would be a folder beside it rather than more files mixed into
  // the same `flows/` and `mappings/`.
  const sourceDir = join(repoDir, SOURCE);

  rmSync(repoDir, { recursive: true, force: true });

  // The control plane's copy of the stand-in's data definitions starts empty, so what a spec holds there is what is read.
  rmSync(E2E.dataDefinitions.cacheDirectory, { recursive: true, force: true });
  mkdirSync(join(sourceDir, "flows"), { recursive: true });
  mkdirSync(join(sourceDir, "cache"), { recursive: true });

  // The mappings the flows pin, and the sample files the pre flows read. The data folder is the source's drop-off point:
  // it is what makes the chain runnable, because without it a pre flow has nothing to land. The lookup tables' files are
  // static data and sit in the cache folder, beside the cache flow that holds them, and their own pre flows land them.
  for (const part of ["mappings", "templates", "data", join("cache", "data")]) {
    cpSync(join(samplesDir, part), join(sourceDir, part), { recursive: true });
  }

  // The fixture mappings the wellbore flows and the interfaces source pin, beside the sample's own, and the templates of
  // theirs the sample's template folder does not hold, in the same folder, where the mappings read them.
  for (const mapping of FIXTURE_MAPPINGS) {
    cpSync(join(fixtureDocuments, "mappings", `${mapping}.yaml`), join(sourceDir, "mappings", `${mapping}.yaml`));
  }
  for (const template of TEMPLATES.filter((t) => t.file.startsWith(FIXTURE_TEMPLATES))) {
    cpSync(resolve(here, "..", "..", template.file), join(sourceDir, "templates", basename(template.file)));
  }

  // Every flow of the estate, each without its schedule and with its tables in the sample database. A fire would be a
  // real run against the sample's OSDU target whenever a suite crossed its cron, and the specs expect flows that join no
  // schedule. The ingestion and delivery flows name their tables in OsduData, while the pre flows write wherever
  // OSDU_DATA_DB points; unless both name the same database, lineage never links a pre flow to what reads it, and the
  // waves the chain runs in are wrong. By default that is the suite's one database, beside the catalog and the module's
  // schema; SQLFLOW_E2E_DATA_DB gives the source data a database of its own, as a real estate does.
  const dataDatabase = databaseOf(E2E.dataDb);
  for (const flow of CHAIN) {
    const shipped = readFileSync(
      (FIXTURE_FLOWS as readonly string[]).includes(flow)
        ? join(fixtureDocuments, "flows", `${flow}.yaml`)
        : join(samplesDir, folderOf(flow), `${flow}.yaml`),
      "utf8",
    );
    writeFileSync(
      join(sourceDir, folderOf(flow), `${flow}.yaml`),
      withoutTheLegalCheck(inDataDatabase(withoutCacheSteps(withoutSchedule(shipped, flow), flow), flow, dataDatabase)),
    );
  }

  // The document that defines the reference data the fixture mappings resolve against, under the source that needs it.
  // That document is the whole of what a repository holds about a cache: the cache itself lives in the module's
  // database, captured there by a run. The suite never refreshes it (that would need an OSDU target), so the seed spec
  // imports the fixture records the suites import (`CACHE_RECORDS`) as its first version.
  writeFileSync(
    join(sourceDir, "cache", `${CACHE}.yaml`),
    withoutSchedule(readFileSync(join(fixtureDocuments, "cache", `${CACHE}.yaml`), "utf8"), CACHE),
  );

  // The partition's reference data the well log mapping checks every id it builds against, searched on OSDU by the
  // sample's reference cache flow. The suite never refreshes it either, so the seed imports the sample records
  // (`REFERENCE_RECORDS`) as that flow's capture.
  writeFileSync(
    join(sourceDir, "cache", `${REFERENCE}.yaml`),
    withoutSchedule(readFileSync(join(samplesDir, "cache", `${REFERENCE}.yaml`), "utf8"), REFERENCE),
  );

  // The lookup tables the mappings translate source spellings through (the unit maps and the curve dictionary) fill the
  // same partition's cache from the ingestion tables their flows load from cache/data, so nothing of them reaches OSDU and
  // the seed refreshes them for real. The tables live in the sample database, like every other ingestion table of the estate.
  writeFileSync(
    join(sourceDir, "cache", `${LOOKUPS}.yaml`),
    inDataDatabase(withoutSchedule(readFileSync(join(samplesDir, "cache", `${LOOKUPS}.yaml`), "utf8"), LOOKUPS), LOOKUPS, dataDatabase),
  );

  const git = (...args: string[]) =>
    execFileSync("git", args, { cwd: repoDir, stdio: "pipe" }).toString("utf8").trim();

  git("init", "-b", "main");
  git("config", "user.email", "e2e@sqlflow.test");
  git("config", "user.name", "OSDU Delivery E2E");
  git("add", "-A");
  git("commit", "-m", "e2e fixture: the welldb delivery estate");
  const headSha = git("rev-parse", "HEAD");

  // Tests read the repo path, the source folder inside it, the exact commit to expect and the database the chain loads
  // into from this meta file (globalSetup runs in a separate process from the specs). Waiting on THIS sha makes re-runs
  // deterministic: a stale synced sha from a previous suite run never satisfies the seed assertions.
  writeFileSync(
    join(fixturesDir, "meta.json"),
    JSON.stringify({
      repoDir: repoDir.replace(/\\/g, "/"),
      sourceDir: sourceDir.replace(/\\/g, "/"),
      headSha,
      dataDb: E2E.dataDb,
    }, null, 2),
  );

  if (!existsSync(join(repoDir, ".git"))) {
    throw new Error(`Fixture repo was not initialized at ${repoDir}.`);
  }
}

/**
 * The source the fixture estate belongs to, and the folder it occupies in the repository. Every file of the estate
 * lives under it, so the catalog and the GUI see one project named after the source rather than a repository whose top
 * level is a pile of file kinds.
 */
export const SOURCE = "welldb";

/** The sample estate the fixture repository is built from, relative to the OSDU module's folder. */
export const SAMPLES = join("samples", "welldb");

/** The documents the suites keep beside the sample estate, relative to the OSDU module's folder. */
export const FIXTURE_DOCUMENTS = join("tests", "SqlFlow.Delivery.Tests", "Fixtures", "documents");

/**
 * The reference data cache the fixture mappings resolve against (units, trajectory station property types): the flow
 * file `<SOURCE>/cache/<CACHE>.yaml`, whose first version the seed imports from `CACHE_RECORDS`.
 */
export const CACHE = "fixtures-osdu-00-reference-cache";

/** The records the seed imports as the reference cache's first version, relative to the OSDU module's folder. */
export const CACHE_RECORDS = join("tests", "SqlFlow.Delivery.Tests", "Fixtures", "cache-records");

/** The cache flow holding the lookup tables in the same partition's cache: `<SOURCE>/cache/<LOOKUPS>.yaml`. */
export const LOOKUPS = "welldb-lookups-00-cache";

/**
 * The cache flow capturing the partition's reference data from OSDU into the same cache, which every id the well log
 * mapping builds is checked against: `<SOURCE>/cache/<REFERENCE>.yaml`, whose capture the seed imports from
 * `REFERENCE_RECORDS`.
 */
export const REFERENCE = "osdu-reference-00-cache";

/** The records the seed imports as the reference flow's capture, relative to the OSDU module's folder. */
export const REFERENCE_RECORDS = join("samples", "cache-records");

/** The delivery flow the specs run: the sample's well log delivery, with the logSource its one required parameter. */
export const DELIVERY_FLOW = "welldb-welllog-03-header-delivery";

/** The log source the sample's schedule delivers, and so the value the specs run the delivery flow with. */
export const LOG_SOURCE = "COMPOSITE";

/** The source that delivers several kinds as interfaces, each with a ledger of its own. */
export const INTERFACES_FLOW = "wells-source-03-interfaces-delivery";

/** The folder of the fixture templates, relative to the OSDU module's folder: the kinds the sample's template folder does not hold. */
export const FIXTURE_TEMPLATES = join("tests", "SqlFlow.Delivery.Tests", "Fixtures", "templates");

/**
 * The templates the estate's mappings fill, each with the bundled schema it is read from (relative to the OSDU module's
 * folder) and its version. The sample's own sit in its template folder, `samples/welldb/templates`, the fixture
 * mappings' in the fixtures; the seed copies both into the source's template folder.
 */
export const TEMPLATES = [
  { kind: "osdu:wks:work-product-component--WellLog:1.4.0", file: join("samples", "welldb", "templates", "osdu_wks_work-product-component--WellLog_1.4.0.json"), version: "26a3c3441882db4f" },
  { kind: "osdu:wks:master-data--Wellbore:1.3.0", file: join("samples", "welldb", "templates", "osdu_wks_master-data--Wellbore_1.3.0.json"), version: "58d6bdbd9d066a06" },
  { kind: "osdu:wks:master-data--Well:1.2.0", file: join("samples", "welldb", "templates", "osdu_wks_master-data--Well_1.2.0.json"), version: "d61b5c4745d024fc" },
  { kind: "osdu:wks:reference-data--UnitQuantity:1.0.0", file: join("samples", "welldb", "templates", "osdu_wks_reference-data--UnitQuantity_1.0.0.json"), version: "7af71b10e691c21f" },
  { kind: "osdu:wks:reference-data--UnitOfMeasure:1.0.0", file: join("samples", "welldb", "templates", "osdu_wks_reference-data--UnitOfMeasure_1.0.0.json"), version: "bb4bd8e91683061d" },
  { kind: "osdu:wks:reference-data--LogCurveFamily:1.1.0", file: join("samples", "welldb", "templates", "osdu_wks_reference-data--LogCurveFamily_1.1.0.json"), version: "faf2a52e39a7928e" },
  { kind: "osdu:wks:reference-data--LogCurveMainFamily:1.0.0", file: join("samples", "welldb", "templates", "osdu_wks_reference-data--LogCurveMainFamily_1.0.0.json"), version: "f427ef2eecfaa4ac" },
  { kind: "osdu:wks:reference-data--LogCurveType:1.1.0", file: join("samples", "welldb", "templates", "osdu_wks_reference-data--LogCurveType_1.1.0.json"), version: "cb4614b0dc3c2871" },
  { kind: "osdu:wks:reference-data--LogCurveBusinessValue:1.0.0", file: join("samples", "welldb", "templates", "osdu_wks_reference-data--LogCurveBusinessValue_1.0.0.json"), version: "b6cc6d13a63e2ce8" },
  { kind: "osdu:wks:reference-data--LogType:1.0.0", file: join("samples", "welldb", "templates", "osdu_wks_reference-data--LogType_1.0.0.json"), version: "643f542b69519f32" },
  { kind: "osdu:wks:reference-data--WellLogSamplingDomainType:1.0.0", file: join("samples", "welldb", "templates", "osdu_wks_reference-data--WellLogSamplingDomainType_1.0.0.json"), version: "e05745bb62213c7a" },
  { kind: "osdu:wks:reference-data--TechnicalAssuranceType:1.0.0", file: join("samples", "welldb", "templates", "osdu_wks_reference-data--TechnicalAssuranceType_1.0.0.json"), version: "9687cd156c86735a" },
  { kind: "osdu:wks:reference-data--VerticalMeasurementType:1.0.3", file: join("samples", "welldb", "templates", "osdu_wks_reference-data--VerticalMeasurementType_1.0.3.json"), version: "29e50d85666ca947" },
  { kind: "osdu:wks:reference-data--TrajectoryStationPropertyType:1.0.0", file: join("samples", "welldb", "templates", "osdu_wks_reference-data--TrajectoryStationPropertyType_1.0.0.json"), version: "84ae5a46853e48e9" },
  { kind: "osdu:wks:work-product-component--WellboreMarkerSet:1.2.1", file: join("samples", "welldb", "templates", "osdu_wks_work-product-component--WellboreMarkerSet_1.2.1.json"), version: "ec9ad4f8f4e6e713" },
  { kind: "osdu:wks:work-product-component--Document:1.0.0", file: join(FIXTURE_TEMPLATES, "osdu_wks_work-product-component--Document_1.0.0.json"), version: "5c6898ebc6775f6e" },
  { kind: "osdu:wks:work-product-component--WellboreTrajectory:1.3.0", file: join("samples", "welldb", "templates", "osdu_wks_work-product-component--WellboreTrajectory_1.3.0.json"), version: "bfbc5973bbdeb7ec" },
] as const;

/** The fixture mappings the wellbore flows and the interfaces source pin, copied beside the sample's WellLog mapping. */
export const FIXTURE_MAPPINGS = ["Wellbore@1.0.0", "Document@1.0.0", "WellboreTrajectory@1.0.0"] as const;

/** The flows of the estate taken from the fixture documents rather than the sample: synced and read, never run. */
export const FIXTURE_FLOWS = [
  "wells-wellbore-01-header-pre",
  "wells-wellbore-01-aliases-pre",
  "wells-wellbore-02-header-ing",
  "wells-wellbore-02-aliases-ing",
  "wells-wellbore-03-header-delivery",
  INTERFACES_FLOW,
  "wells-osdu-04-metadata-retrieval",
] as const;

/** What globalSetup leaves behind for the specs, which run in a process of their own and so cannot be told directly. */
export interface FixtureMeta {
  /** The fixture git repository, which the suite registers as a repo source. */
  repoDir: string;
  /** The source's folder inside it (`<repoDir>/<SOURCE>`): where its flows, mappings, cache and data are. */
  sourceDir: string;
  /** The commit the sync has to reach before the seed assertions hold. */
  headSha: string;
  dataDb: string;
}

/**
 * The name the fixture repo is registered under. The seed registers the source with it and every run of the chain
 * records into it, so a run never falls back to naming a repo after the folder it was started from.
 */
export const REPO_NAME = "e2e-repo";

/**
 * The flows that load the lookup tables' static files. They sit in the source's cache folder, beside those files and the
 * cache flow that holds the tables, rather than among the flows of the source's data.
 */
export const CACHE_LOADING_FLOWS = [
  "welldb-curvedictionary-01-pre",
  "welldb-curvedictionary-02-ing",
  "welldb-unitalias-01-pre",
  "welldb-unitalias-02-ing",
  "welldb-depthunitalias-01-pre",
  "welldb-depthunitalias-02-ing",
] as const;

/** The folder of the source a flow of the estate sits in. */
export function folderOf(flow: string): "cache" | "flows" {
  return (CACHE_LOADING_FLOWS as readonly string[]).includes(flow) ? "cache" : "flows";
}

/** The delivery flows of the fixture estate, and the pre and ingestion flows that fill the tables they read. */
export const CHAIN = [
  "welldb-welllog-01-header-pre",
  "welldb-welllog-01-curves-pre",
  "welldb-welllog-02-header-ing",
  "welldb-welllog-02-curves-ing",
  DELIVERY_FLOW,
  ...CACHE_LOADING_FLOWS,
  // The fixture flows: the wellbore chain, and the estate in the shape a source takes, several kinds as interfaces each
  // with a ledger of its own. They are synced and read, never run, so they add more kinds, routes and a multi-interface
  // source to the catalog without delivering anything twice.
  ...FIXTURE_FLOWS,
] as const;

/** The flows that load the ingestion tables, in the order they have to run: the pre flows land files, the ing flows key them. */
export const LOADING_FLOWS = [
  "welldb-welllog-01-header-pre",
  "welldb-welllog-01-curves-pre",
  "welldb-welllog-02-header-ing",
  "welldb-welllog-02-curves-ing",
  ...CACHE_LOADING_FLOWS,
] as const;

/**
 * A sample flow without its schedule: the top-level block a flow of its own declares, or the one line naming a schedule
 * of the estate's library it joins. The suite triggers every run itself: a fire would be a real run against the sample's
 * OSDU target whenever a suite crossed its cron, and the specs expect flows that join no schedule, so the runs board and
 * the schedules page show only what the suite created. The library itself (`schedules.yaml`) is not copied either.
 */
function withoutSchedule(yaml: string, flow: string): string {
  const stripped = yaml
    .replace(/^schedule:\r?\n(?:[ \t].*\r?\n)*/m, "")
    .replace(/^(?:#.*\r?\n)*schedule:[ \t]*[A-Za-z0-9_.-]+[ \t]*(?:#.*)?(?:\r?\n|$)/m, "");
  if (/^schedule:/m.test(stripped)) {
    throw new Error(`The fixture flow '${flow}' still declares a schedule block; the e2e suite needs flows that join no schedule.`);
  }

  return stripped;
}

/**
 * A sample flow without its cache steps (its top-level `cache:` block). The suite's runs stop short of OSDU and render
 * against the cache the seed imports, so a step that would bring the cache up to date before or after a run stays out,
 * as the platform it reads does.
 */
function withoutCacheSteps(yaml: string, flow: string): string {
  const stripped = yaml.replace(/^cache:\r?\n(?:[ \t].*\r?\n)*/m, "");
  if (/^cache:/m.test(stripped)) {
    throw new Error(`The fixture flow '${flow}' still declares a cache block; the e2e suite needs flows whose runs stop short of OSDU.`);
  }

  return stripped;
}

/**
 * A flow whose runs ask no legal service. Every run this suite triggers stops short of OSDU: a plan never opens the
 * target, and an intake, which is how records reach the ledger without anything being sent, would otherwise ask the
 * legal service about the mapping's tags before it plans. The check is what the target is for, so turning it off here
 * is what keeps the suite off the network; a flow that declares no protocolOptions is left as it is, because no spec
 * intakes one.
 */
function withoutTheLegalCheck(yaml: string): string {
  return yaml.replace(/^( *)protocolOptions: *$/m, (line, indent: string) => `${line}
${indent}  validateLegalTags: false`);
}

/** The database the shipped ingestion and delivery flows name their tables in. */
const SHIPPED_DATABASE = "OsduData";

/**
 * A sample flow whose tables live in the given database rather than the shipped OsduData: every
 * `object: OsduData.<schema>.<table>`, and every cache flow's `table: OsduData.<schema>.<table>`, becomes the
 * bracketed three-part name in that database. Any other mention of OsduData fails the setup, so a shipped document that
 * names it somewhere new cannot leave the estate reading a database the suite never loaded.
 */
function inDataDatabase(yaml: string, flow: string, database: string): string {
  const quoted = `[${database.replace(/]/g, "]]")}]`;
  const rewritten = yaml.replace(
    /^([ \t]*(?:- )?)(object|table):[ \t]*OsduData\.(\w+)\.(\w+)[ \t]*$/gm,
    (_match, indent: string, key: string, schema: string, table: string) => `${indent}${key}: "${quoted}.[${schema}].[${table}]"`,
  );
  if (rewritten.includes(`${SHIPPED_DATABASE}.`)) {
    throw new Error(
      `The fixture flow '${flow}' still names ${SHIPPED_DATABASE} somewhere other than an object: <database>.<schema>.<table> value, so the e2e estate would read a database the suite never loaded.`,
    );
  }

  return rewritten;
}
