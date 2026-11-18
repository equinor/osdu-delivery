// How a control plane's address is shown to a person: its name first, the part they tell estates apart by, and the rest
// of the address under it. A cloud host's address is long and the same at its end for every estate of one platform, so
// its first label is the name; a machine on the network or the person's own (an IP address, localhost) is its whole
// address. Kept free of imports, so the unit suite reads it.

export interface ControlPlaneAddress {
  /** What tells this control plane from another: a host's first label, or a local address whole, with its port. */
  name: string;
  /** The rest of the address after the name, with its port and path; null when the name is all of it. */
  rest: string | null;
  /** Whether the address is https. */
  secure: boolean;
}

const IPV4 = /^\d{1,3}(\.\d{1,3}){3}$/;

/** The address as a person reads it. What is no absolute address is shown as it was given, as a name alone. */
export function controlPlaneAddress(url: string): ControlPlaneAddress {
  let parsed: URL;
  try {
    parsed = new URL(url);
  } catch {
    return { name: url.trim(), rest: null, secure: false };
  }

  const port = parsed.port === "" ? "" : `:${parsed.port}`;
  const path = parsed.pathname.replace(/\/+$/, "");
  const host = parsed.hostname;
  const secure = parsed.protocol === "https:";
  // An IPv6 host comes back bracketed; it, an IPv4 address and a host with no dot have no first label to stand for them.
  if (host.startsWith("[") || IPV4.test(host) || !host.includes(".")) {
    return { name: `${host}${port}`, rest: path === "" ? null : path, secure };
  }

  const dot = host.indexOf(".");
  return { name: host.slice(0, dot), rest: `${host.slice(dot + 1)}${port}${path}`, secure };
}
