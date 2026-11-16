// What a page does that a browser tab answers and an editor's webview does not: opening a page in a tab of its own,
// leaving for an address outside the GUI, and saving a file. A webview is one document with nowhere to go and no
// downloads folder, so each of these is handed to the host (protocol.ts), which opens a tab of its own, the person's
// browser, or a save dialog. They are caught here, at the doors a page leaves by, so no page is written differently for
// being hosted.

import { toast } from "sonner";
import type { PageHost } from "./hostBridge";
import { isGuiPath } from "./protocol";

/** The largest file a page hands its host in one message. A page builds its downloads in memory, well under this. */
const MAX_SAVED_BYTES = 256 * 1024 * 1024;

/** An address as a page wrote it: a page of the GUI, an address outside it, or something held in the page's memory. */
type Target = { kind: "page"; path: string } | { kind: "external"; url: string } | { kind: "held"; url: string } | null;

function targetOf(address: string): Target {
  if (isGuiPath(address)) {
    return { kind: "page", path: address };
  }

  if (/^(blob|data):/i.test(address)) {
    return { kind: "held", url: address };
  }

  return /^(https?|mailto):/i.test(address) ? { kind: "external", url: address } : null;
}

/** A name for a file a page opened rather than named, from what it holds. */
function nameFor(contentType: string): string {
  const extension = contentType.includes("html") ? "html"
    : contentType.includes("json") ? "json"
      : contentType.includes("csv") ? "csv"
        : contentType.startsWith("text/") ? "txt"
          : "bin";
  return `download.${extension}`;
}

/** Catches the ways a page leaves, and hands each to the host. Called once, before the page renders. */
export function installPageDoors(host: PageHost): void {
  const save = async (name: string | null, url: string): Promise<void> => {
    const blob = await (await fetch(url)).blob();
    if (blob.size > MAX_SAVED_BYTES) {
      throw new Error(`The file is ${blob.size} bytes, more than a page hands its host at once.`);
    }

    host.send({
      type: "save",
      name: name ?? nameFor(blob.type),
      contentType: blob.type || "application/octet-stream",
      bytes: new Uint8Array(await blob.arrayBuffer()),
    });
  };

  const saved = (name: string | null, url: string): void => {
    void save(name, url).catch((error: unknown) => {
      toast.error("The file could not be saved", { description: error instanceof Error ? error.message : String(error) });
    });
  };

  const follow = (target: Target, name: string | null): boolean => {
    if (target === null) {
      return false;
    }

    if (target.kind === "page") {
      host.send({ type: "open", path: target.path });
    } else if (target.kind === "external") {
      host.send({ type: "external", url: target.url });
    } else {
      saved(name, target.url);
    }

    return true;
  };

  // A page that opens a window opens a page of the GUI beside this one, an address in the browser, or a document it
  // built, which a webview cannot show in a window of its own and so is saved.
  window.open = (url?: string | URL): WindowProxy | null => {
    if (url !== undefined) {
      follow(targetOf(String(url)), null);
    }

    return null;
  };

  // A download is a link with a `download` name and what it holds, clicked by the page itself or by the person.
  const click = HTMLAnchorElement.prototype.click;
  HTMLAnchorElement.prototype.click = function hostedClick(this: HTMLAnchorElement): void {
    const address = this.getAttribute("href") ?? "";
    if (this.hasAttribute("download") && /^(blob|data):/i.test(address)) {
      saved(this.download === "" ? null : this.download, address);
      return;
    }

    click.call(this);
  };

  const onClick = (event: MouseEvent): void => {
    if (event.defaultPrevented || !(event.target instanceof Element)) {
      return;
    }

    const anchor = event.target.closest("a[href]");
    if (!(anchor instanceof HTMLAnchorElement)) {
      return;
    }

    const address = anchor.getAttribute("href") ?? "";
    const target = targetOf(address);
    if (target === null) {
      return;
    }

    if (anchor.hasAttribute("download") && target.kind === "held") {
      event.preventDefault();
      saved(anchor.download === "" ? null : anchor.download, target.url);
      return;
    }

    // A link to a page is the router's while it is clicked plainly: it shows the page in this tab. Asked for in a tab
    // of its own (a new tab or window in a browser), it is the host's, as an address outside the GUI always is.
    const aside = anchor.target === "_blank" || event.ctrlKey || event.metaKey || event.shiftKey || event.button === 1;
    if (target.kind === "page" && !aside) {
      return;
    }

    event.preventDefault();
    event.stopPropagation();
    follow(target, null);
  };

  // In the capture phase, ahead of the router's own handler on the link, which would otherwise show the page here.
  document.addEventListener("click", onClick, true);
  document.addEventListener("auxclick", onClick, true);
}
