---
id: delivery-guide-vscode-environments
title: "Environments in the VS Code extension: dev, test and prod, each in its colour"
type: guide
summary: "Save the control planes you work against with a name and a stage, switch between them with their sign-ins, and see the one in use in green, orange or red."
keywords:
  - environments
  - dev test prod
  - switch environment
  - switch control plane
  - add an environment
  - manage environments
  - environment colour
  - production in red
  - stage
  - development
  - test
  - production
  - sqlflow.environments
  - sign-in per control plane
  - status bar
  - side bar
related:
  - delivery-guide-vscode
  - delivery-wiki-0061-environments-are-deployments
  - delivery-concept-partitions
  - concept-environments
sourceRefs:
  - sqlflow/gui/src/embed/environments.ts
  - sqlflow/gui/src/embed/HostedEnvironments.tsx
  - sqlflow/gui/src/embed/HostedSignIn.tsx
  - sqlflow/tools/sqlflow-vscode/src/gui/environments.ts
  - sqlflow/tools/sqlflow-vscode/src/gui/environmentCalls.ts
  - sqlflow/tools/sqlflow-vscode/src/gui/credentials.ts
  - sqlflow/tools/sqlflow-vscode/src/host.ts
  - osdu/hosts/osdu-delivery-vscode/package.json
---

# Environments in the VS Code extension

An estate runs dev, test and prod as [deployments of their own](../../wiki/decisions/0061-environments-are-deployments.md),
each with its own control plane. The extension keeps the ones you work against as **environments**: each a control
plane's address, the name you call it by and its **stage**. You switch between them from the side bar, the sign-in kept
for each comes with it, and every surface shows the environment in use in its stage's colour.

| Stage | Colour | What it is for |
| --- | --- | --- |
| Development | Green | Where flows and mappings are built and tried. |
| Test | Orange | Where a change is checked before it is released. |
| Production | Red | What the business runs on. Switching to it asks first. |

## Where the environment in use shows

| Surface | What it shows |
| --- | --- |
| The side bar | A line in the stage's colour across its top, the environment's name and stage, and the menu that switches. On production the row is tinted red. |
| A page's tab | A line in the stage's colour across the top of the page, and the environment's name in the strip over it; on production, the word **Production** on red. |
| The status bar | The environment's name, in green or orange; production on the editor's red. |
| The sign-in | The environment you sign in to, its stage and whether it answers, and **Sign in to** its name. |
| **Sync** in the side bar | Names the environment the commit moves on. |

A person who has saved no environment sees what they saw before: nothing new on the pages or in the status bar, and the
control plane in use marked **Not saved** in the side bar, with the way to save it.

## Adding one

Open the side bar's environment menu (the name at its top) and pick **Add an environment**, or **Manage environments**
for the list, or run **OSDU Delivery: Switch or Add an Environment**. While nobody is signed in, the sign-in's
**Environment** card leads to the same list. The form takes:

- **Control plane address**, asked whether it answers as you type: the extension calls the control plane's anonymous
  `GET /api/v1/auth/providers`, with no credential, and says how it signs in, that it does not answer and why, or that
  something else answers there (a GUI's address, say). A control plane that is down can still be saved.
- **Name**, the address's first label until you type one, by the control plane's own rule for an environment's name:
  letters, digits, `-`, `_` and `.`, starting with a letter or a digit, at most 64 characters.
- **Stage**, picked for you when the name says one (`dev`, `local`, `sandbox`; `test`, `qa`, `uat`, `staging`, `preprod`;
  `prod`, `prd`, `production`, `live`, each with or without a number after it), or else the address's host does. When
  neither says exactly one, none is picked and the form asks, so a production control plane at a plain address is never
  shown in development's colour because nobody chose.
- **GUI address**, optional: where **Open in the browser** goes from a page of this environment.

Two environments cannot share a name or a control plane. Saving the first environment, while you work against none of
yours, also works against it, and the button says so. The control plane in use when it is none of yours is listed first,
marked **Not saved**, with **Save it with its stage**.

## Switching

Pick an environment in the side bar's menu or in the list. Switching to production asks first, naming what moves with it.
A switch:

- sets `sqlflow.controlPlaneUrl` to the environment's control plane and `sqlflow.guiUrl` to its GUI address (empty when it
  has none, so a page never opens another environment's GUI on this one's ids). Each is written where it is set: in the
  workspace's settings when the workspace sets it, and in yours otherwise. A folder of a multi-root workspace that sets
  `sqlflow.controlPlaneUrl` outweighs both, and the switch says so rather than seem to happen;
- moves everything that follows that setting: the MCP server, the assistants it is shared with, the `SQLFLOW_URL` of new
  terminals and the `sqlflow` command line's current control plane
  ([which control plane a command reaches](../../../../sqlflow/docs/reference/cli/control-plane.md#signing-in));
- closes the pages open from the environment before, and says how many: a page's address names its control plane's flows,
  runs and records by id, and the same address on another control plane names something else.

### The sign-in comes with it

`~/.sqlflow/credentials.json` keeps one sign-in per control plane, so switching to an environment signs in with the one
kept for it, with no browser, and switching to one with none shows **Sign in to** its name. The list says which
environments hold a sign-in (**Signed in as**), and the menu marks them with a key.

Only the environment in use has its session renewed. A session lasts the control plane's
`ControlPlane:Jwt:AccessTokenMinutes` (720, twelve hours, by default), so an environment you have not worked against for
longer asks you to sign in again when you switch back. **Sign out** signs out of the environment in use and keeps the
others' sign-ins.

## Changing and removing one

Each environment in the list has a menu with **Edit** and **Remove**. Editing the environment in use moves the control
plane with it when its address changes. Removing one takes it off your list only: its control plane, and the sign-in kept
for it, are left as they are, and removing the one in use leaves you working against it, marked **Not saved**.

## The setting

The list is `sqlflow.environments`, in your own settings (an application setting: a workspace cannot set it, so a
repository cannot relabel production as development), and Settings Sync carries it to your other machines.

```json
"sqlflow.environments": [
  { "name": "dev", "url": "https://sqlflow-dev.example.com", "stage": "development" },
  { "name": "test", "url": "https://sqlflow-test.example.com", "stage": "test" },
  { "name": "prod", "url": "https://sqlflow-prod.example.com", "stage": "production", "guiUrl": "https://sqlflow-prod-gui.example.com" }
]
```

An entry the extension cannot read (a stage it does not know, a name or address another entry has, an address with a
user name in it, one past the twentieth) is left out of the side bar and said in the extension's output, and the side bar
saves nothing until it is corrected, so nothing in the list is lost.

The environment names here are yours. The GUI's title bar names the environment a deployment says it is
([environments](../../../../sqlflow/docs/reference/concepts/environments.md)).
