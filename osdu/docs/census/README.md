# Editor key census

The files here describe every key of the documents OSDU Delivery adds, in the key census format SQLFlow's editor
tooling reads (`sqlflow/tools/README.md`, Module census files). They drive hover documentation, colouring and key
checks for these documents in the GUI's YAML editor, and in the language server when its client names this folder in
`initializationOptions.censusDirectories`.

| File | Describes |
| --- | --- |
| `keys.delivery.json` | A delivery flow, `flowType: delivery`, in the single form and as a source with interfaces. |
| `keys.retrieval.json` | A retrieval flow, `flowType: retrieval`. |
| `keys.cache.json` | A cache flow, `flowType: cache`: OSDU types, ingestion table types, dictionary types and dimension types. |
| `keys.assertion.json` | An assertion flow, `flowType: assertion`: its source, defaults and tests, and every subject, condition and modifier an assertion takes. |
| `keys.dimension.json` | A dimension flow, `flowType: dimension`: its source and the dimensions it builds, with their keys, labels, attributes and cleaning. |
| `keys.inventory.json` | An inventory flow, `flowType: inventory`: its source, the inventories it reads and what a removal may take. |
| `keys.removal.json` | A removal flow, `flowType: removal`: its guard and its removals, each with the delivery flow it writes to and its key sources (`keys`, `removed`). |
| `keys.download.json` | A download flow, `flowType: download`: its source, where its files go (`target`) and the downloads it writes. |
| `keys.mapping.json` | A mapping, `documentType: mapping`: its header and its `record` tree, whose node grammar (every `$` word, modifier and setting) is described on `record.<name>`. |
| `keys.dictionary.json` | A dictionary, `documentType: dictionary`. |
| `keys.repository.json` | A repository document, `documentType: repository`: the code a repository's runs carry in their correlation id. |

Every loader of the module refuses a key it does not know, so each file says `strictKeys: true`, and the flow kinds
take the platform envelope (`includeEnvelope: true`) as SQLFlow documents it. The descriptions are written from
the loaders, and agree with the reference pages in [../reference/flow/](../reference/flow/).

`EditorCensusTests` checks the files against the code: every key a flow kind's strict loader accepts is documented and
nothing is documented that it would refuse (read off the YAML models by reflection), the mapping's node grammar names
every word, modifier and setting `MappingMapper` accepts, the dictionary's keys match the names `DictionaryMapper`
accepts, and the repository document's match its YAML model. A key added to a loader without its entry here fails that suite, so the editor never stops documenting a key an
author can write.

The GUI module registers the files (`osdu/gui/src/module.tsx`), and each one loads when the editor first starts.
