---
id: delivery-flow-dimension-clean
title: "Dimension clean steps: each value trimmed, folded, replaced or mapped through a dictionary"
type: flow-reference
summary: "A dimension's clean steps: trim, collapse spaces, case, Unicode forms, separator fold, replace and map through a dictionary, and keys left out."
keywords:
  - clean steps
  - trim
  - collapseSpaces
  - upper
  - foldSeparators
  - replace
  - map dictionary
yamlPath: "dimensions[].clean"
related:
  - delivery-flow-dimension
  - delivery-flow-dictionary
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Engine/Dimensions/DimensionCleaner.cs
  - osdu/src/SqlFlow.Delivery/Documents/DimensionDocumentMapper.cs
---

# Dimension clean steps: each value trimmed, folded, replaced or mapped through a dictionary

A [dimension](dimension.md) cleans each value into what a person picks, so spellings of one value are one.

Each value is cleaned by its steps in order: the label when one was read, the `unlabelled` text for a key whose label was
not read, otherwise the key (for a key naming a record, its decoded code). The result is trimmed. A step is written as
its name, or as a one-key map with its settings:

| Step | What it does |
| --- | --- |
| `trim` | Removes white space at both ends. |
| `collapseSpaces` | Makes every run of white space one space. |
| `upper`, `lower` | Changes case by the invariant culture's rules. |
| `nfc`, `nfkc` | Unicode normalization form C, or KC (compatibility characters folded). |
| `foldSeparators` | Keeps letters and digits in order, makes every run of anything else one hyphen, and lower-cases: `Gamma_Ray (API)` is `gamma-ray-api`. |
| `replace: { pattern, with }` | Replaces a regular expression's matches; `$1` names a group, `with: ''` removes them. The pattern runs on .NET's non-backtracking engine, at most one second a value, so a backreference, lookaround or atomic group is refused when the document is read. |
| `map: <dictionary>` or `map: { dictionary, field, otherwise }` | Looks the value up in a [dictionary document](dictionary.md) (`dictionaries/<name>.yaml` above the flow), exactly or ignoring case when that finds one entry, and replaces it with the entry's value (`field` names which, for a dictionary of several fields). `otherwise` says what an unlisted value comes to: left out, it is kept; `~`, the key is left out of every value; a text, that text. |

A key is left out of every value, with the reason kept on it, when cleaning leaves nothing, when the value is longer
than 256 characters, when `map` leaves it out, or when a step fails on it. `sqlflow validate` finds and reads the
dictionary each `map` step names, in the nearest `dictionaries/` folder walking up from the flow file, and refuses a flow
whose dictionary is missing or does not load
(`<file>: dimension '<name>': Dictionary '<dictionary>' was not found under '../dictionaries'. Expected <dictionary>.yaml or <dictionary>.yml.`).
A build of a dimension whose dictionary cannot be read fails, and the other dimensions build.
