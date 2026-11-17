# OSDU Delivery, powered by SQLFlow

**Get any data into an OSDU® data platform. Configured, not coded.**

Wells, wellbores, well logs, reference data, files: you describe each delivery in a few YAML files, and OSDU Delivery
turns the source rows into records, sends them in the order the platform needs, and keeps a ledger of every record that
lands. This extension is where you write those files, with every record in view before it is sent.

![Even a small change is a development cycle; with OSDU Delivery you describe the data flow instead, with no new build and no new deployment](https://raw.githubusercontent.com/TahirRiaz/osdu-delivery/main/osdu/hosts/osdu-delivery-vscode/media/process-change.png)

## Why it pays off

- **A change is an edit, not a release.** A new source, a new template version or a corrected property is one reviewed
  edit to one file. No new build, no new deployment.
- **No surprises in the platform.** A mapping previews as you type: the rows it reads, the record each one becomes, and
  what fills every property of its template. Only records that really changed are sent.
- **Every record accounted for.** The ledger holds each record's source file and row, the mapping that rendered it,
  every attempt, and the id and version it landed as. Assertions check what the platform holds against the source.
- **An assistant that knows your estate.** Ask the editor's chat, Claude Code, Cursor or Codex about flows, runs and
  lineage. It reads the estate's metadata, never your records, unless you ask a data question an administrator has
  opened.

![A mapping's overview: the tables it reads and the reference data it looks up, joined to the template property each one fills](https://raw.githubusercontent.com/TahirRiaz/osdu-delivery/main/osdu/hosts/osdu-delivery-vscode/media/mapping-overview.png)

## Get started

1. Open the folder of a delivery repository.
2. Run **OSDU Delivery: Set Control Plane URL**, then **OSDU Delivery: Sign In with the Browser**.
3. Open a mapping and press Ctrl+K V to see the records it makes.

Completion, hover and problems work offline, with no sign-in. VS Code 1.101 or later, on Windows, Linux and macOS; the
servers and the command line come in the package, signed. No telemetry.

## Open source

OSDU Delivery is open source, under the GNU GPL 3.0 or later.

- [TahirRiaz/osdu-delivery](https://github.com/TahirRiaz/osdu-delivery): the source,
  [the guide](https://github.com/TahirRiaz/osdu-delivery/blob/main/osdu/docs/reference/guides/vscode.md), and
  [issues](https://github.com/TahirRiaz/osdu-delivery/issues).
- [equinor/osdu-delivery](https://github.com/equinor/osdu-delivery): Equinor's fork.

## Trademarks

OSDU is a registered trademark of The Open Group. OSDU Delivery is an independent product built on SQLFlow, and is
not affiliated with, sponsored by, endorsed by or certified by The Open Group or the OSDU® Forum.
