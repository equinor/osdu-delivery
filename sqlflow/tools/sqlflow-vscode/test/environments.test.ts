import assert from 'node:assert/strict';
import { test } from 'node:test';
import { probeControlPlane, reachOf, readEnvironmentRequest, RequestRefused } from '../src/gui/environmentCalls.ts';

// What the side bar asks of the person's environments is read as coming from anyone: only in the protocol's shape.
test('a request is read only in the shape the protocol gives it', () => {
    assert.deepEqual(readEnvironmentRequest({ kind: 'use', url: 'https://cp.example.com' }), { kind: 'use', url: 'https://cp.example.com' });
    assert.deepEqual(readEnvironmentRequest({ kind: 'remove', url: 'https://cp.example.com' }), { kind: 'remove', url: 'https://cp.example.com' });
    assert.deepEqual(readEnvironmentRequest({ kind: 'probe', url: 'x' }), { kind: 'probe', url: 'x' });
    const environment = { name: 'prod', url: 'https://cp.example.com', stage: 'production', guiUrl: null };
    assert.deepEqual(
        readEnvironmentRequest({ kind: 'save', replaces: null, environment, use: true, extra: 'ignored' }),
        { kind: 'save', replaces: null, environment, use: true }
    );
    // Only the fields an environment has are taken from what was sent.
    assert.deepEqual(
        readEnvironmentRequest({ kind: 'save', replaces: 'https://old.example.com', environment: { ...environment, token: 'x' }, use: false }),
        { kind: 'save', replaces: 'https://old.example.com', environment, use: false }
    );
});

test('a request in another shape is refused, saying what it lacked', () => {
    const refused = (raw: unknown, pattern: RegExp): void => {
        assert.throws(() => readEnvironmentRequest(raw), (error: unknown) => error instanceof RequestRefused && pattern.test(error.message));
    };
    refused(null, /not one the editor reads/);
    refused({ kind: 'purge' }, /does not do to environments/);
    refused({ kind: 'use' }, /no address/);
    refused({ kind: 'use', url: 42 }, /no address/);
    refused({ kind: 'save', replaces: 7, environment: {}, use: true }, /not an address/);
    refused({ kind: 'save', replaces: null, environment: { name: 'x', url: 'y', stage: 'production', guiUrl: null } }, /whether to work against/);
    refused({ kind: 'save', replaces: null, environment: { name: 'x', url: 'y', stage: 'live', guiUrl: null }, use: true }, /no stage/);
    refused({ kind: 'save', replaces: null, environment: { name: 'x', url: 'y', stage: 'test', guiUrl: 3 }, use: true }, /GUI address/);
    refused({ kind: 'save', replaces: null, environment: 'prod', use: true }, /no environment/);
});

test('an address answers as a control plane when it sends its sign-in methods', () => {
    assert.deepEqual(reachOf(200, { local: false, bootstrap: false, entra: { enabled: true } }), {
        answers: true,
        detail: 'It answers as a control plane, signing in with Microsoft Entra.',
    });
    assert.match(reachOf(200, { local: true, bootstrap: true, entra: { enabled: true } }).detail, /Microsoft Entra or an account/);
    assert.match(reachOf(200, { local: true, bootstrap: false, entra: { enabled: false } }).detail, /with an account/);
    assert.match(reachOf(200, { local: false, bootstrap: true, entra: { enabled: false } }).detail, /an access token/);
});

test('anything else at an address is not a control plane, and says what it is likely to be', () => {
    assert.deepEqual(reachOf(404, undefined).answers, false);
    assert.match(reachOf(404, undefined).detail, /HTTP 404/);
    assert.match(reachOf(502, { error: 'x' }).detail, /HTTP 502/);
    assert.match(reachOf(200, '<html>').detail, /not as a control plane.*GUI's address/);
    assert.match(reachOf(200, { local: true }).detail, /not as a control plane/);
});

test('a probe asks the sign-in methods, sends no credential, and says why an address did not answer', async () => {
    const asked: { url: string; headers: Record<string, string> }[] = [];
    const answering: typeof fetch = async (input, init) => {
        asked.push({ url: String(input), headers: init?.headers as Record<string, string> });
        return new Response(JSON.stringify({ local: true, bootstrap: false, entra: { enabled: false } }), { status: 200 });
    };
    const reach = await probeControlPlane('https://cp.example.com/sqlflow/', 'probe/1', answering);
    assert.deepEqual(reach, { answers: true, detail: 'It answers as a control plane, signing in with an account.' });
    assert.equal(asked[0].url, 'https://cp.example.com/sqlflow/api/v1/auth/providers');
    assert.deepEqual(Object.keys(asked[0].headers).sort(), ['accept', 'user-agent']);

    const refusing: typeof fetch = async () => {
        throw Object.assign(new TypeError('fetch failed'), { cause: { code: 'ENOTFOUND' } });
    };
    const silent = await probeControlPlane('https://nowhere.example.com', 'probe/1', refusing);
    assert.equal(silent.answers, false);
    assert.match(silent.detail, /does not answer: fetch failed \(ENOTFOUND\)/);

    // An address that never answers. Its open connection is what keeps the process waiting, as a real socket does: the
    // probe's own timeout (AbortSignal.timeout) holds nothing open, so without it the process would end first.
    const slow: typeof fetch = async (_input, init) => new Promise((_resolve, reject) => {
        const connection = setTimeout(() => reject(new Error('the probe never gave up on an address that does not answer')), 5000);
        init?.signal?.addEventListener('abort', () => {
            clearTimeout(connection);
            reject(init.signal?.reason);
        });
    });
    const late = await probeControlPlane('https://slow.example.com', 'probe/1', slow, 20);
    assert.match(late.detail, /nothing came back/);

    const notJson: typeof fetch = async () => new Response('<html></html>', { status: 200 });
    assert.match((await probeControlPlane('https://gui.example.com', 'probe/1', notJson)).detail, /GUI's address/);
});

test('a probe of what is no address asks nothing, and says why', async () => {
    let called = false;
    const never: typeof fetch = async () => {
        called = true;
        return new Response('{}');
    };
    const reach = await probeControlPlane('https://me:pw@cp.example.com', 'probe/1', never);
    assert.equal(called, false);
    assert.deepEqual(reach, { answers: false, detail: 'An address never carries a user name or a password.' });
});
