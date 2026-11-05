---
id: delivery-wiki-0046-eu-ai-act
title: "Decision 0046: under the EU AI Act the assistant is not high-risk, says a model writes its answers, and marks what it wrote"
type: decision
summary: "Why the assistant is a limited-risk AI system under the EU AI Act, how it meets Article 50, and what else was added to keep people in charge."
keywords:
  - eu ai act
  - ai act
  - regulation 2024/1689
  - digital omnibus
  - article 50
  - transparency obligation
  - ai disclosure
  - marking ai-generated content
  - high-risk ai
  - annex iii
  - ai literacy
  - provider and deployer
  - general-purpose ai model
  - compliance
updated: 2026-11-05
sourceRefs:
  - sqlflow/src/SqlFlow.Assistant/AssistantDisclosure.cs
  - sqlflow/src/SqlFlow.Assistant/AssistantInstructions.cs
  - sqlflow/src/SqlFlow.ControlPlane/Api/ChatEndpoints.cs
  - sqlflow/src/SqlFlow.ControlPlane/Api/QuestionExampleEndpoints.cs
  - sqlflow/src/SqlFlow.ControlPlane/Security/AssistantDelegation.cs
  - sqlflow/src/SqlFlow.ControlPlane/Background/QuestionSearch.cs
  - sqlflow/src/SqlFlow.ControlPlane/Maintenance/CatalogStorageContributor.cs
  - sqlflow/src/SqlFlow.Catalog/CatalogEntities.cs
  - sqlflow/src/SqlFlow.Catalog/ChatRetention.cs
  - sqlflow/src/SqlFlow.SlackBot/SlackAssistantHandler.cs
  - sqlflow/tools/sqlflow-mcp/src/server.rs
  - sqlflow/gui/src/features/chat/answerProvenance.ts
  - osdu/src/SqlFlow.Delivery.ControlPlane/Configuration/DeliveryAssistantAccess.cs
  - osdu/gui/src/features/delivery/RecordContentView.tsx
  - osdu/deploy/bicep/ai-foundry.bicep
  - osdu/deploy/bicep/main.bicep
referenceRefs:
  - delivery-concept-eu-ai-act
  - delivery-concept-ai-features
  - delivery-guide-mcp
  - guide-chat-assistant
  - guide-slack-assistant
related:
  - delivery-wiki-0029-storage-maintenance
  - delivery-wiki-0039-vscode-extension-is-an-mcp-client
  - wiki-the-mcp-server-is-the-assistants-one-definition
  - wiki-semantic-layer-is-the-allow-list
---
# 0046: Under the EU AI Act the assistant is not high-risk, says a model writes its answers, and marks what it wrote

Status: accepted. The product's side is built and documented in [EU AI Act compatibility](../../reference/concepts/eu-ai-act.md)
and [AI in OSDU Delivery](../../reference/concepts/ai-features.md). The reading of the Act below is the implementation's,
made from the sources it names; it is not legal advice, and counsel confirms it.

The human's words: "I need you to check if osdu delivery is compatible with EU AI act. We probably need to add some
stuff to ai", "We need to create an MD files on this as part of the documentation as well.", and "Can you please bridge
the gasp and make osdu delivery compatible. Create an md file about compatablity."

## Context

**The law on 2026-11-05.** The AI Act, Regulation (EU) 2024/1689, entered into force on 1 August 2024. Its prohibitions and
its AI literacy article have applied since 2 February 2025, and the obligations of general-purpose AI model providers
since 2 August 2025. The Digital Omnibus on AI, Regulation (EU) 2026/1744, was published in the Official Journal on 24
July 2026 and entered into force on 27 July 2026. It moved the high-risk obligations to 2 December 2027 for the
stand-alone systems of Annex III and to 2 August 2028 for systems embedded in the products of Annex I. It left the
transparency obligations of Article 50 applying from 2 August 2026, and gave a system placed on the market before that
date until 2 December 2026 to mark its output under Article 50(2). It changed Article 4 on AI literacy, which the sources
read as turning the duty to ensure staff literacy into one of supporting it. It kept, in a simplified form, the
registration of an Annex III system its provider concludes is not high-risk. The Commission's Code of Practice on
transparency of AI-generated content was published in its final form on 10 June 2026; signing it is voluntary, Article 50
is not.

