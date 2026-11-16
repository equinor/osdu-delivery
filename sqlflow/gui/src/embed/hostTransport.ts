// A hosted page's calls to the control plane, carried by its host (protocol.ts). The page's own origin is not one the
// control plane's CORS allows, and the person's credential stays with the host, so the API client (api/client.ts) hands
// each call to this transport in place of the browser's fetch and gets back a Response it reads as it reads any other:
// the same status, headers and body, and a body that arrives in pieces where the call asked for a stream.
// Takes its host as an argument and touches nothing a browser alone has, so the unit suite plays a host to it.

import { carriedBytes, type CarriedBody, type HostMessage, type PageMessage } from "./protocol.ts";

/** A call as the API client hands it over, answered as fetch answers one: the client's `ApiTransport`. */
export type CarriedCall = (url: URL, init: RequestInit) => Promise<Response>;

/** The part of a page's host the transport talks through. */
export interface TransportHost {
  send(message: PageMessage): void;
  listen(listener: (message: HostMessage) => void): () => void;
}

interface Waiting {
  answered: (response: Response) => void;
  failed: (error: unknown) => void;
  /** The body of a streamed answer, fed as its pieces arrive; null until the answer's head is in, and for one not streamed. */
  stream: ReadableStreamDefaultController<Uint8Array> | null;
  /** Lets go of the call's abort signal. */
  release: () => void;
}

/** Statuses a Response may not be built with a body for. */
const BODILESS = new Set([101, 204, 205, 304]);

function abortError(): DOMException {
  return new DOMException("The call was aborted.", "AbortError");
}

function headersOf(init: RequestInit): Record<string, string> {
  const headers: Record<string, string> = {};
  new Headers(init.headers ?? undefined).forEach((value, name) => {
    headers[name] = value;
  });
  return headers;
}

async function carried(body: RequestInit["body"]): Promise<CarriedBody | undefined> {
  if (body === null || body === undefined) {
    return undefined;
  }

  if (typeof body === "string") {
    return { text: body };
  }

  if (body instanceof Blob) {
    return { bytes: new Uint8Array(await body.arrayBuffer()) };
  }

  if (body instanceof ArrayBuffer) {
    return { bytes: new Uint8Array(body) };
  }

  if (ArrayBuffer.isView(body)) {
    return { bytes: new Uint8Array(body.buffer, body.byteOffset, body.byteLength) };
  }

  throw new TypeError("A hosted page's call carries text or bytes; this body is neither.");
}

function bodyOf(body: CarriedBody | undefined): string | Blob | null {
  if (body === undefined) {
    return null;
  }

  return "text" in body ? body.text : new Blob([new Uint8Array(carriedBytes(body.bytes))]);
}

/**
 * The transport over one host. Every call is numbered; the host answers by number, so calls in flight at once never
 * read each other's answers, and a call the page gives up on is dropped here and at the host.
 */
export function hostTransport(host: TransportHost): CarriedCall {
  const waiting = new Map<number, Waiting>();
  const encoder = new TextEncoder();
  let nextId = 1;

  const settle = (id: number): Waiting | undefined => {
    const call = waiting.get(id);
    if (call !== undefined) {
      waiting.delete(id);
      call.release();
    }

    return call;
  };

  host.listen((message) => {
    switch (message.type) {
      case "response": {
        const call = waiting.get(message.id);
        if (call === undefined) {
          return;
        }

        const init: ResponseInit = { status: message.status, statusText: message.statusText, headers: message.headers };
        if (!message.streamed) {
          settle(message.id);
          call.answered(new Response(BODILESS.has(message.status) ? null : bodyOf(message.body), init));
          return;
        }

        // The call stays in the table until its body ends: the pieces that follow are fed to this stream by its number,
        // and a reader that stops reading (the client's abort signal cancels the read) tells the host to stop sending.
        const body = new ReadableStream<Uint8Array>({
          start: (controller) => {
            call.stream = controller;
          },
          cancel: () => {
            if (settle(message.id) !== undefined) {
              host.send({ type: "abort", id: message.id });
            }
          },
        });
        call.answered(new Response(body, init));
        return;
      }

      case "chunk":
        waiting.get(message.id)?.stream?.enqueue(encoder.encode(message.text));
        return;

      case "end":
        settle(message.id)?.stream?.close();
        return;

      case "failed": {
        const call = settle(message.id);
        if (call === undefined) {
          return;
        }

        const error = message.aborted ? abortError() : new TypeError(message.reason);
        if (call.stream !== null) {
          call.stream.error(error);
        } else {
          call.failed(error);
        }

        return;
      }

      default:
        return;
    }
  });

  return async (url, init) => {
    const signal = init.signal ?? null;
    if (signal?.aborted) {
      throw abortError();
    }

    const body = await carried(init.body);
    const headers = headersOf(init);
    const id = nextId++;
    return new Promise<Response>((answered, failed) => {
      const onAbort = () => {
        const call = settle(id);
        if (call === undefined) {
          return;
        }

        host.send({ type: "abort", id });
        if (call.stream !== null) {
          call.stream.error(abortError());
        } else {
          failed(abortError());
        }
      };

      signal?.addEventListener("abort", onAbort, { once: true });
      waiting.set(id, {
        answered,
        failed,
        stream: null,
        release: () => signal?.removeEventListener("abort", onAbort),
      });
      host.send({
        type: "request",
        id,
        method: (init.method ?? "GET").toUpperCase(),
        url: url.toString(),
        headers,
        body,
        stream: (headers.accept ?? "").includes("text/event-stream"),
      });
    });
  };
}
