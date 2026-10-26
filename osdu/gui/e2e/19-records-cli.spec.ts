import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { DELIVERY_FLOW, INTERFACES_FLOW } from "./global-setup";
import { cliJson as json, cliRefusal, deliveryCli as cli, deliveryFlowFile, fixtureMeta, stageWellLogRecords } from "./delivery-cli";
import { expect, expectRecordsWorkbook, test } from "./helpers";

// A record's history where an operator already is. The GUI and the API have shown a record's attempts from the start;
// `sqlflow records` shows the same ledger from a terminal or a node, with no control plane to reach. The spec stages the
// records it reads itself (an intake), so it reads them whatever ran before it.

test.describe.serial("records from the CLI", () => {
  test("the ledger's records and one record's attempts are readable from the command line", () => {
    test.setTimeout(600_000);
    const flow = deliveryFlowFile();

    // Records reach the ledger when a submission is planned, which is what an intake does: it renders and stages every
    // record of the scope and sends nothing.
    expect(stageWellLogRecords()).toContain("record(s)");

    const listed = json<{
      flow: string;
      flowId: string;
      records: { deliveryKey: string; sourceKey: string; status: string; targetId: string | null }[];
    }>(cli("records", "list", flow, "--json"));
    expect(listed.flow).toBe(DELIVERY_FLOW);
    expect(listed.records.length).toBeGreaterThan(0);
    // A record the intake staged is pending: its document is queued and nothing has been sent.
    const first = listed.records.find((record) => /^pending$/i.test(record.status));
    if (first === undefined) {
      throw new Error(`the intake staged no pending record: ${JSON.stringify(listed.records.map((r) => [r.sourceKey, r.status]))}`);
    }
    expect(first.deliveryKey).toMatch(/^[0-9a-f]{32}$/);
    // A planned record already claims the OSDU id it will land as: the id is given at plan time, not at delivery.
    expect(first.targetId).toContain("work-product-component--WellLog");

    // The source key is what an operator holds, so it finds the record as surely as the delivery key does.
    const shown = json<{
      record: { deliveryKey: string; sourceKey: string; status: string; attempts: unknown[] };
    }>(cli("records", "show", flow, "--key", first.sourceKey, "--json"));
    expect(shown.record.deliveryKey).toBe(first.deliveryKey);
    expect(shown.record.sourceKey).toBe(first.sourceKey);
    expect(Array.isArray(shown.record.attempts)).toBe(true);

    // The plain listing is a person's view of the same thing, and a status the ledger does not know is refused.
    expect(cli("records", "list", flow, "--status", "pending")).toContain(first.sourceKey);
    expect(cliRefusal("records", "list", flow, "--status", "nonsense")).toMatch(/not a record status/);

    // The same records as the Excel workbook the Records tab's Export gives, every one of them, written to --out.
    const every = json<{ records: unknown[] }>(cli("records", "list", flow, "--max", "1000", "--json"));
    const folder = mkdtempSync(join(tmpdir(), "records-export-"));
    try {
      const out = join(folder, "welllogs.xlsx");
      const exported = json<{ flow: string; file: string; records: number }>(cli("records", "export", flow, "--out", out, "--json"));
      expect(exported.flow).toBe(DELIVERY_FLOW);
      expect(resolve(exported.file)).toBe(resolve(out));
      expect(exported.records).toBe(every.records.length);
      expectRecordsWorkbook(out);
      expect(cli("records", "export", flow, "--status", "pending", "--out", out)).toMatch(/wrote \d+ record\(s\) to /);

      // A workbook needs a file to go to, and one Excel opens it by.
      expect(cliRefusal("records", "export", flow)).toMatch(/--out <file\.xlsx>/);
      expect(cliRefusal("records", "export", flow, "--out", join(folder, "welllogs.csv"))).toMatch(/ending in \.xlsx/);
    } finally {
      rmSync(folder, { recursive: true, force: true });
    }
  });

  test("a source is read one interface at a time, and an unknown interface says which there are", () => {
    const flow = `${fixtureMeta().sourceDir}/flows/${INTERFACES_FLOW}.yaml`;

    // A source delivers several interfaces, so a command that names none cannot tell which ledger it means.
    expect(cliRefusal("records", "list", flow)).toMatch(/delivers 4 interfaces/);
    expect(cliRefusal("records", "list", flow, "--interface", "nope")).toMatch(/has no interface 'nope'/);

    const listed = json<{ flow: string; records: unknown[] }>(cli("records", "list", flow, "--interface", "wellbores", "--json"));
    expect(listed.flow).toBe(`${INTERFACES_FLOW} / wellbores`);
    // The source's own ledgers are its own: nothing is read from the single-form flows beside it.
    expect(listed.records).toHaveLength(0);
  });
});
