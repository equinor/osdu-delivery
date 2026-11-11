---
id: delivery-wiki-0052-custom-schemas-registered-by-a-command
title: "Decision 0052: custom schemas are registered by a command a developer runs, never by a sync or a run"
type: decision
summary: "Why custom kinds reach a partition's Schema service through sqlflow template register, in development by default, never as a side effect of a sync or run."
keywords:
  - "register custom schema"
  - "schema service registration"
  - "template register"
  - "development schema"
  - "publish schema"
  - "env file input"
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Cli/DeliveryTemplateRegister.cs
  - osdu/src/SqlFlow.Delivery/Engine/Search/SchemaRegistrar.cs
  - osdu/src/SqlFlow.Delivery/Engine/Search/SchemaServiceReader.cs
  - osdu/src/SqlFlow.Delivery/Templates/CustomTemplates.cs
  - osdu/specs/core/INTEGRATION.md
referenceRefs:
  - delivery-cli-template-register
  - delivery-concept-templates
  - delivery-guide-linked-records
  - delivery-concept-dry-run
related:
  - delivery-wiki-0019-linked-templates
  - delivery-wiki-linked-canonical-and-custom-records
updated: 2026-11-11
---
# 0052: Custom schemas are registered by a command a developer runs, never by a sync or a run

Status: accepted. Documented in [sqlflow template register](../../reference/cli/template-register.md).

## Context

A custom kind (a template whose authority is not `osdu`) was registered with a partition's Schema service outside OSDU
Delivery ([0019](0019-linked-templates.md)). Storage takes records of a kind the service does not hold, and the indexer
flags each `schema not found`, so search, the explorer and assertions do not find them by their data. Doing it by hand
meant copying a template file's JSON into a request, choosing a status, and repeating it in every partition and after
every edit of the file while the kind was in development; the dry run already read the service and said `unregistered`
or `differs`, but nothing wrote. The owner asked for a robust way to bring OSDU in step with the latest custom schemas,
reading the connection from an env file, that finds every custom schema and gives the developer the choice.

The Schema service's own code settled what a write can do (schema-service at the commit `osdu/specs/core/INTEGRATION.md`
pins): `PUT /schema` creates a schema in development, replaces one in development, or moves it to published or obsolete;
a published or obsolete schema never changes; no route removes a schema; a write is compared with the nearest versions
of its family in the same major version; and a missing `service.schema-service.editors` answers 401.

## Decision

- **A command a developer runs, `sqlflow template register`.** It finds the custom kinds in a working copy's template
  folders, reads where each stands, shows the plan, and writes only what the developer answers, or what `--confirm`
  with the partition typed back says to write. The template file stays the only source of a schema.
- **Never a side effect.** A repository sync and a delivery run register nothing. A schema is shared by everything in
  the partition and cannot be removed, so a write follows from a person's choice, not from a commit reaching a branch.
- **Development by default; publishing is typed back.** Register and update write `DEVELOPMENT`, which a later run can
  still replace. Publishing makes a schema final, so it is written only after the word `publish` is typed, or with
  `--publish` and `--confirm`. A kind not registered is registered in development first, so a refused publish leaves a
  schema that can still change.
- **The latest version of each family is proposed.** Older versions are listed and can be chosen; `--all` and `--kind`
  take more. A published schema that differs, a kind the platform shares and two files holding different schemas of one
  kind are never written: each says what the developer does instead.
- **Every write is read back.** A kind counts as written when the service holds the file's schema in the status asked,
  compared by template version, since the service stores a schema re-serialised. An answer lost on the way that the
  read back contradicts is reported as done.
- **The env file is the input.** The connection and the partition come from the env file named or the nearest
  `.sqlflow/env`, over the process environment, which the run notes and never changes; values are never printed.

## Rejected

- **Registering on repository sync.** A sync of any branch would rewrite a schema the whole partition reads, the same
  hazard that ruled out registering a branch on an estate.
- **Registering inside a delivery run.** The run's client would need the editors group, and a delivery would change the
  platform beyond the records it is about; the integration notes already recommend a separate, reviewed operation.
- **A script beside the CLI.** Two copies of the connection, the template folders and the schema comparison would drift;
  the verb reuses the partition connection the explorer reads through and the Schema service reader the dry run uses.
- **Supplying the env file's values as a run's references.** Supplied references refuse a literal credential, and a
  developer's env file holds one; the run reads the file through a resolver of its own over the command's providers.

## Consequences

- The client a developer runs the command with needs `service.schema-service.editors` in the partition; the dry run and
  `--dry-run` need only viewers.
- A change of a property's type in development leaves the kind's index on the old mapping until an administrator
  reindexes the kind with `force_clean`; the plan says so before the write.
- A CI job can fail on `--dry-run` while a custom kind is out of step, and bring development schemas in step after a
  merge with `--confirm`; publishing stays a person's step.
