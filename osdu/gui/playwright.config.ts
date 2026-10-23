import { resolve } from "node:path";
import { defineConfig, devices } from "@playwright/test";

// Run through `npm run e2e`, which passes `--tsconfig tsconfig.node.json`. Without it Playwright applies tsconfig.json,
// whose catch-all `paths` entry (there so the vendored GUI sources resolve this package's node_modules) maps
// "@playwright/test" to its directory; that resolves to the CommonJS entry, and every named import from it fails.
//
// The e2e suite spins up EVERYTHING itself:
//  1. the OSDU Delivery control plane host (dotnet), pointed at the suite's one database, OsduDeliveryE2E; bootstrap
//     provisioning applies SQLFlow's catalog migrations and the OSDU module's beside them, seeds roles, and creates the
//     e2e admin the tests sign in with. The chain's source and ingestion tables are in the same database, in schemas
//     of their own;
//  2. the GUI dev server, pointed at that control plane via VITE_API_BASE_URL (which wins in dev mode);
//  3. a stand-in for the OSDU platform the sample flows deliver to (e2e/osdu-standin.mjs), which answers the wellbore
//     searches a WellLog or a trajectory makes when it renders, and nothing else, so no run reaches a real OSDU.
// Requirements on the machine: a SQL Server the suite may create a database on, sqlcmd, and the .NET SDK, same as the
// DB-backed xUnit suites. The default connection uses integrated security on a local server; SQLFLOW_E2E_CATALOG_DB
// names any other, a SQL login included (CI runs the suite that way).
//
// Both ports are overridable so the suite can run next to a developer's own environment. This matters most
// for the GUI port: a long-running `npm run dev` server on the default 5173 points at the DEV control plane
// (public/config.json), and reuseExistingServer would silently reuse it, so every API call in the suite
// would land on the wrong backend and fail with rejected e2e tokens.

const apiPort = Number(process.env.SQLFLOW_E2E_API_PORT ?? 5299);
const guiPort = Number(process.env.SQLFLOW_E2E_GUI_PORT ?? 5173);
const osduPort = Number(process.env.SQLFLOW_E2E_OSDU_PORT ?? 5301);

// The suite's one database: SQLFlow's catalog, the module's osdu schema and the chain's source and ingestion tables.
// Every local run uses this same database and brings it forward: the host migrates it, and the seed waits for this
// run's fixture commit and replaces what an earlier run left. Never point a run at a new name to get a fresh estate:
// every such name stays on the server. If an estate ever cannot be brought forward, drop OsduDeliveryE2E and run again.
// SQLFLOW_E2E_CATALOG_DB names the database on another server, a SQL login included (CI runs the suite that way).
const catalogDb = process.env.SQLFLOW_E2E_CATALOG_DB
  ?? "Server=localhost;Database=OsduDeliveryE2E;Trusted_Connection=True;TrustServerCertificate=True";

/** The keywords of a SQL Server connection string, lower-cased, each with its last value. */
export function connectionParts(connectionString: string): Map<string, string> {
  const parts = new Map<string, string>();
  for (const pair of connectionString.split(";")) {
    const at = pair.indexOf("=");
    if (at > 0) {
      parts.set(pair.slice(0, at).trim().toLowerCase(), pair.slice(at + 1).trim());
    }
  }

  return parts;
}

/** The first non-empty value among the keys given, the synonyms a connection string may use for one setting. */
export function connectionValue(parts: Map<string, string>, ...keys: string[]): string | undefined {
  return keys.map((key) => parts.get(key)).find((value) => value !== undefined && value !== "");
}

/** The database a connection string names; the chain's flows read three-part names, so one is required. */
export function databaseOf(connectionString: string): string {
  const database = connectionValue(connectionParts(connectionString), "database", "initial catalog");
  if (!database) {
    throw new Error("The e2e connection string names no database, and the chain's flows read three-part names. Add Database=<name>.");
  }

  return database;
}

