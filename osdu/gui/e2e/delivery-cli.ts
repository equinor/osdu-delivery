import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { E2E, hostRun } from "../playwright.config";
import { DELIVERY_FLOW, FixtureMeta, LOG_SOURCE, REPO_NAME } from "./global-setup";

/** The OSDU module's folder: the sample estate and the hosts live beside the GUI. */
const moduleRoot = join(import.meta.dirname, "..", "..");

/** What globalSetup left for the specs: the fixture repository, its source folder and the databases the chain uses. */
export function fixtureMeta(): FixtureMeta {
  return JSON.parse(readFileSync(join(import.meta.dirname, ".fixtures", "meta.json"), "utf8")) as FixtureMeta;
}

/** The well log delivery flow's document in the fixture repository, as a command names it. */
export function deliveryFlowFile(): string {
  return `${fixtureMeta().sourceDir}/flows/${DELIVERY_FLOW}.yaml`;
}

/**
 * Runs the module's CLI host against the e2e catalog, returning stdout. The catalog reaches it as a reference, the way
 * a deployment's does, so a connection string never lands in an argument list or a failure message.
 */
export function deliveryCli(...args: string[]): string {
  return execFileSync(
    "dotnet",
    [...hostRun(join(moduleRoot, "hosts", "SqlFlow.Delivery.Cli.Host")), "--", ...args, "--db", "${env:SQLFLOW_E2E_CATALOG_CONNECTION}"],
    {
      encoding: "utf8",
      timeout: 400_000,
      env: {
        ...process.env,
        OSDU_DATA_DB: fixtureMeta().dataDb,
        // The ledger these commands read is in the catalog's database, which --db names.
        SQLFLOW_E2E_CATALOG_CONNECTION: E2E.catalogDb,
        // The flow's target: the stand-in platform. An intake sends nothing (the fixture turns the legal check off), but
        // it renders, and a render asks the platform's search for the wellbore each log names.
        ...E2E.osdu,
      },
    },
  );
}

/**
 * The JSON document in what the CLI printed. `dotnet run` builds the host on its way and prints what that says first,
 * so the document is taken from its first brace rather than from the first byte of the output.
 */
export function cliJson<T>(output: string): T {
  const start = output.indexOf("{");
  if (start < 0) {
    throw new Error(`no JSON in the CLI's output: ${output.slice(0, 400)}`);
  }

  return JSON.parse(output.slice(start, output.lastIndexOf("}") + 1)) as T;
}

/** What the CLI said when it refused: the command must fail, and its reason is what the test is about. */
export function cliRefusal(...args: string[]): string {
  try {
    deliveryCli(...args);
  } catch (error) {
    const failure = error as { stderr?: string; stdout?: string; message?: string };
    return `${failure.stderr ?? ""}${failure.stdout ?? ""}${failure.message ?? ""}`;
  }

  throw new Error(`'${args.join(" ")}' was expected to fail and did not.`);
}

/**
 * Stages the well log flow's records in the ledger and returns what the run printed. Records reach the ledger when a
 * submission is planned, which is what an intake does: it renders and stages every record of the scope and sends
 * nothing. A plan run reports what it would do and stages nothing, so a spec that needs the flow's records stages them
 * itself rather than counting on another spec having run first. Staging records already staged plans them again and
 * leaves them pending, so any spec may ask, in any order.
 *
 * The run records itself into the repo the fixture is registered under, like every other run of this estate. Without
 * --repo it would name the repo after the folder it was started from, leaving a second repo called "flows" in the
 * catalog holding a copy of this flow.
 */
export function stageWellLogRecords(): string {
  return deliveryCli("run", deliveryFlowFile(), "--operation", "intake", "--set", `logSource=${LOG_SOURCE}`, "--repo", REPO_NAME);
}

/**
 * Imports the records under <paramref name="records"/> (relative to the OSDU module's folder) into the cache of the
 * partition the cache flow <paramref name="flow"/> of the fixture repository names, as that flow's capture, through the
 * CLI host: the offline path an operator uses, since a refresh would search the estate's OSDU target. Returns what the
 * import printed (JSON). Importing what the cache already holds writes no version.
 */
export function importCacheRecords(flow: string, records: string): string {
  // The records are not repository content: a cache lives in the module's database, so the records that stand in for a
  // capture sit beside the estate rather than inside the source that reads the cache.
  return deliveryCli("cache", "import", `${fixtureMeta().sourceDir}/cache/${flow}.yaml`, "--from-dir", join(moduleRoot, records), "--json");
}
