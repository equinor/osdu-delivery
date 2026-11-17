import * as path from 'path';
import * as vscode from 'vscode';
import { activateHost, deactivateHost, VscodeHost } from './host';

/** The Cargo target beside this folder, where a checkout's builds land. */
function buildFolders(extensionPath: string): string[] {
    return ['release', 'debug'].map((profile) => path.join(extensionPath, '..', 'target', profile));
}

/** SQLFlow's own extension: the host with no modules. */
const SQLFLOW: VscodeHost = {
    product: 'SQLFlow',
    mcp: {
        program: 'sqlflow-mcp',
        providerId: 'sqlflow.mcp',
        searchPaths: buildFolders,
    },
    lspSearchPaths: buildFolders,
    censusDirectories: () => [],
    docCorpora: (extensionPath) => [path.join(extensionPath, 'dist', 'reference')],
    skeletons: [
        {
            label: 'Ingestion flow',
            detail: 'flowType: ing, table to table',
            content: 'flowType: ing\nname: newFlow\nsource:\n  server: src\n  table: dbo.source\ntarget:\n  server: dwh\n  table: stg.target\nload:\n  mode: append\n',
        },
        {
            label: 'File flow',
            detail: 'Files into a table',
            content: 'name: newFlow\nsource:\n  type: csv\n  path: ./data/*.csv\ntarget:\n  server: dwh\n  table: stg.newFlow\n',
        },
        {
            label: 'Lineage description',
            detail: 'flowType: lin, a step another system runs, for lineage only and never executed',
            content: 'flowType: lin\nname: newStep-lin\ndescription: What the step does, in a sentence.\nsystem: databricks\nreads:\n  - { name: schema.source, as: src, origin: true }\nwrites:\n  - feeds: newFlow\n    columns:\n      id: src.id\ncontext: |\n  Which rows are left out or added and why, how keys are formed, and when the step runs.\n',
        },
        {
            label: 'Report',
            detail: 'flowType: rpt, read-only queries shown as figures, charts and tables, run on demand and never as a flow',
            content: 'flowType: rpt\nname: newReport-rpt\ntitle: What the report shows\nconnection: ${env:SQLFLOW_DW}\nparameters:\n  days:\n    label: Days back\n    type: integer\n    default: 30\nqueries:\n  per_day:\n    title: Rows loaded per day\n    sql: |\n      SELECT CAST(LoadedUtc AS date) AS Day, COUNT(*) AS Loaded\n      FROM stg.newFlow\n      WHERE LoadedUtc >= DATEADD(day, -@days, SYSUTCDATETIME())\n      GROUP BY CAST(LoadedUtc AS date)\n      ORDER BY Day\n    show:\n      - { type: column, by: Day, values: Loaded }\n      - table\n',
        },
        {
            label: 'Schedule library',
            detail: 'schedules.yaml, named schedules flows join',
            content: 'schedules:\n  nightly:\n    cron: "0 1 * * *"\n    timezone: UTC\n',
        },
        {
            label: 'AI context',
            detail: '*.ai-context.yaml, what an assistant may use once an admin approves it',
            content: 'aiContext: sales\ndescription: What the context covers\ntables:\n  - table: dw.sales.Orders\n    name: Order lines\n    description: One row per order line.\n',
        },
    ],
    cliVerbs: [
        {
            verb: 'validate',
            label: 'Validate',
            detail: 'Parse and validate this document through the loader a run uses, offline.',
            applies: () => true,
        },
    ],
};

export async function activate(context: vscode.ExtensionContext): Promise<void> {
    await activateHost(context, SQLFLOW);
}

export async function deactivate(): Promise<void> {
    await deactivateHost();
}
