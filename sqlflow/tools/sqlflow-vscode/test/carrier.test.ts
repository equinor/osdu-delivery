import assert from 'node:assert/strict';
import { createServer, type IncomingMessage, type Server, type ServerResponse } from 'node:http';
import type { AddressInfo } from 'node:net';
import { after, before, test } from 'node:test';
import type { HostMessage, PageMessage } from '../../../gui/src/embed/protocol.ts';
import { Carrier, type CarrierAccess } from '../src/gui/carrier.ts';

// The carrier takes a page's calls to the control plane. A control plane is played here by a server on this machine, so
// what is checked is what really crosses: the credential the control plane sees, and the answer the page gets back.

type Handler = (request: IncomingMessage, response: ServerResponse, body: Buffer) => void;

let server: Server;
let base: string;
let handle: Handler = (_request, response) => response.end();
const seen: { method: string; url: string; headers: IncomingMessage['headers']; body: Buffer }[] = [];

before(async () => {
    server = createServer((request, response) => {
        const chunks: Buffer[] = [];
        request.on('data', (chunk: Buffer) => chunks.push(chunk));
        request.on('end', () => {
            const body = Buffer.concat(chunks);
            seen.push({ method: request.method ?? '', url: request.url ?? '', headers: request.headers, body });
            handle(request, response, body);
        });
    });
    await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve));
    base = `http://127.0.0.1:${(server.address() as AddressInfo).port}`;
});

after(async () => {
    server.closeAllConnections();
    await new Promise((resolve) => server.close(resolve));
});

type CarriedRequest = Extract<PageMessage, { type: 'request' }>;

function request(overrides: Partial<CarriedRequest> = {}): CarriedRequest {
    return { type: 'request', id: 1, method: 'GET', url: `${base}/api/v1/runs`, headers: { Accept: 'application/json' }, stream: false, ...overrides };
}

interface Played {
    carrier: Carrier;
    said: HostMessage[];
    refusals: string[];
    signedOut: number;
}

function carrier(access: Partial<CarrierAccess> = {}, afterRefusal: string | null = null): Played {
    const played: Played = { said: [], refusals: [], signedOut: 0, carrier: undefined as unknown as Carrier };
    played.carrier = new Carrier(
        {
            userAgent: 'test-vscode/1.0.0',
            access: async () => ({ apiBaseUrl: base, token: 'the-credential', ...access }),
            refused: async (token) => {
                played.refusals.push(token);
                return afterRefusal;
            },
            signedOut: () => {
                played.signedOut += 1;
            },
        },
        (message) => played.said.push(message)
    );
    return played;
}

test('a call reaches the control plane under the person\'s credential, and its answer comes back whole', async () => {
    seen.length = 0;
    handle = (_request, response) => {
        response.writeHead(200, { 'Content-Type': 'application/json', 'X-Correlation-ID': 'abc' });
        response.end('{"items":[]}');
    };
    const played = carrier();
    await played.carrier.carry(request({ url: `${base}/api/v1/runs?page=2`, headers: { Accept: 'application/json', 'X-Partition': 'dev' } }));

    assert.equal(seen[0].url, '/api/v1/runs?page=2');
    assert.equal(seen[0].headers.authorization, 'Bearer the-credential');
    assert.equal(seen[0].headers['x-partition'], 'dev');
    assert.equal(seen[0].headers['user-agent'], 'test-vscode/1.0.0');
    const [answer] = played.said;
    assert.ok(answer.type === 'response');
    assert.equal(answer.status, 200);
    assert.equal(answer.headers['x-correlation-id'], 'abc');
    assert.deepEqual(answer.body, { text: '{"items":[]}' });
    assert.equal(answer.streamed, false);
});

test('a page cannot say whom a call is made as', async () => {
    seen.length = 0;
    handle = (_request, response) => response.end();
    const played = carrier();
    await played.carrier.carry(request({ headers: { Authorization: 'Bearer of-the-page', Cookie: 'a=b', Accept: 'application/json' } }));

    assert.equal(seen[0].headers.authorization, 'Bearer the-credential');
    assert.equal(seen[0].headers.cookie, undefined);
});

test('a call for any address but the control plane is never made', async () => {
    seen.length = 0;
    const played = carrier();
    for (const url of ['http://127.0.0.1:1/api/v1/runs', 'https://example.com/api/v1/runs', 'file:///etc/passwd', '/api/v1/runs']) {
        await played.carrier.carry(request({ url }));
    }

    assert.equal(seen.length, 0);
    assert.equal(played.said.length, 4);
    for (const answer of played.said) {
        assert.ok(answer.type === 'failed' && /no other address/.test(answer.reason), JSON.stringify(answer));
    }
});

test('a call by a method the API does not have is never made', async () => {
    seen.length = 0;
    const played = carrier();
    await played.carrier.carry(request({ method: 'TRACE' }));

    assert.equal(seen.length, 0);
    assert.ok(played.said[0].type === 'failed' && /TRACE/.test(played.said[0].reason));
});

test('a body goes out as it was given, text or bytes, and bytes come back as bytes', async () => {
    seen.length = 0;
    handle = (_request, response, body) => {
        response.writeHead(200, { 'Content-Type': 'application/octet-stream' });
        response.end(Buffer.from([...body].reverse()));
    };
    const played = carrier();
    await played.carrier.carry(request({ method: 'POST', headers: { 'Content-Type': 'application/json' }, body: { text: '{"a":1}' } }));
    await played.carrier.carry(request({ id: 2, method: 'POST', headers: { 'Content-Type': 'audio/webm' }, body: { bytes: Uint8Array.from([1, 2, 3]) } }));

    assert.equal(seen[0].body.toString('utf8'), '{"a":1}');
    assert.deepEqual([...seen[1].body], [1, 2, 3]);
    const second = played.said[1];
    assert.ok(second.type === 'response' && second.body !== undefined && 'bytes' in second.body);
    assert.deepEqual([...second.body.bytes], [3, 2, 1]);
});

