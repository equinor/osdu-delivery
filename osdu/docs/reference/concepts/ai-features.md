---
id: delivery-concept-ai-features
title: "AI in OSDU Delivery: what calls a language model, what it is sent, what it may do, what is kept, and how to switch it off"
type: concept
summary: "Every feature that calls a language model: its switch and default, the data it sends, what it may change, what is stored and what a person is told."
keywords:
  - ai
  - artificial intelligence
  - language model
  - llm
  - eu ai act
  - ai act
  - ai inventory
  - intended purpose
  - ai transparency
  - ai disclosure
  - ai-generated
  - chat assistant
  - slack assistant
  - explain
  - voice input
  - data mode
  - auto-run
  - saved answers
  - confirm_question
  - generated questions
  - model provider
  - azure ai foundry
  - data residency
  - aiFoundryModelSkuName
  - chat retention
  - switch off the assistant
related:
  - delivery-concept-eu-ai-act
  - delivery-guide-mcp
  - guide-chat-assistant
  - guide-slack-assistant
  - concept-semantic-layer
  - flow-ai-context
  - concept-maintenance
  - delivery-concept-authentication-and-identity
  - delivery-guide-deployment
  - delivery-guide-radix-setup
  - delivery-wiki-0046-eu-ai-act
sourceRefs:
  - sqlflow/src/SqlFlow.Assistant/AssistantDisclosure.cs
  - sqlflow/src/SqlFlow.Assistant/AssistantSettings.cs
  - sqlflow/src/SqlFlow.Assistant/AssistantInstructions.cs
  - sqlflow/src/SqlFlow.Assistant/ResponsesApiGateway.cs
  - sqlflow/src/SqlFlow.Assistant/AnthropicGateway.cs
  - sqlflow/src/SqlFlow.Assistant/TranscriptionGateway.cs
  - sqlflow/src/SqlFlow.Assistant/QuestionExpander.cs
  - sqlflow/src/SqlFlow.Assistant/QuestionGenerator.cs
  - sqlflow/src/SqlFlow.ControlPlane/Api/ChatEndpoints.cs
  - sqlflow/src/SqlFlow.ControlPlane/Api/QueryEndpoints.cs
  - sqlflow/src/SqlFlow.ControlPlane/Api/QuestionExampleEndpoints.cs
  - sqlflow/src/SqlFlow.ControlPlane/Api/ColumnPolicyGuard.cs
  - sqlflow/src/SqlFlow.ControlPlane/Security/AssistantDelegation.cs
  - sqlflow/src/SqlFlow.ControlPlane/Configuration/ControlPlaneOptions.cs
  - sqlflow/src/SqlFlow.ControlPlane/Background/QuestionSearch.cs
  - sqlflow/src/SqlFlow.ControlPlane/Background/SubscriberQuestionEnrichment.cs
  - sqlflow/src/SqlFlow.ControlPlane/Maintenance/CatalogStorageContributor.cs
  - sqlflow/src/SqlFlow.Catalog/CatalogEntities.cs
  - sqlflow/src/SqlFlow.Catalog/ChatRetention.cs
  - sqlflow/src/SqlFlow.Catalog/ComputeTaskStore.cs
  - sqlflow/src/SqlFlow.Core/Query/QueryModels.cs
  - sqlflow/src/SqlFlow.SlackBot/SlackAssistantHandler.cs
  - sqlflow/src/SqlFlow.SlackBot/SlackAssistantMode.cs
  - sqlflow/tools/sqlflow-mcp/src/server.rs
  - sqlflow/gui/src/components/CodeView.tsx
  - sqlflow/gui/src/components/assistant-ui/thread.tsx
  - sqlflow/gui/src/features/chat/ChatThread.tsx
  - sqlflow/gui/src/features/chat/answerProvenance.ts
  - sqlflow/gui/src/features/chat/AnswerConfirmation.tsx
  - sqlflow/gui/src/features/chat/QueryResultEmbed.tsx
  - sqlflow/gui/src/features/semantic-layer/ReportsPanel.tsx
  - osdu/src/SqlFlow.Delivery.ControlPlane/Configuration/DeliveryAssistantAccess.cs
  - osdu/gui/src/features/delivery/RecordContentView.tsx
  - osdu/deploy/bicep/ai-foundry.bicep
  - osdu/deploy/bicep/main.bicep
  - osdu/deploy/terraform/main.tf
  - osdu/deploy/terraform/variables.tf
  - osdu/deploy/radix/radixconfig.yaml
