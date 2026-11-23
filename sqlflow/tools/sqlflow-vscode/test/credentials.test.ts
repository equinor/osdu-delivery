import assert from 'node:assert/strict';
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';
import { controlPlaneKey } from '../../../gui/src/embed/environments.ts';
import { credentialFile, credentialKey, currentCredential, fileCredential, heldSignIn, readCredentialFile, tokenExpiry } from '../src/gui/credentials.ts';

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

test('a kept sign-in is shown for a control plane while it has not ended, with who it is', () => {
    const now = Date.parse('2026-10-30T11:30:00Z');
    const entries = {
        'https://control.example.com': { token: 'sqlf_a-personal-access-token', username: 'alice', expiresUtc: '2027-01-28T10:00:00.0000000Z' },
        'http://localhost:5000': { token: jwt({ sub: 'bob@example.com', exp: now / 1000 + 3600 }), renews: true },
        'https://ended.example.com': { token: 'x', username: 'carol', expiresUtc: '2026-10-30T11:00:00Z' },
        'https://ending.example.com': { token: jwt({ sub: 'dave', exp: now / 1000 + 10 }) },
        'https://forever.example.com': { token: 'sqlf_no-end' },
        'https://empty.example.com': { token: '' },
    };
    assert.deepEqual(heldSignIn(entries, 'https://CONTROL.example.com/', now), { account: 'alice' });
    assert.deepEqual(heldSignIn(entries, 'http://localhost:5000', now), { account: 'bob@example.com' });
    assert.equal(heldSignIn(entries, 'https://ended.example.com', now), null);
    // Inside the floor a server passes a credential over, it is as good as ended.
    assert.equal(heldSignIn(entries, 'https://ending.example.com', now), null);
    assert.deepEqual(heldSignIn(entries, 'https://forever.example.com', now), { account: null });
    assert.equal(heldSignIn(entries, 'https://empty.example.com', now), null);
    assert.equal(heldSignIn(entries, 'https://unknown.example.com', now), null);
});

test('the shared file is read whole, and a missing or broken one holds nothing', () => {
    const folder = mkdtempSync(join(tmpdir(), 'sqlflow-held-'));
    try {
        assert.deepEqual(readCredentialFile(join(folder, 'missing.json')), {});
        const broken = join(folder, 'broken.json');
        writeFileSync(broken, '[1, 2]');
        assert.deepEqual(readCredentialFile(broken), {});
        assert.deepEqual(Object.keys(readCredentialFile(shared)).sort(), ['http://localhost:5000', 'https://control.example.com']);
    } finally {
        rmSync(folder, { recursive: true, force: true });
    }
});

test('an environment and the sign-in kept for it are found by the same key', () => {
    for (const written of ['https://control.example.com', 'https://CONTROL.example.com/', 'https://control.example.com:443', 'http://localhost:5000/', 'https://example.com/SqlFlow/', ' https://example.com/x ']) {
        assert.equal(controlPlaneKey(written), credentialKey(written.trim()), written);
    }
});