// The build configuration the hosts run from. Unset, `dotnet run` builds them on the way, inside the servers' start-up
// timeout. CI builds the solution in Release first and names it here, so the hosts start from that build instead.
const dotnetConfiguration = process.env.SQLFLOW_E2E_DOTNET_CONFIGURATION ?? "";
if (!/^[A-Za-z0-9_.-]*$/.test(dotnetConfiguration)) {
  throw new Error(`SQLFLOW_E2E_DOTNET_CONFIGURATION must be a build configuration name such as Release, not '${dotnetConfiguration}'.`);
}

/** The `dotnet` arguments that run one of the module's hosts, from the build configuration named above when there is one. */
export function hostRun(project: string): string[] {
  return ["run", "--project", project, ...(dotnetConfiguration ? ["-c", dotnetConfiguration, "--no-build"] : [])];
}

export const E2E = {
  apiBaseUrl: `http://localhost:${apiPort}`,
  guiBaseUrl: `http://localhost:${guiPort}`,
  adminUsername: "e2e-admin",
  adminPassword: "e2e-admin-password-123456",
  bootstrapSecret: "e2e-bootstrap-secret-0123456789-PADDING",
  // The metadata database: SQLFlow's catalog schema (pipelines, runs, schedules, lineage, sources, users) and the
  // delivery module's osdu schema beside it (the ledger, the mappings, the templates and the partition caches).
  catalogDb,
  // Where the chain's source and ingestion tables live: the suite's one database, where the pre and ingestion flows
  // create their own schemas. SQLFLOW_E2E_DATA_DB moves them to a database of their own, the shape of a real estate,
  // where the source data is the volume; the seed spec creates that database when it is missing.
  dataDb: process.env.SQLFLOW_E2E_DATA_DB ?? catalogDb,
  // Where the sample flows deliver, as every process of the estate resolves their ${env:...} references: the stand-in
  // platform on loopback, which a delivery host reaches only when told it may, and the partition, access groups and
  // legal tag every sample mapping fixture pins. The estate is hermetic, so these are declared here rather than taken
  // from whatever the shell that started the suite happens to hold.
  osdu: {
    OSDU_URL: `http://127.0.0.1:${osduPort}`,
    OSDU_TOKEN_URL: `http://127.0.0.1:${osduPort}/token`,
    OSDU_CLIENT_ID: "the-e2e-authenticates-with-nothing",
    OSDU_CLIENT_SECRET: "the-e2e-authenticates-with-nothing",
    OSDU_SCOPE: "the-e2e-authenticates-with-nothing",
    OSDU_DATA_PARTITION: "dev",
    OSDU_ACL_OWNER: "data.default.owners@dev.dataservices.energy",
    OSDU_ACL_VIEWER: "data.default.viewers@dev.dataservices.energy",
    OSDU_LEGAL_TAG: "dev-reference-data-default",
    SQLFLOW_DELIVERY_ALLOW_LOOPBACK: "true",
  },
  // The OSDU data definitions the control plane reads (the Templates page, and the example records a check's guidance
  // quotes) are the stand-in's as well, kept in a local copy of the suite's own that the global setup clears, so no run
  // reaches the public repository and no run reads what an earlier one kept.
  dataDefinitions: {
    apiUrl: `http://127.0.0.1:${osduPort}/__e2e/data-definitions/`,
    webUrl: `http://127.0.0.1:${osduPort}/__e2e/data-definitions-web/`,
    cacheDirectory: resolve(import.meta.dirname, "e2e", ".fixtures", "data-definitions"),
  },
} as const;

