import assert from 'node:assert/strict';
import { posix, win32 } from 'node:path';
import { test } from 'node:test';
import { claudeServerJson, findClaude, registrationsOf } from '../src/mcp/claudeCode.ts';

test('Claude Code is found on the PATH, then where its installers put it', () => {
    const found = (have: string[]) => (file: string) => have.includes(file);
    assert.deepEqual(findClaude({ PATH: '/usr/bin:/opt/claude' }, 'linux', '/h', found(['/opt/claude/claude'])), { program: '/opt/claude/claude', prefix: [] });
    assert.deepEqual(findClaude({ PATH: '/usr/bin' }, 'darwin', '/h', found([posix.join('/h', '.local', 'bin', 'claude')])), { program: posix.join('/h', '.local', 'bin', 'claude'), prefix: [] });
    assert.equal(findClaude({ PATH: '/usr/bin' }, 'linux', '/h', found([])), undefined);
});

test('on Windows an exe is run as it is, and npm\'s claude.cmd through Node with the script it runs', () => {
    const exe = win32.join('C:\\WinGet\\Links', 'claude.exe');
    assert.deepEqual(findClaude({ Path: 'C:\\WinGet\\Links' }, 'win32', 'C:\\Users\\Ada', (file) => file === exe), { program: exe, prefix: [] });

    const npm = 'C:\\Users\\Ada\\AppData\\Roaming\\npm';
    const script = win32.join(npm, 'node_modules', '@anthropic-ai', 'claude-code', 'cli.js');
    const node = win32.join('C:\\Program Files\\nodejs', 'node.exe');
    const have = [win32.join(npm, 'claude.cmd'), script, node];
    assert.deepEqual(
        findClaude({ PATH: `${npm};C:\\Program Files\\nodejs` }, 'win32', 'C:\\Users\\Ada', (file) => have.includes(file)),
        { program: node, prefix: [script] }
    );
    // A claude.cmd whose script is not where npm puts it is not run through a shell.
    assert.equal(findClaude({ PATH: npm }, 'win32', 'C:\\Users\\Ada', (file) => file === win32.join(npm, 'claude.cmd')), undefined);
});

test('the server\'s install answer gives each registration its name, program and arguments', () => {
    const answer = '{\n  "mcpServers": {\n    "sqlflow-data": {\n      "command": "C:\\\\Users\\\\Ada\\\\.sqlflow\\\\bin\\\\sqlflow-mcp.exe",\n      "args": ["--mode", "data"],\n      "env": { "SQLFLOW_CONTROL_PLANE_URL": "http://localhost:8080" }\n    }\n  }\n}';
    assert.deepEqual(registrationsOf(answer), [
        { name: 'sqlflow-data', command: 'C:\\Users\\Ada\\.sqlflow\\bin\\sqlflow-mcp.exe', args: ['--mode', 'data'] },
    ]);
    assert.throws(() => registrationsOf('Unknown client'), /not JSON/);
    assert.throws(() => registrationsOf('{"mcpServers":{"x":{}}}'), /no program/);
});

test('Claude Code is given a stdio server with the extension\'s environment, not the install answer\'s example', () => {
    const json = claudeServerJson(
        { name: 'sqlflow-estate', command: '/h/.sqlflow/bin/sqlflow-mcp', args: ['--mode', 'estate'] },
        { SQLFLOW_CONTROL_PLANE_URL: 'https://control.example.com' }
    );
    assert.deepEqual(JSON.parse(json), {
        type: 'stdio',
        command: '/h/.sqlflow/bin/sqlflow-mcp',
        args: ['--mode', 'estate'],
        env: { SQLFLOW_CONTROL_PLANE_URL: 'https://control.example.com' },
    });
});