Sources: the European Parliament's
[Legislative Train on the Digital Omnibus on AI](https://www.europarl.europa.eu/legislative-train/package-digital-package/file-digital-omnibus-on-ai),
[Cooley's summary of the adopted text](https://cdp.cooley.com/digital-ai-omnibus-delays-key-deadlines-introduces-new-rules/),
[Gibson Dunn on the provisional agreement](https://www.gibsondunn.com/eu-ai-act-omnibus-agreement-postponed-high-risk-deadlines-and-other-key-changes/)
and the Commission's page on the
[Code of Practice on AI-generated content](https://digital-strategy.ec.europa.eu/en/policies/code-practice-ai-generated-content).
The Official Journal text of 2026/1744 was not read in full, so the wording of the amended Article 4 and of the Article
50(2) grace period is to be checked against it.

**What the product held.** Five features call a language model, all of them SQLFlow's assistant: the GUI assistant (with
Explain and voice input), the Slack assistant, synonym expansion of a `!` question, and questions generated for report
visuals. None is on the delivery path: no flow, mapping, render, submission or ledger write calls a model, and the MCP
server calls none. A chat or Slack run works under a token delegated for one run, which the control plane refuses on
every route not opened to the assistant; the delivery module opens only its readers. An inventory of the code found what
the Act's Article 50 asked and the product did not do: nothing told a person a language model wrote the answers, and no
stored answer or generated question said a model wrote it. It found four more things a person would not expect: the
model's own confirmation of an answer was stored as the person's, its instructions forbade it to say an answer was
unverified, Explain sent record content though the documentation said the assistant reads it only through a `!`
question, and conversations and query results were kept with no limit an estate could set.

## Decision

### The roles

- The model providers (Microsoft through Azure AI Foundry, OpenAI, Anthropic) are providers of general-purpose AI
  models. Their obligations under Chapter V are theirs.
- Each assistant feature is an AI system built on such a model. Whoever places OSDU Delivery on the market, or puts it
  into service under its own name, is the provider of those systems (Article 3(3)); the organisation that runs an estate
  is their deployer (Article 3(4)). An organisation that builds and runs it for itself is both.
- The MCP server is not an AI system: it infers nothing and only answers tool calls. A client that uses it (Claude Code,
  Copilot, Cursor) is its vendor's AI system, and the organisation that uses that client is its deployer.

### Not prohibited and not high-risk

Nothing the assistant does is a practice of Article 5. It is no safety component of a product under Annex I. Of Annex
III, the two areas near this product are point 2, AI used as a safety component in the management and operation of
critical digital infrastructure, road traffic, or the supply of water, gas, heating or electricity, and point 4, AI used
to monitor and evaluate the performance and behaviour of workers. The assistant is neither:

- it answers questions about pipelines, records and data; the delegation fence keeps it from starting a run, changing
  OSDU, the ledger, a mapping or a repository, so it is no safety component of anything an estate operates;
- judging the people who operate the estate is not its intended purpose.

That holds for the intended purpose only. The ledger records who acted on each record and when, and the assistant reads
the audit trail (`delivery_activities`). An estate that asks it to rank or judge its operators uses it as point 4
describes, and Article 25 makes whoever puts it to that use the provider of a high-risk system. So the reference states
the intended purpose and names that use as outside it.

Since the assistant is not an Annex III system, the registration Article 6(3) asks of a provider who claims an Annex III
system is not high-risk does not apply to it.

### What Article 50 asks, and how it is met

| Duty | How the product meets it |
| --- | --- |
| 50(1), 50(5): a system that interacts with people tells them it is an AI system, clearly, at the latest at the first interaction | The chat and Explain say under the composer, from the first question on, that an AI language model writes the answers, which model on which provider, and that they can be wrong. The sentence is written once (`AssistantDisclosure`) and served by the control plane, so every surface says the same. The Slack bot introduces itself as an AI assistant and ends every answer with the mark. |
| 50(2): a provider of a system that writes text marks its output, machine-readable and detectable as generated | Every stored answer records its provider and model, served with it by the API; each answer in the chat carries "AI-generated by" its model, and an exported answer a header a program reads. A Slack answer carries `ai_generated`, `provider` and `model` in its metadata. A generated report question records its model. |
| 50(3), 50(4) | Do not apply: no emotion recognition or biometric categorisation, no deep fakes, nothing published to the public. |

Article 4 asks organisations, not products, about literacy; the product supports it with the reference pages, which say
what each feature does, sends and keeps.

### What the product added

SQLFlow owns the assistant and is under the same Act, so the first four are generic changes to `sqlflow/`, each in a
`sqlflow:` commit (the [change record](../../../../docs/sqlflow-changes.md#what-the-eu-ai-act-asks-of-an-assistant)); the
last two are this module's.

1. **Disclosure and marking** (Article 50). The disclosure and marks above. The mark is metadata stored with what the
   model wrote, not a watermark in the text: a model's text is watermarked at the model, which is its provider's to do,
   and for text the catalog keeps and an API serves, the stored metadata is the layer the product controls.
2. **A verdict is the person's.** The confirm endpoint refuses a GUI chat run's delegated token, which carries the
   person's own id: the model's confirmation was stored as `user-confirmed` under the person's id, the same as their own
   click, and the answer could then run with no click at all. The fence still admits the call, so the Slack bot, which
   has no buttons, confirms under its own credential, and the row names it. The GUI's surface rules tell the model never
   to record a verdict.
3. **The model may say an answer is unchecked.** The rule for offering a composed query forbade it to mention "that an
   answer is unverified"; that clause went, and the model says plainly, when asked, that a query it put together has not
   been checked. Scores and thresholds stay out, since they read as the assistant explaining its own plumbing.
4. **Retention an estate sets.** Chat conversations and compute tasks, query results included, became optional areas of
   the Maintenance page, kept until an admin sets a limit ([decision 0029](0029-storage-maintenance.md)).
5. **Explain stays off record content.** Every view of a source row, a rendered or previewed record or cache values shows
   it through `RecordContentView`, which turns Explain off, so the product does what its documentation says: record
   content reaches a model only through a `!` question over an approved AI context.
6. **The model's region is a deployment setting.** `aiFoundryModelSkuName` (Terraform: `ai_foundry_model_sku_name`) picks
   where the chat and transcription models process a prompt: `GlobalStandard`, `DataZoneStandard` or `Standard`.

Items 2 to 6 are not the Act's demands for a system of limited risk: 2 and 3 keep a person in charge of what the
assistant's work becomes, 4 and 5 are data protection and the product's own rule that what it says it does, it does, and
6 is data residency. They are here because the assessment found them.

One thing found was kept. Search serves a generated report question with the Power BI provenance of its report. The
provenance describes the answer, and the query behind a report question is the report's own; only the question's
wording was generated, and that wording now records the model that wrote it.

## Alternatives considered

- **Treat the assistant as high-risk and run a conformity assessment.** Rejected: no Annex III use is its purpose, and
  the obligations of Chapter III would cost a quality management system, technical documentation and registration for
  nothing the Act asks. The intended-purpose statement is what keeps that reading honest.
- **Rely on the disclosure being obvious.** Rejected: the exception is judged from the person's side, the entry is named
  "Assistant", and a person reading a Slack thread may not know the bot is a model. A disclosure is a line of text.
- **Refuse every confirmation an assistant run makes.** Rejected: the Slack bot has no buttons, and a person there says an
  answer is right in words; refusing the fence's whole surface would take saving answers from Slack. The delegated token
  is the case that stored a model's word as the person's.
- **Turn Explain off everywhere.** Rejected: a mapping's YAML, a template and a statement are definitions the assistant
  reads anyway, and explaining them is what Explain is for.
- **Default the model to `DataZoneStandard`.** Rejected: changing the default would replace the model deployments of every
  estate on its next apply, and the capacity a region offers differs per type; the estate chooses.
- **Switch the assistant off for estates in the EU.** Rejected: the duties are light, the additions meet them, and the
  switches remain for an estate that wants no model at all.

## Consequences

- An estate that turns the assistant on meets Article 50 with no setting of its own. What remains its own is in
  [What an estate does](../../reference/concepts/eu-ai-act.md#what-an-estate-does): literacy, the intended purpose, the
  region, the retention, the Slack bot, editor assistants and its records of processing.
- An answer stored before answers recorded their model is marked "AI-generated" without naming one; nothing backfills a
  model the catalog never recorded.
- Counsel confirms three readings: whether Article 50(2) reaches the answers of an assistant used inside one
  organisation, the amended text of Article 4, and whether OSDU Delivery was placed on the market before 2 August 2026.
- Every feature that calls a model changes [AI in OSDU Delivery](../../reference/concepts/ai-features.md), and a change to
  what the Act asks changes the compatibility page and this record.
