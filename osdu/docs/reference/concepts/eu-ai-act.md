---
id: delivery-concept-eu-ai-act
title: "EU AI Act compatibility: how OSDU Delivery meets each obligation that applies to it, and what an estate that runs it does"
type: concept
summary: "OSDU Delivery against the EU AI Act: its role and risk class, each article that applies and how it is met, and what an estate does itself."
keywords:
  - eu ai act
  - ai act
  - ai act compliance
  - ai act compatibility
  - regulation 2024/1689
  - digital omnibus
  - article 50
  - article 4
  - ai literacy
  - transparency obligation
  - ai disclosure
  - marking ai-generated content
  - high-risk ai
  - annex iii
  - intended purpose
  - provider
  - deployer
  - general-purpose ai model
  - data residency
  - retention
related:
  - delivery-concept-ai-features
  - delivery-wiki-0046-eu-ai-act
  - delivery-guide-mcp
  - guide-chat-assistant
  - guide-slack-assistant
  - concept-maintenance
sourceRefs:
  - sqlflow/src/SqlFlow.Assistant/AssistantDisclosure.cs
  - sqlflow/src/SqlFlow.Assistant/AssistantInstructions.cs
  - sqlflow/src/SqlFlow.ControlPlane/Api/ChatEndpoints.cs
  - sqlflow/src/SqlFlow.ControlPlane/Api/QuestionExampleEndpoints.cs
  - sqlflow/src/SqlFlow.ControlPlane/Security/AssistantDelegation.cs
  - sqlflow/src/SqlFlow.ControlPlane/Maintenance/CatalogStorageContributor.cs
  - sqlflow/src/SqlFlow.Catalog/CatalogEntities.cs
  - sqlflow/src/SqlFlow.Catalog/ChatRetention.cs
  - sqlflow/src/SqlFlow.SlackBot/SlackAssistantHandler.cs
  - sqlflow/src/SqlFlow.SlackBot/SlackAssistantMode.cs
  - sqlflow/gui/src/features/chat/answerProvenance.ts
  - sqlflow/gui/src/features/chat/ChatThread.tsx
  - sqlflow/tests/SqlFlow.ControlPlane.Tests/ChatAnswerDisclosureTests.cs
  - sqlflow/tests/SqlFlow.ControlPlane.Tests/ConfirmQuestionApiTests.cs
  - sqlflow/tests/SqlFlow.ControlPlane.Tests/MaintenanceApiTests.cs
  - sqlflow/tests/SqlFlow.SlackBot.Tests/SlackAssistantModeTests.cs
  - sqlflow/gui/unit/answerProvenance.test.ts
  - osdu/src/SqlFlow.Delivery.ControlPlane/Configuration/DeliveryAssistantAccess.cs
  - osdu/gui/src/features/delivery/RecordContentView.tsx
  - osdu/gui/unit/recordContentExplain.test.ts
  - osdu/deploy/bicep/main.bicep
  - osdu/deploy/terraform/variables.tf
---

# EU AI Act compatibility