export default defineConfig({
  testDir: "./e2e",
  testMatch: /.*\.spec\.ts$/, // never pick up compiled .js siblings
  globalSetup: "./e2e/global-setup.ts",
  fullyParallel: false, // one control plane + one catalog: keep mutations ordered
  workers: 1,
  retries: process.env.CI ? 1 : 0,
  reporter: [["list"], ["html", { open: "never" }]],
  timeout: 60_000,
  use: {
    baseURL: E2E.guiBaseUrl,
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
  },
  projects: [
    {
      name: "chromium",
      use: { ...devices["Desktop Chrome"] },
    },
  ],
  webServer: [
    {
      // First, so a plan the control plane runs as soon as it is up finds the platform its flows search.
      command: "node e2e/osdu-standin.mjs",
      url: `${E2E.osdu.OSDU_URL}/health`,
      timeout: 30_000,
      reuseExistingServer: !process.env.CI,
      env: {
        SQLFLOW_E2E_OSDU_PORT: String(osduPort),
      },
    },
    {
      command: ["dotnet", ...hostRun("../hosts/SqlFlow.Delivery.ControlPlane.Host")].join(" "),
      // The start-up probe, not liveness or readiness: liveness is up before bootstrap has created a thing, and readiness
      // answers once the catalog and every module database are verified, while the start-up pass may still be making
      // the admin and writing the partitions, so the first test would spend its timeout retrying a login against an
      // estate with no admin yet. The start-up probe answers when every step of the pass is done, which is the state
      // every spec assumes.
      url: `${E2E.apiBaseUrl}/health/startup`,
      timeout: 240_000,
      reuseExistingServer: !process.env.CI,
      env: {
        ASPNETCORE_URLS: E2E.apiBaseUrl,
        ASPNETCORE_ENVIRONMENT: "Production",
        // The host reads the nearest .sqlflow/env at its content root or above, which in a developer's checkout is their
        // own: real credentials and a live OSDU partition. This estate is hermetic and must never pick those up, so it
        // opts out and declares below exactly what it resolves.
        SQLFLOW_LOCAL_ENV_FILE: "false",
        // The catalog connection reaches the control plane as a reference, the way a deployment's does: it can carry a
        // password, and a literal secret never goes into configuration.
        ControlPlane__Catalog__ConnectionReference: "${env:SQLFLOW_E2E_CATALOG_CONNECTION}",
        SQLFLOW_E2E_CATALOG_CONNECTION: E2E.catalogDb,
        // The sample flows read their ingestion tables through this reference. Without it a plan cannot open the tables
        // the seed loaded, and every delivery run fails on a connection it cannot resolve.
        OSDU_DATA_DB: E2E.dataDb,
        // Where the sample flows deliver: the stand-in platform, whose search a plan asks for the wellbores it renders.
        ...E2E.osdu,
        // The partition the estate works against, named as a deployment names it: the control plane registers it when it
        // starts and is not ready without one. Its values stay the variables above, which every run resolves on its node.
        [`Osdu__Partitions__${E2E.osdu.OSDU_DATA_PARTITION}__Description`]: "The stand-in platform's partition",
        // The data definitions the stand-in publishes, never the public repository, and nothing read from it at start.
        Osdu__SchemaRepository__ApiUrl: E2E.dataDefinitions.apiUrl,
        Osdu__SchemaRepository__WebUrl: E2E.dataDefinitions.webUrl,
        Osdu__SchemaRepository__CacheDirectory: E2E.dataDefinitions.cacheDirectory,
        Osdu__SchemaRepository__WarmOnStart: "false",
        ControlPlane__Jwt__SigningKey: "e2e-signing-key-0123456789abcdef-0123456789abcdef-PADDING",
        ControlPlane__Jwt__BootstrapSecret: E2E.bootstrapSecret,
        // The suite provisions a fresh, dedicated test catalog, so it opts into database creation explicitly.
        // Production startup leaves this off, refusing to create/initialise a database it was pointed at by mistake.
        ControlPlane__Bootstrap__AllowCreate: "true",
        ControlPlane__Bootstrap__AdminUsername: E2E.adminUsername,
        ControlPlane__Bootstrap__AdminPasswordReference: E2E.adminPassword,
        ControlPlane__Cors__AllowedOrigins__0: E2E.guiBaseUrl,
        // The suite polls aggressively; the production rate limit would trip on an innocent run.
        ControlPlane__RateLimit__PermitPerWindow: "1000000",
        // Tight loops so the suite is not waiting on production cadences.
        ControlPlane__Scheduler__PollSeconds: "1",
        ControlPlane__ManagedSync__PollSeconds: "1",
      },
    },
    {
      // The CLI port wins over vite.config.ts, so an overridden gui port really binds (strictPort keeps the
      // failure loud if even that port is taken).
      command: `npm run dev -- --port ${guiPort} --strictPort`,
      url: E2E.guiBaseUrl,
      timeout: 120_000,
      reuseExistingServer: !process.env.CI,
      env: {
        VITE_API_BASE_URL: E2E.apiBaseUrl,
      },
    },
  ],
});
