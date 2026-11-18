---
id: concept-environments
title: "Environments: the estate's separate deployments, named and opened from the title bar"
type: concept
summary: How a deployment names its environment and lists the estate's others (ControlPlane:Environments), and the title bar menu that opens them.
keywords:
  - environments
  - environment switcher
  - switch environment
  - dev test prod
  - several environments
  - separate deployments
  - instance
  - ControlPlane:Environments
  - ControlPlane:Environments:Current
  - ControlPlane:Environments:Links
  - ControlPlane__Environments__Links__0__Url
  - GET /environments
  - title bar
related:
  - concept-control-plane
  - concept-repository-auto-sync
  - guide-deployment
sourceRefs:
  - src/SqlFlow.ControlPlane/Configuration/EnvironmentsOptions.cs
  - src/SqlFlow.ControlPlane/Configuration/ControlPlaneOptions.cs
  - src/SqlFlow.ControlPlane/Api/EnvironmentEndpoints.cs
  - src/SqlFlow.ControlPlane/Hosting/ControlPlaneHost.cs
  - gui/src/layout/workbench/EnvironmentSwitcher.tsx
  - gui/src/layout/workbench/environmentMenu.ts
  - gui/src/layout/workbench/TitleBar.tsx
---

# Environments

An estate that runs dev, test and prod runs each as a deployment of its own: its own control plane, catalog, nodes and
sign-in, each moving to a new commit of the repository only when that environment syncs it
([Automatic sync of a repository](repository-auto-sync.md)). The environments setting tells each deployment which one it
is and where the others are, so the GUI's title bar names the environment a person is working in and opens another one's
GUI from a menu, rather than from bookmarks. The menu only links: each environment keeps its own data and signs a person
in itself.

## The setting

`ControlPlane:Environments`, in the control plane's configuration. `Current` names the environment this control plane is;
`Links` lists every environment of the estate, this one among them, in the order the menu shows them. Every deployment
can be given the same list, so they differ only in `Current`.

| Key | Meaning |
| --- | --- |
| `Environments:Current` | The name of the environment this control plane is: one of the names `Links` gives when it lists any (case does not matter; the list's spelling is shown). Set alone, the title bar names the environment and offers no other. |
| `Environments:Links:<n>:Name` | An environment's name, as the title bar shows it: `dev`, `test`, `prod`. |
| `Environments:Links:<n>:Url` | The address of that environment's GUI, which the menu opens. |
| `Environments:Links:<n>:Description` | What the environment is for, in one line, shown under its name. Optional. |

Nothing is set by default, and the title bar then shows no environment.

As environment variables, the form deployments set (`__` for each `:`), the test deployment of an estate of three:

```text
ControlPlane__Environments__Current=test
ControlPlane__Environments__Links__0__Name=dev
ControlPlane__Environments__Links__0__Url=https://sqlflow-dev.example.com
ControlPlane__Environments__Links__0__Description=Development
ControlPlane__Environments__Links__1__Name=test
ControlPlane__Environments__Links__1__Url=https://sqlflow-test.example.com
ControlPlane__Environments__Links__1__Description=Acceptance testing
ControlPlane__Environments__Links__2__Name=prod
ControlPlane__Environments__Links__2__Url=https://sqlflow.example.com
ControlPlane__Environments__Links__2__Description=Production
```

The dev and prod deployments carry the same lines with `Current` set to `dev` and to `prod`.

## A mistake in the setting

The setting only labels and links, so a mistake in it never stops a control plane, whatever else it is running. The
control plane reads the setting once when it starts, leaves out what the title bar cannot show, and logs a warning for
each mistake, naming the setting and what was left out:

| A mistake | What the title bar does |
| --- | --- |
| A name that is not one (letters, digits, `-`, `_` and `.`, starting with a letter or a digit, at most 64 characters), or an environment listed again, ignoring case | Leaves that entry out of the menu; an environment listed again keeps its first entry. |
| An address that is not an absolute `http` or `https` address of at most 2,000 characters, or that carries a user name, password, query or fragment | Leaves that environment out of the menu. The warning never repeats the address, since a mistyped one could carry a credential. |
| A description over 400 characters, or on more than one line | Shows the environment without it. |
| More than 20 environments | Shows the first 20. |
| `Links` lists environments and `Current` names none of them, or is not a name | Shows no environment at all, rather than a label that could be wrong. |

Leaving the setting out entirely shows nothing and logs nothing. A warning reads, for example:
`The title bar's environments: ControlPlane:Environments:Current is 'stage', which ControlPlane:Environments:Links does not list (dev, test, prod), so the title bar shows no environment: name one of them, or add this environment to the list with its address.`
and
`The title bar's environments: ControlPlane:Environments:Links:2:Url carries a user name or a password, which the address of an environment's GUI never holds, so the title bar's menu leaves 'prod' out.`

A deployment template may check the setting earlier still, as a Terraform variable's validation does at plan, before
anything changes.

## The API

`GET /api/v1/environments` (policy `read`) answers the setting as the title bar shows it, what a mistake left out already
left out: `current`, the name of the environment this control plane is (null when none is configured, or when the setting
does not say which it is), and `environments`, each with `name`, `description`, `url` and `current`, in the configured
order (empty when only the current one is named). It answers a signed-in caller
only, since the list names the estate's other addresses, and is closed to an assistant run's delegated token.

```json
{
  "current": "test",
  "environments": [
    { "name": "dev", "description": "Development", "url": "https://sqlflow-dev.example.com", "current": false },
    { "name": "test", "description": "Acceptance testing", "url": "https://sqlflow-test.example.com", "current": true },
    { "name": "prod", "description": "Production", "url": "https://sqlflow.example.com", "current": false }
  ]
}
```

## In the GUI

The title bar shows **Environment** and the current environment's name, just before the controls modules add. With
other environments listed, it opens a menu of every environment in order: the current one checked, each other one a link
to its GUI with its description and address. Following one opens that environment's start page, in the same tab (or a new
one, as any link can), where it signs the person in. With no other environment it names this one without a menu, and with
none configured it shows nothing. The title bar is on every page, so nothing in the answer may break it: the GUI links
only an `http` or `https` address, leaves out an entry it cannot read, and shows nothing when the answer is not one it
can read or the request fails.

## See also

- [The control plane](control-plane.md#configuration): the `ControlPlane` settings beside this one.
- [Automatic sync of a repository](repository-auto-sync.md): an environment that moves to a new commit only when it is
  synced.
- [Deploying](../guides/deployment.md): giving a deployment its configuration.