OSDU Delivery is compatible with the EU AI Act, Regulation (EU) 2024/1689 as the Digital Omnibus on AI (Regulation (EU)
2026/1744) amended it, for its intended purpose. Its assistant is an AI system of limited risk: nothing it does is
prohibited, it is not high-risk, and the transparency obligations of Article 50 that apply to it are met in the product.
What the product cannot do for an estate (training its people, keeping the assistant to its purpose, choosing where
prompts are processed and how long they are kept) is listed under [What an estate does](#what-an-estate-does).

This page is the product's reading as of 2026-11-05, made from the sources
[decision 0046](../../wiki/decisions/0046-eu-ai-act.md) names. It is not legal advice; counsel confirms it, and the open
points are [listed below](#open-points-for-counsel). Every feature that calls a model, what it is sent and what it keeps,
is [AI in OSDU Delivery](ai-features.md).

## The dates that apply

| From | What applies |
| --- | --- |
| 2 February 2025 | The prohibited practices (Article 5) and AI literacy (Article 4) |
| 2 August 2025 | The obligations of general-purpose AI model providers (Chapter V) |
| 2 August 2026 | The transparency obligations of Article 50; marking under Article 50(2) by 2 December 2026 for a system placed on the market before 2 August 2026 |
| 2 December 2027 | The high-risk obligations for the stand-alone systems of Annex III |
| 2 August 2028 | The high-risk obligations for systems embedded in the products of Annex I |

## Roles

| Who | Role under the Act |
| --- | --- |
| Microsoft (through Azure AI Foundry), OpenAI, Anthropic | Providers of the general-purpose AI models the assistant calls. Chapter V is theirs. |
| Whoever places OSDU Delivery on the market, or puts it into service under its own name | Provider of the assistant's AI systems (Article 3(3)). |
| The organisation that runs an estate | Deployer of those systems (Article 3(4)); provider and deployer at once when it builds and runs the product itself. |
| The vendor of an editor's assistant that uses the MCP server (Claude Code, Copilot, Cursor) | Provider of that AI system; the organisation using it is its deployer. The MCP server infers nothing and is no AI system. |

## Each obligation and how it is met

| Article | What it asks | Applies | How OSDU Delivery meets it |
| --- | --- | --- | --- |
| 5 | No prohibited practice (manipulation, social scoring, biometric identification and the rest) | No | The assistant does none of them. |
| 6(1), Annex I | Safety components of regulated products | No | It is part of no such product. |
| 6(2), Annex III | Obligations of a high-risk system, for the uses Annex III lists | No, for its intended purpose | It answers questions about pipelines, records and data. It is no safety component of critical infrastructure (point 2): the delegation fence keeps every chat and Slack run from starting a run or changing OSDU, the ledger, a mapping or a repository (`DeliveryAssistantAccess`). It is not meant to evaluate workers (point 4), and the [intended purpose](ai-features.md#intended-purpose) says so. |
| 6(3), 49(2) | Registration of an Annex III system its provider concludes is not high-risk | No | The assistant is not an Annex III system at all. |
| 50(1), 50(5) | A system that interacts with people tells them they are interacting with an AI system, clearly, at the latest at the first interaction | Yes | The chat and Explain say under the composer, from the first question on, that an AI language model writes the answers, which model on which provider, and that they can be wrong (`AssistantDisclosure.Sentence`). The Slack bot introduces itself as an AI assistant and ends every answer with the same mark. |
| 50(2) | A provider of a system that generates text marks its output, machine-readable and detectable as generated | Yes | Every stored answer records its provider and model (`ChatMessage.ModelProvider`, `Model`), which the API serves with each message and on the stream's `done` event. Each answer in the chat carries "AI-generated by" its model; an exported answer carries a header a program reads (`ai-generated: true`, provider, model). A Slack answer carries `ai_generated`, `provider` and `model` in its message metadata. A generated report question records its model. |
| 50(3) | Disclosure of emotion recognition and biometric categorisation | No | The product does neither. |
| 50(4) | Labels on deep fakes and on generated text published to inform the public | No | Answers are internal to an estate's operators and are not published. |
| 4 | Providers and deployers support the AI literacy of their staff | Yes, for the estate | The organisation's to do. The product supports it: [AI in OSDU Delivery](ai-features.md) says in plain words what each feature does, sends and keeps, and the chat says an answer can be wrong. |
| 25 | Whoever puts a system to a high-risk use becomes its provider | The estate's | The intended purpose names the use it excludes: judging the people who operate the estate. |
| Chapter V | Obligations of general-purpose AI model providers | The model providers' | An estate calls models of providers that carry them. |

Beyond what the Act requires of a system of limited risk, the product keeps a person in charge of what the assistant's
work becomes:

- **A verdict is the person's.** Only the row under an answer records one in the GUI; the confirm endpoint refuses a chat
  run's own token ("A verdict is the person's"), so an answer stored as confirmed, and the person named beside it, mean
  the person said so.
- **A query runs when a person runs it.** A composed query is shown first and runs when the person presses Run; the model
  says, when asked, that a query it put together is unchecked. Only a saved answer a person confirmed runs at once, and
  that within 50 rows and 5 seconds (`ControlPlane:AiContext:AutoRun:Enabled` turns it off).
- **The assistant reads, and never acts.** No chat or Slack run reaches an operator action, a run, a sync or a schedule.

And it keeps what reaches a model, and what is kept of it, in the estate's hands, as data protection asks:

- **No record content through Explain.** Explain is off on every view of record content (`RecordContentView`), so record
  content and source rows reach a model only through a `!` question, over the columns an approved AI context opens.
- **Retention an admin sets.** Chat conversations and compute tasks, query results included, take a limit on the
  Maintenance page.
- **Where prompts are processed.** `aiFoundryModelSkuName` (Terraform: `ai_foundry_model_sku_name`) keeps them within the
  EU data zone (`DataZoneStandard`) or the account's region (`Standard`).

## What an estate does

1. **Train the people who use it** (Article 4): that the assistant is a language model, that an answer can be wrong, what
   it is sent, and that a person checks a query before pressing Run. [AI in OSDU Delivery](ai-features.md) is written
   for them.
2. **Keep it to its purpose.** Do not use it to monitor, rank or judge the people who operate the estate, though the
   ledger records who acted on each record: that is a high-risk use (Annex III point 4) whose obligations fall on
   whoever puts it to it (Article 25).
3. **Choose where prompts are processed.** An estate bound to the EU sets `aiFoundryModelSkuName` to `DataZoneStandard` or
   `Standard`, or uses a provider whose terms keep processing where it must. A key-based provider (OpenAI, Anthropic) is
   called at its own endpoint.
4. **Set the retention it wants** on the Maintenance page, for chat conversations and for compute tasks; both keep
   everything until a limit is set.
5. **Decide on the Slack bot.** Its answers are read by everyone in a thread and carry the mark; every query it runs and
   every answer it saves is the bot's owner's.
6. **Treat editor assistants as its own AI systems.** An assistant such as Claude Code or Copilot that uses the MCP server
   acts with the person's own credential, and what it does is recorded as the person's; its transparency is its vendor's,
   and the estate is its deployer.
7. **Record the processing** in its records of processing under the GDPR: what goes to which provider (the questions,
   images, metadata and query results [AI in OSDU Delivery](ai-features.md#what-a-model-is-sent) lists), where, and for
   how long.

## How it stays true

Each behaviour above is held by a test, so a change that breaks one fails before it lands:

| Behaviour | Test |
| --- | --- |
| The disclosure, its model, and the model every stored answer records | `ChatAnswerDisclosureTests` |
| A Slack answer's mark, its metadata and the bot's introduction | `SlackAssistantModeTests` |
| The mark under an answer and the export's header | `sqlflow/gui/unit/answerProvenance.test.ts` |
| A chat run's confirmation refused, nothing stored | `ConfirmQuestionApiTests` |
| The GUI's rule that the model records no verdict | `AssistantInstructionsTests` |
| A generated question records its model | `SubscriberQuestionEnrichmentTests` |
| The retention of conversations and compute tasks | `MaintenanceApiTests`, `StorageAreaRulesTests` |
| No record content view offers Explain | `osdu/gui/unit/recordContentExplain.test.ts` |

[AI in OSDU Delivery](ai-features.md) changes with every feature that calls a model, and this page with every change to
what the Act asks or how the product meets it.

## Open points for counsel

- Whether Article 50(2) reaches the answers of an assistant used inside one organisation. The product marks them either
  way.
- The amended wording of Article 4, which the sources read as a duty to support literacy rather than to ensure it.
- Whether OSDU Delivery was placed on the market before 2 August 2026, which decides whether marking was due on that
  date or by 2 December 2026. It is in place now, either way.

## See also

- [AI in OSDU Delivery](ai-features.md)
- [Decision 0046: the EU AI Act](../../wiki/decisions/0046-eu-ai-act.md)
- [SQLFlow: the GUI chat assistant](../../../../sqlflow/docs/reference/guides/chat-assistant.md#who-writes-the-answers)
- [SQLFlow: the Maintenance page](../../../../sqlflow/docs/reference/concepts/maintenance.md#the-assistants-conversations-and-query-results)
