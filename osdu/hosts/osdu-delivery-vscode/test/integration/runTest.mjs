import { runTests } from '@vscode/test-electron';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { checkToolCalls } from './toolCalls.mjs';

// Runs the extension in a real editor, with the real language server and MCP server, over the welldb sample: what the
// unit tests cannot reach is whether the manifest, the activation and the servers fit together. It needs the two
// programs built (npm run bundle stages them) and downloads an editor once, into .vscode-test.
const here = dirname(fileURLToPath(import.meta.url));
const extensionDevelopmentPath = join(here, '..', '..');
const workspace = join(extensionDevelopmentPath, '..', '..', 'samples', 'welldb');
await checkToolCalls(extensionDevelopmentPath);

const home = mkdtempSync(join(tmpdir(), 'osdu-delivery-vscode-'));

try {
    await runTests({
        extensionDevelopmentPath,
        extensionTestsPath: join(here, 'suite.cjs'),
        launchArgs: [workspace, '--disable-extensions', `--user-data-dir=${join(home, 'data')}`],
        extensionTestsEnv: {
            // The sign-in and the settings of whoever runs this stay out of it, and the server reaches nothing.
            HOME: home,
            USERPROFILE: home,
            SQLFLOW_CONTROL_PLANE_URL: 'http://127.0.0.1:1',
            SQLFLOW_CREDENTIALS_FILE: join(home, 'credentials.json'),
            // A run may name a control plane to check the real answers (test/integration/online.cjs).
            ...(process.env.OSDU_VSCODE_TEST_CONTROL_PLANE
                ? {
                    SQLFLOW_CONTROL_PLANE_URL: process.env.OSDU_VSCODE_TEST_CONTROL_PLANE,
                    SQLFLOW_CONTROL_PLANE_TOKEN: process.env.OSDU_VSCODE_TEST_TOKEN,
                    SQLFLOW_GUI_URL: process.env.OSDU_VSCODE_TEST_GUI,
                }
                : {}),
        },
    });
} finally {
    rmSync(home, { recursive: true, force: true, maxRetries: 3 });
}