test('an answer with no body has none', async () => {
    handle = (_request, response) => {
        response.writeHead(204);
        response.end();
    };
    const played = carrier();
    await played.carrier.carry(request({ method: 'DELETE' }));

    assert.ok(played.said[0].type === 'response' && played.said[0].status === 204 && played.said[0].body === undefined);
});

test('a refusal is the answer, with the problem the control plane gave', async () => {
    handle = (_request, response) => {
        response.writeHead(404, { 'Content-Type': 'application/problem+json' });
        response.end('{"title":"Not found","detail":"No run has that id."}');
    };
    const played = carrier();
    await played.carrier.carry(request());

    const [answer] = played.said;
    assert.ok(answer.type === 'response' && answer.status === 404);
    assert.deepEqual(answer.body, { text: '{"title":"Not found","detail":"No run has that id."}' });
    assert.equal(played.refusals.length, 0);
});

test('a credential refused is looked at again, and a renewed one makes the call once more', async () => {
    seen.length = 0;
    handle = (incoming, response) => {
        if (incoming.headers.authorization === 'Bearer renewed') {
            response.writeHead(200, { 'Content-Type': 'application/json' });
            response.end('{"ok":true}');
        } else {
            response.writeHead(401);
            response.end();
        }
    };
    const played = carrier({}, 'renewed');
    await played.carrier.carry(request());

    assert.deepEqual(played.refusals, ['the-credential']);
    assert.equal(seen.length, 2);
    assert.ok(played.said[0].type === 'response' && played.said[0].status === 200);
    assert.equal(played.signedOut, 0);
});

test('a credential refused with none to take its place ends the sign-in, and the page gets the 401', async () => {
    seen.length = 0;
    handle = (_request, response) => {
        response.writeHead(401);
        response.end();
    };
    const played = carrier({}, null);
    await played.carrier.carry(request());

    assert.equal(seen.length, 1);
    assert.ok(played.said[0].type === 'response' && played.said[0].status === 401);
    assert.equal(played.signedOut, 1);
});

test('a call made while nobody is signed in carries no credential, and its 401 asks nobody to look again', async () => {
    seen.length = 0;
    handle = (_request, response) => {
        response.writeHead(401);
        response.end();
    };
    const played = carrier({ token: null });
    await played.carrier.carry(request());

    assert.equal(seen[0].headers.authorization, undefined);
    assert.deepEqual(played.refusals, []);
    assert.equal(played.signedOut, 0);
});

test('a stream arrives in pieces, in order, and ends', async () => {
    handle = (_request, response) => {
        response.writeHead(200, { 'Content-Type': 'text/event-stream' });
        response.write('event: entry\ndata: one\n\n');
        setTimeout(() => {
            response.write('data: two\n\n');
            response.end();
        }, 30);
    };
    const played = carrier();
    await played.carrier.carry(request({ headers: { Accept: 'text/event-stream' }, stream: true }));

    assert.ok(played.said[0].type === 'response' && played.said[0].streamed === true && played.said[0].body === undefined);
    const text = played.said.filter((message) => message.type === 'chunk').map((message) => (message.type === 'chunk' ? message.text : '')).join('');
    assert.equal(text, 'event: entry\ndata: one\n\ndata: two\n\n');
    assert.deepEqual(played.said.at(-1), { type: 'end', id: 1 });
});

test('a stream the control plane refuses is answered whole, as any refusal', async () => {
    handle = (_request, response) => {
        response.writeHead(403, { 'Content-Type': 'application/problem+json' });
        response.end('{"title":"Forbidden"}');
    };
    const played = carrier();
    await played.carrier.carry(request({ headers: { Accept: 'text/event-stream' }, stream: true }));

    assert.equal(played.said.length, 1);
    assert.ok(played.said[0].type === 'response' && played.said[0].streamed === false && played.said[0].status === 403);
});

test('a call the page gives up on is dropped, and says so', async () => {
    let reached: () => void = () => undefined;
    const arrived = new Promise<void>((resolve) => {
        reached = resolve;
    });
    handle = () => reached();
    const played = carrier();
    const carried = played.carrier.carry(request());
    await arrived;
    played.carrier.abort(1);
    await carried;

    assert.deepEqual(played.said, [{ type: 'failed', id: 1, aborted: true, reason: 'The call was dropped.' }]);
});

test('a control plane that does not answer is said to be that, with why', async () => {
    const played = carrier({ apiBaseUrl: 'http://127.0.0.1:1' });
    await played.carrier.carry(request({ url: 'http://127.0.0.1:1/api/v1/runs' }));

    const [answer] = played.said;
    assert.ok(answer.type === 'failed' && !answer.aborted);
    assert.match(answer.reason, /^The control plane did not answer: /);
});

test('a control plane that sends a call elsewhere is not followed there', async () => {
    seen.length = 0;
    handle = (_request, response) => {
        response.writeHead(302, { Location: 'http://127.0.0.1:1/elsewhere' });
        response.end();
    };
    const played = carrier();
    await played.carrier.carry(request());

    assert.equal(seen.length, 1);
    assert.ok(played.said[0].type === 'failed');
});
