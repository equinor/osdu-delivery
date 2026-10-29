---
id: delivery-wiki-0038-download-flow
title: "Decision 0038: a download flow writes each record to a file named after its id, and its ledger keeps the version each file holds"
type: decision
summary: "Why records go to a folder a file each through a flow kind of its own, named after the id by a reversible rule, with a ledger of the version each file holds."
keywords:
  - download flow
  - one file per record
  - file name from osdu id
  - reversible file name
  - case-insensitive file systems
  - mirror folder
  - record metadata files
updated: 2026-10-29
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Engine/Downloads/DownloadFileNames.cs
  - osdu/src/SqlFlow.Delivery/Engine/Downloads/DownloadRunner.cs
  - osdu/src/SqlFlow.Delivery/Documents/DownloadDocumentMapper.cs
  - osdu/src/SqlFlow.Delivery/Ledger/SqlServerLedgerBulk.Download.cs
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
referenceRefs:
  - delivery-flow-download
  - delivery-flow-inventory
  - delivery-flow-retrieval
  - delivery-table-download-record
---
# 0038: A download flow writes each record to a file named after its id, and its ledger keeps the version each file holds

Status: accepted. Documented in [the download flow](../../reference/flow/download.md).

## Context

The owner asked for a pipeline that takes the records of one OSDU kind, the way an inventory takes them, and stores each
record's document on a target location, local disk or cloud storage, named after its id if the id can be made a file
name. The document is the record's metadata, the JSON OSDU stores; never the data a dataset links to.

A [retrieval flow](../../reference/flow/retrieval.md) already brings records back to the lake, but as JSON Lines files in
a new folder per run, with a manifest and a watermark on `modifyTime`: data to load into a table, not one file per
record that a person or a tool opens by its id.

An OSDU id is letters, digits, `_`, `-`, `.`, and from its third segment `:` and `%` (the storage API's record id
pattern). Windows refuses `:` in a file name, Hadoop and Spark read `dev:...` as a URI scheme, and `%` is the escape of a
URL, which a blob's address is. OSDU ids are case-sensitive (the foot `...UnitOfMeasure:ft` and the femtotesla
`...UnitOfMeasure:fT`), and the folders of Windows and macOS are not. A file name has at most 255 characters; an id has
no such bound.

## Decision

- **A flow kind of its own, `flowType: download`.** Its source is an inventory flow's, and its kinds are listed by the
  inventory's own readers, search or storage, so the two list a kind alike. Each record due is then read from storage, a
  hundred to a request, and written as it is held.
- **The file is named after the id, by a rule that turns back.** `:` is written `~` and `%` is written `=`, characters no
  id holds, so the name reads as the id, converts back to it, and no two ids share it.
- **A name that cannot be the id's own says so.** An id whose name another id of the download holds but for its case,
  one too long for a file name, or one holding a character outside the pattern is named by its hash: the name cut to
  fit, then `@` and the start of the SHA-256 of the id. The id sorting first by its exact characters keeps its own name.
  The names are compared in a case-insensitive collation under a unique index, so the database refuses two ids one file
  whatever the merge does.
- **An id keeps its name for its life**, gone or not, so a name is never given to another id.
- **The ledger keeps, per id, the version its file holds**, with the file's size and SHA-256, so a run writes only what
  is new or changed, and the ledger, not the folder, says what each file holds. A newer version replaces the file.
- **A record OSDU no longer serves keeps its file** and is marked gone. Nothing a download does removes a file.
- **The folder is the same for every run.** `{run}` and `{date}` are refused, a relative path is refused (a run reads its
  flow from a checkout of its own), and a run writing to another folder than the last completed one writes every record
  again, as a forced run does.

## Alternatives considered

- **A per-record layout as an option of the retrieval flow.** Not taken: a retrieval's folder per run, its watermark and
  its manifest describe a feed of changes, and a folder that mirrors what OSDU serves needs a state per id, which a
  retrieval does not keep. The two would share keys that mean different things.
- **`:` written `_`.** Not taken: `_` is an id's own character, so `a:b` and `a_b` would share a file and the second
  would overwrite the first without a word.
- **Percent-encoding the name (`%3A`).** Not taken: `%` is what a blob's address escapes with, and ids already hold
  percent-escapes (`ft%2Fs`), so a name would carry escapes of escapes and read as neither.
- **Failing an id whose name collides, or is too long.** Not taken: it would leave records of a kind out of the folder for
  good. The hash keeps every record written, and the record in the file says its id.
- **A file per version** (`<id>~<version>.json`). Not taken, by the owner's choice: the folder holds what OSDU serves, and
  the ledger keeps which version each file holds.
- **The file a dataset links to.** Not taken, by the owner's choice: the download is the record's metadata.

## Consequences

- Four tables: the downloads, their runs, a row per id, and a run's staged listing until it merges.
- A folder holds the files of gone records until someone removes them; the Downloads tab lists them as gone.
- A file changed by hand in the folder is not noticed until a forced run writes it again; the ledger's SHA-256 of each
  file tells which no longer holds what was written.
- The targets are the file stores the product has: a local disk and Azure Storage. Another cloud's store is work of its own.