---

# AI in OSDU Delivery

OSDU Delivery delivers records without a language model. No flow, mapping, render, submission, removal or ledger write
calls one, and nothing under `osdu/src` does. Models are called only to answer people's questions and to help find a
saved answer, through SQLFlow's assistant. This page lists every place a model is called, what each is sent, what it may
change, what is kept and what a person is told, so an estate can judge its AI use from one page. How this meets the EU
AI Act is [EU AI Act compatibility](eu-ai-act.md), and why it is this way is
[decision 0046](../../wiki/decisions/0046-eu-ai-act.md).

## Intended purpose

The assistant answers the questions of the people who build and operate an estate: what a pipeline, flow, mapping or
record is and where it stands, why a run failed, what the documentation says, and, through a `!` question, what the
data an approved AI context opens holds. It reads; a person acts. It is not meant to decide or carry out anything an
estate operates, and it is not meant to monitor, rank or judge the people who work in it, although the ledger records
who acted on each record and the assistant can read that record. Putting it to such a use is outside its purpose and
changes what the EU AI Act asks of whoever does so ([EU AI Act compatibility](eu-ai-act.md#what-an-estate-does)).

## What calls a model

| Feature | Who starts it | What calls the model | On a fresh deployment |
| --- | --- | --- | --- |
| The GUI assistant: the Assistant page, Home's question box, and Explain on a code view | A signed-in person | The control plane, through the configured provider | Off: `ControlPlane:Assistant:Enabled` is false |
| The Slack assistant | Someone who mentions the bot or writes to it | `SqlFlow.SlackBot`, through the same gateways | Not deployed: no template of this product deploys the bot |
| Voice input | A person at the microphone in the chat | The control plane's transcription deployment; without one, the browser's own speech recognition | Off until a transcription deployment is set |
| Synonym expansion of a `!` question | `find_similar_questions`, on the server | The control plane, always Anthropic | Off unless AI context is on and either an Anthropic key is set or `ControlPlane:AiContext:ExpandSynonyms` is true |
| Questions generated for report visuals | A repository sync | The control plane, always Anthropic | Off unless an Anthropic key is set and a subscriber library sets `generateQuestions: true` |

Two parts of the product never call a model. The MCP server (`osdu-delivery-mcp`) serves tools and instructions to a model
that a client runs (Claude Code, Cursor, VS Code, Codex, or the GUI's own chat); its only outbound calls are to the control
plane ([MCP server](../guides/mcp.md)). The delivery module calls none.

## Switches, providers and models

| Setting | Default | What it does |
| --- | --- | --- |
| `ControlPlane:Assistant:Enabled` | `false` | Turns the GUI assistant on. Off, the chat page says it is not configured and Explain is hidden. |
| `ControlPlane:Assistant:Provider` | `AzureFoundry` | `AzureFoundry`, `OpenAI` or `Anthropic`. |
| `ControlPlane:Assistant:Foundry:ModelDeploymentName` | none | The Foundry deployment that answers; required with Foundry. |
| `ControlPlane:Assistant:OpenAI:Model` | `gpt-5-mini` | The model when the provider is OpenAI. |
| `ControlPlane:Assistant:Anthropic:Model` | `claude-opus-4-8` | The model when the provider is Anthropic, and the model synonym expansion and question generation use. |
| `ControlPlane:Assistant:Mcp:AllowedTools` | every tool | Narrows the tools an assistant run may call. |
| `ControlPlane:AiContext:Enabled` | `false` | Turns `!` questions on: queries over the columns the approved AI contexts open. |
| `ControlPlane:AiContext:AutoRun:Enabled` | `true` | With AI context on, a saved answer judged a match runs without a click (50 rows, 5 seconds). |

The deployment templates turn the GUI assistant on when the MCP server and a Foundry model are both set (Bicep's
`mcpEnabled` with `aiFoundryName` and `aiFoundryModelName`, and Terraform's equivalents). Terraform's shipped
environments set `mcp_enabled = true` and `ai_foundry_model_name = "gpt-5.1"` with `ai_context_enabled = false`; the Radix
configuration turns on both the assistant and AI context ([Radix setup](../guides/radix-setup.md#8-ai-foundry)).

Where a prompt is processed is the Foundry deployment's type, Bicep's `aiFoundryModelSkuName` and Terraform's
`ai_foundry_model_sku_name`, for the chat and transcription models alike:

| Value | A prompt is processed |
| --- | --- |
| `GlobalStandard` (the default) | In any Azure region the model is deployed in |
| `DataZoneStandard` | Within the data zone of the account's region: the EU or the US |
| `Standard` | In the account's own region |

An estate whose prompts must stay in the EU sets `DataZoneStandard` or `Standard`; changing it replaces the model
deployments. The OpenAI and Anthropic providers are called at the provider's own endpoint, under the provider's terms.
The Foundry account is created in the estate's region, without a content filter policy or diagnostic settings of its own.

## What a model is sent

**The GUI assistant**, on each question: the MCP server's instructions, the conversation's last 20 messages, the
question (at most 32,000 characters), up to 4 images of up to 8 MB each, and whatever the tools return during the run. In
estate mode that is metadata: the catalog, flow YAML, run errors, file names, and the ledger's bookkeeping about a record
(its state, OSDU id and version, attempts and their errors, source file and row number, who acted and when; the table in
[MCP server](../guides/mcp.md#metadata-and-a--questions-query)). In data mode it is the approved AI contexts' tables,
columns and descriptions, the SQL of saved answers, and the rows a query returns: 200 by default and at most 5,000 for a
prepared query, 50 for an auto-run.

**Explain** sends what a code view shows as the question: a mapping's YAML, a template, a statement, a search request.
It is off on every view of record content (`RecordContentView`): a record's Source and Render tabs, a preview's documents,
step bodies and source rows, a cache item, a cache change's values and the record the mapping overview renders. So no
record content or source row reaches a model outside a `!` question.

**The Slack assistant**: the question, up to 4 attached images, and, for a question asked inside a thread, the thread's
messages from anyone in it (up to 500 read, the last 20 sent).

**Synonym expansion**: the question's text. **Question generation**: a report visual's type, title and field names; no
rows.

The column allow-list (`ColumnPolicyGuard`) decides which columns a query may read. Nothing masks values, and nothing
filters the metadata or a Slack thread before they are sent. Foundry and OpenAI are called through the Responses API with
`store: true`, chaining a conversation by `previous_response_id`; the Anthropic gateway sends the transcript again on each
call.

## What a model may do

A GUI or Slack assistant run works under a token delegated from the person for that run alone (the run's timeout plus 60
seconds), carrying the person's own id without the admin and node scopes. The control plane refuses it on every route
not opened to the assistant: it may read, prepare and run a query, auto-run a saved answer, and start the compute tasks
those need. The delivery module opens only its readers (`DeliveryAssistantAccess.ReaderTools`); the six operator actions,
`trigger_run`, a sync and a schedule are refused, and the assistant says where a person does them. No chat or Slack run
writes to OSDU, the ledger, a mapping or a repository.

In data mode:

- **A composed query** is prepared (`prepare_query`) and shown, and runs only when it is redeemed (`run_query`) with its
  plan token, which is single-use and lives 15 minutes. In the GUI the person's click on Run redeems it. The server does
  not check that a person clicked: the instructions tell the model to wait for the person's agreement. When asked, the
  model says plainly that a query it put together has not been checked by anyone.
- **A saved answer judged a match** runs at once (`auto_run_trusted_match`) while auto-run is on, without a click, within
  50 rows and 5 seconds.
- **A verdict on an answer** is the person's. In the GUI only the row under the answer (Yes, Not quite, No) records one:
  the confirm endpoint refuses a GUI chat run's delegated token ("A verdict is the person's"), so an answer stored as
  `user-confirmed`, and the id beside it, mean the person said so. The Slack bot confirms with its own credential when
  someone in the thread says an answer is right, and the row names the bot's owner.
- **Questions generated for report visuals** are written to the catalog on a sync without review, each recording the
  model that wrote it; an admin removes them on the AI context page. Search serves one with the Power BI provenance of
  its report, since the query behind it is the report's own.

A client outside the control plane (Claude Code, Cursor, VS Code, Codex) calls the MCP server with the person's own
token, so it can do what the person can, operator actions and pipeline proposals included
([the delivery tools](../guides/mcp.md#the-delivery-tools)). What it does is recorded as the person's.

## What is kept

| What | Holds | Kept |
| --- | --- | --- |
| Chat conversations | Every question, image and answer, the tools each answer called and the model that wrote it | Until the person deletes it, the user is removed, or nobody wrote in it for the limit an admin sets on the Maintenance page |
| Query plans | A prepared query's SQL and who prepared it | 24 hours |
| Compute tasks | Each query a node ran: its SQL, who asked, and the result rows | Until it is older than the limit an admin sets on the Maintenance page; a task still queued or running stays |
| AI contexts and saved answers | Saved answers with their provenance and who confirmed them | Until an admin removes them, or the repository that proposed them goes |
| Report visual questions | Generated questions (`Origin = generated`) with the model that wrote each | Replaced on each sync; removed by an admin |

Both limits start unset, as every limit on the Maintenance page does: nothing goes until an admin sets one
([SQLFlow's Maintenance page](../../../../sqlflow/docs/reference/concepts/maintenance.md#the-assistants-conversations-and-query-results)).
A chat answer whose query result was removed says so, and that running the query again shows it. The logs carry a failed
run's conversation id and a provider's throttling or truncation; they carry no prompt or token count per request.

## What a person is told

Under the chat's composer, from the first question on, and in Explain's drawer: "Answers are written by an AI language
model (gpt-5.1 on Azure AI Foundry) and can be wrong: check what matters before you act on it." The model and provider are
the deployment's own (`GET /api/v1/chat/capabilities` serves the sentence as `disclosure`). Each answer carries the mark
"AI-generated by gpt-5.1 on Azure AI Foundry" (an answer stored before answers recorded their model reads
"AI-generated"), and **Export as Markdown** saves it with a header a program reads (`ai-generated: true`, the provider and
model, when it was exported) above the same mark.

The Slack bot ends every answer with "_AI-generated by gpt-5.1 on Azure AI Foundry; it can be wrong._", carries
`ai_generated`, `provider` and `model` in the answer's message metadata, and answers a mention that asks nothing with
"I'm an AI assistant." and the same sentence the chat shows.

A composed query opens with "I don't have a saved answer for this question yet, so I've put together a query that should
answer it.", and the GUI asks "Did this query answer your question?". On the AI context page a saved answer carries
Confirmed, Power BI or AI context, and a report question carries "generated by" its model or "written by a person".

## Switching it off

| To stop | Set |
| --- | --- |
| The GUI assistant and Explain | `ControlPlane:Assistant:Enabled` to `false` |
| `!` questions and every query a model writes | `ControlPlane:AiContext:Enabled` to `false` |
| Runs without a click | `ControlPlane:AiContext:AutoRun:Enabled` to `false` |
| Synonym expansion | `ControlPlane:AiContext:ExpandSynonyms` to `false`, or no Anthropic key |
| Question generation | No Anthropic key, or no library with `generateQuestions: true` |
| Voice transcription by a model | No transcription deployment (the browser's speech recognition remains) |
| The Slack assistant | Do not deploy `SqlFlow.SlackBot` |

The question generator and synonym expansion are independent of the chat: an Anthropic key alone makes them available
with the assistant off. Every model call stops only when the assistant is off, no Anthropic key is set, no transcription
deployment is configured and the Slack bot is not deployed.

## See also

- [EU AI Act compatibility](eu-ai-act.md)
- [Decision 0046: the EU AI Act](../../wiki/decisions/0046-eu-ai-act.md)
- [The MCP server](../guides/mcp.md)
- [SQLFlow: the GUI chat assistant](../../../../sqlflow/docs/reference/guides/chat-assistant.md)
- [SQLFlow: the Slack assistant](../../../../sqlflow/docs/reference/guides/slack-assistant.md)
- [Authentication and identity](authentication-and-identity.md)
