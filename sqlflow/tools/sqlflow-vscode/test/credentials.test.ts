import assert from 'node:assert/strict';
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';
import { credentialFile, credentialKey, currentCredential, fileCredential, tokenExpiry } from '../src/gui/credentials.ts';

// The fixture the MCP server's reader and the command line's are held to (sqlflow-mcp/tests/shared-credentials.json):
// this reader is the third, and reads the same file.
const here = dirname(fileURLToPath(import.meta.url));
const shared = join(here, '..', '..', 'sqlflow-mcp', 'tests', 'shared-credentials.json');

function jwt(claims: Record<string, unknown>): string {
    const part = (value: unknown): string => Buffer.from(JSON.stringify(value)).toString('base64url');
    return `${part({ alg: 'none' })}.${part(claims)}.signature`;
}

test('the shared file gives a control plane its own credential, a token or a session', () => {
    assert.equal(fileCredential(shared, 'https://control.example.com'), 'sqlf_a-personal-access-token');
    assert.equal(fileCredential(shared, 'http://localhost:5000'), 'a.session.token');
    assert.equal(fileCredential(shared, 'https://another.example.com'), null);
});

test('one key names a control plane however its address is written', () => {
    for (const written of ['https://control.example.com', 'https://CONTROL.example.com/', 'https://control.example.com:443', 'HTTPS://control.example.com//']) {
        assert.equal(credentialKey(written), 'https://control.example.com', written);
    }
    assert.equal(credentialKey('http://localhost:5000/'), 'http://localhost:5000');
    assert.equal(credentialKey('https://example.com/SqlFlow/'), 'https://example.com/sqlflow');
    assert.equal(credentialKey('not an address'), 'not an address');
});

test('a file that is missing, is not JSON or holds something else gives nothing', () => {
    const folder = mkdtempSync(join(tmpdir(), 'sqlflow-credentials-'));
    try {
        assert.equal(fileCredential(join(folder, 'none.json'), 'http://localhost:5000'), null);
        const files: [string, string][] = [
            ['broken.json', '{'],
            ['list.json', '[]'],
            ['empty-token.json', '{"http://localhost:5000":{"token":""}}'],
            ['wrong.json', '{"http://localhost:5000":"token"}'],
        ];
        for (const [name, content] of files) {
            writeFileSync(join(folder, name), content);
            assert.equal(fileCredential(join(folder, name), 'http://localhost:5000'), null, name);
        }
    } finally {
        rmSync(folder, { recursive: true, force: true });
    }
});

test('a token says when it ends only when it is one that does', () => {
    assert.equal(tokenExpiry(jwt({ exp: 1_900_000_000 })), 1_900_000_000_000);
    assert.equal(tokenExpiry('sqlf_an-access-token'), undefined);
    assert.equal(tokenExpiry(jwt({ sub: 'alice' })), undefined);
    assert.equal(tokenExpiry('a.b.c'), undefined);
});

test('the environment\'s credential comes first while it has not lapsed, then the file\'s', () => {
    const now = 1_800_000_000_000;
    const file = { SQLFLOW_CREDENTIALS_FILE: shared };
    const live = jwt({ exp: now / 1000 + 600 });
    const lapsed = jwt({ exp: now / 1000 + 10 });

    assert.equal(currentCredential('http://localhost:5000', file, now), 'a.session.token');
    assert.equal(currentCredential('http://localhost:5000', { ...file, SQLFLOW_CONTROL_PLANE_TOKEN: live }, now), live);
    assert.equal(currentCredential('http://localhost:5000', { ...file, SQLFLOW_CONTROL_PLANE_TOKEN: 'sqlf_never-lapses' }, now), 'sqlf_never-lapses');
    // One within its last half minute would lapse on the way, so the stored sign-in is not shadowed by it.
    assert.equal(currentCredential('http://localhost:5000', { ...file, SQLFLOW_CONTROL_PLANE_TOKEN: lapsed }, now), 'a.session.token');
    assert.equal(currentCredential('https://another.example.com', file, now), null);
});

test('the file is where the environment says, else in the person\'s own folder', () => {
    assert.equal(credentialFile({ SQLFLOW_CREDENTIALS_FILE: ' /somewhere/credentials.json ' }), '/somewhere/credentials.json');
    assert.match(credentialFile({}), /[\\/]\.sqlflow[\\/]credentials\.json$/);
});
