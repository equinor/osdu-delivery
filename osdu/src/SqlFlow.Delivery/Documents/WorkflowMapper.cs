using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SqlFlow.Core;
using SqlFlow.Delivery.Engine.Workflows;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// The parts of a flow document the composed, manifest and workflow routes read (osdu/docs/reference/flow/routes.md):
/// the payload sets a composed route sends, the manifest-by-reference options, the workflow route's declaration and the
/// Airflow instance behind the Workflow service. Everything that needs no network is checked here, while the document
/// is read.
/// </summary>
internal static partial class FlowMapper
{
    /// <summary>The longest workflow input name.</summary>
    private const int MaxInputNameLength = 32;

    /// <summary>The longest run a flow may wait on, in minutes: a week.</summary>
    private const int MaxWorkflowTimeoutMinutes = 10_080;

    private const int MaxManifestInlineLimitKb = 1_048_576;

    private static string WorkflowKey(KeyPaths paths) => paths.Interface is null ? "target.workflow" : $"interfaces.{paths.Interface}.workflow";

    /// <summary>The key a message names for a payload set: a workflow input under the workflow, any other set where it is declared.</summary>
    private static string PartKey(FlowDefinition flow, string payload, KeyPaths paths)
        => flow.Target.Workflow?.Inputs.Any(i => string.Equals(i.Name, payload, StringComparison.Ordinal)) == true
            ? $"{WorkflowKey(paths)}.inputs.{payload}"
            : paths.Payload(payload);

    /// <summary>The workflow route's declaration, or null for a flow that declares none.</summary>
    private static WorkflowRoute? MapWorkflow(WorkflowYaml? declared, DeliveryProtocol protocol, bool declaresFiles, string source, KeyPaths paths)
    {
        var at = WorkflowKey(paths);
        if (declared is null)
        {
            return protocol == DeliveryProtocol.Workflow
                ? throw new FlowValidationException($"{source}: the workflow route runs the workflow {at} declares, and the document declares none.")
                : null;
        }

        if (protocol != DeliveryProtocol.Workflow)
        {
            throw new FlowValidationException($"{source}: {at} declares a workflow, which only the workflow route runs, and the flow's route is {protocol}. Remove {at}, or name the workflow route.");
        }

        return MapWorkflowRoute(declared, at, declaresFiles, source);
    }

    /// <summary>
    /// The workflow a flow runs over the record of an enrichment of its mapping once the record and the enrichment's datasets
    /// landed (<c>workflows.&lt;name&gt;</c>, osdu/docs/reference/flow/enrich-workflow.md): stages, results, secrets and when it
    /// runs, as the workflow route declares them, over the record the flow's route wrote. The record is written already, so it
    /// names no anchor and writes no anchor tag; the enrichment's datasets are registered already, so it names no inputs and
    /// reads them as <c>{dataset:enrich.&lt;name&gt;}</c>.
    /// </summary>
    private static WorkflowRoute MapStepWorkflow(WorkflowYaml declared, string at, string source)
    {
        if (declared.Inputs is { Count: > 0 })
        {
            throw new FlowValidationException(
                $"{source}: {at}.inputs registers files as the workflow's inputs, and an enrichment's files are registered as its datasets and read by the workflow as {{dataset:enrich.<name>}}; remove {at}.inputs.");
        }

        if (!string.IsNullOrWhiteSpace(declared.Anchor) || !string.IsNullOrWhiteSpace(declared.AnchorTag))
        {
            throw new FlowValidationException(
                $"{source}: {at} names an anchor or an anchor tag, and an enrichment's workflow runs over the record the flow's route wrote, which it neither registers nor tags; remove {at}.anchor and {at}.anchorTag.");
        }

        return MapWorkflowRoute(declared, at, declaresFiles: false, source) with { Anchor = WorkflowAnchor.Storage };
    }

    /// <summary>A workflow declaration under <paramref name="at"/>: its stages, inputs, results, secrets and when it runs.</summary>
    private static WorkflowRoute MapWorkflowRoute(WorkflowYaml declared, string at, bool declaresFiles, string source)
    {
        if (declared.Stages is not { Count: > 0 } stages)
        {
            throw new FlowValidationException($"{source}: {at}.stages lists no stage; name the workflow each run triggers and its context.");
        }

        if (stages.Count > WorkflowRoute.MaxStages)
        {
            throw new FlowValidationException(
                string.Create(CultureInfo.InvariantCulture, $"{source}: {at}.stages lists {stages.Count} stages; a route runs at most {WorkflowRoute.MaxStages}."));
        }

        var mapped = new List<WorkflowStage>(stages.Count);
        for (var n = 0; n < stages.Count; n++)
        {
            var stageAt = string.Create(CultureInfo.InvariantCulture, $"{at}.stages[{n}]");
            var stage = stages[n] ?? throw new FlowValidationException($"{source}: {stageAt} is empty.");
            var outputs = new Dictionary<string, WorkflowOutput>(StringComparer.Ordinal);
            foreach (var (key, output) in stage.Outputs ?? [])
            {
                var name = key?.Trim() ?? string.Empty;
                if (!OutputName().IsMatch(name))
                {
                    throw new FlowValidationException($"{source}: {stageAt}.outputs names '{name}'; an output name is a letter followed by letters, digits, '_' and '-'.");
                }

                outputs[name] = MapOutput(output, $"{stageAt}.outputs.{name}", source);
            }

            mapped.Add(new WorkflowStage
            {
                Workflow = Require(stage.Workflow, stageAt + ".workflow", source),
                Contract = Optional(stage.Contract),
                Context = ContextTemplate(stage.Context, stageAt + ".context", source),
                TimeoutMinutes = stage.TimeoutMinutes ?? 0,
                PollSeconds = stage.PollSeconds ?? 0,
                Outputs = outputs,
            });
        }

        var inputs = new List<WorkflowInput>();
        foreach (var (key, input) in declared.Inputs ?? [])
        {
            var name = key?.Trim() ?? string.Empty;
            var inputAt = $"{at}.inputs.{name}";
            if (input is null)
            {
                throw new FlowValidationException($"{source}: {inputAt} declares nothing; an input needs at least its root and its location column.");
            }

            inputs.Add(new WorkflowInput(name, Optional(input.DatasetKind) ?? "osdu:wks:dataset--File.Generic:1.0.0", input.Optional ?? false));
        }

        return new WorkflowRoute
        {
            Anchor = string.IsNullOrWhiteSpace(declared.Anchor)
                ? AnchorOf(null, declaresFiles)
                : ParseEnum<WorkflowAnchor>(declared.Anchor.Trim(), at + ".anchor", source),
            Stages = mapped,
            Inputs = inputs,
            Results = MapResults(declared.Results, at + ".results", source),
            RunWhen = ParseEnum(declared.RunWhen, WorkflowRunWhen.Changed, at + ".runWhen", source),
            Secrets = (declared.Secrets ?? []).ToDictionary(kv => kv.Key.Trim(), kv => kv.Value?.Trim() ?? string.Empty, StringComparer.Ordinal),
            AnchorTagKey = Optional(declared.AnchorTag),
        };
    }

    /// <summary>The source with the workflow's inputs as payload sets of their own, so they are planned, hashed and read like any other.</summary>
    private static FlowSource WithInputs(FlowSource mapped, WorkflowYaml? declared, string source, KeyPaths paths)
    {
        if (declared?.Inputs is not { Count: > 0 } inputs)
        {
            return mapped;
        }

        var payloads = new Dictionary<string, FlowPayload>(mapped.Payloads, StringComparer.Ordinal);
        foreach (var (key, input) in inputs)
        {
            var name = key?.Trim() ?? string.Empty;
            var at = $"{WorkflowKey(paths)}.inputs.{name}";
            if (!InputName().IsMatch(name) || name.Length > MaxInputNameLength)
            {
                throw new FlowValidationException(
                    string.Create(CultureInfo.InvariantCulture, $"{source}: {WorkflowKey(paths)}.inputs names '{name}'; an input name is a letter followed by letters, digits, '_' and '-', at most {MaxInputNameLength} characters."));
            }

            if (name is FilesPayload or BulkPayload || payloads.ContainsKey(name))
            {
                throw new FlowValidationException($"{source}: {at} takes a name the document already gives a payload set; name the input differently.");
            }

            var declaredInput = input ?? throw new FlowValidationException($"{source}: {at} declares nothing; an input needs at least its root and its location column.");
            payloads[name] = new FlowPayload
            {
                Root = Require(declaredInput.Root, at + ".root", source),
                LocationColumn = Optional(declaredInput.LocationColumn),
                Pattern = Optional(declaredInput.Pattern) ?? FlowPayload.DefaultPattern,
                HashColumn = Optional(declaredInput.HashColumn),
                ChunkCountColumn = Optional(declaredInput.ChunkCountColumn),
            };
        }

        return mapped with { Payloads = payloads };
    }

    private static AirflowAccess? MapAirflow(AirflowYaml? declared, string source)
    {
        if (declared is null)
        {
            return null;
        }

        var endpoint = Require(declared.Endpoint, "target.airflow.endpoint", source);
        return new AirflowAccess
        {
            Endpoint = endpoint,
            Auth = MapAuth(declared.Auth, source, "target.airflow.auth"),
            Headers = new Dictionary<string, string>(declared.Headers ?? [], StringComparer.OrdinalIgnoreCase),
            ApiVersion = ParseEnum(declared.ApiVersion, AirflowApiVersion.V1, "target.airflow.apiVersion", source),
        };
    }

    private static WorkflowOutput MapOutput(WorkflowOutputYaml? declared, string at, string source)
    {
        var output = declared ?? throw new FlowValidationException($"{source}: {at} declares nothing; give its value, or the XCom entry it is read from.");
        if ((output.Value is null) == (output.Xcom is null))
        {
            throw new FlowValidationException($"{source}: {at} names its value or the XCom entry it is read from (xcom: {{task, key}}), one of the two.");
        }

        if (output.Xcom is { } xcom)
        {
            return new WorkflowOutput
            {
                XComTask = Require(xcom.Task, at + ".xcom.task", source),
                XComKey = Require(xcom.Key, at + ".xcom.key", source),
                Match = Optional(xcom.Match),
            };
        }

        return new WorkflowOutput { Value = output.Value };
    }

    private static WorkflowResults MapResults(WorkflowResultsYaml? declared, string at, string source)
    {
        if (declared is null)
        {
            return new WorkflowResults();
        }

        var strategies = new List<WorkflowResultStrategy>();
        if (declared.Anchor == true)
        {
            strategies.Add(WorkflowResultStrategy.Anchor);
        }

        if (declared.Ids is not null)
        {
            strategies.Add(WorkflowResultStrategy.Ids);
        }

        if (declared.Artefact is not null)
        {
            strategies.Add(WorkflowResultStrategy.Artefact);
        }

        if (declared.Search is not null)
        {
            strategies.Add(WorkflowResultStrategy.Search);
        }

        if (declared.Manifest is not null)
        {
            strategies.Add(WorkflowResultStrategy.Manifest);
        }

        if (declared.Xcom is not null)
        {
            strategies.Add(WorkflowResultStrategy.XCom);
        }

        if (strategies.Count > 1)
        {
            throw new FlowValidationException($"{source}: {at} names {string.Join(", ", strategies.Select(s => s.ToString().ToLowerInvariant()))}; the results are found one way.");
        }

        var strategy = strategies.Count == 0 ? WorkflowResultStrategy.None : strategies[0];
        var defaults = new WorkflowResults();
        return new WorkflowResults
        {
            Strategy = strategy,
            Template = strategy switch
            {
                WorkflowResultStrategy.Ids => declared.Ids,
                WorkflowResultStrategy.Manifest => declared.Manifest,
                _ => null,
            },
            Kind = strategy == WorkflowResultStrategy.Search ? Require(declared.Search!.Kind, at + ".search.kind", source) : null,
            Query = strategy == WorkflowResultStrategy.Search ? Optional(declared.Search!.Query) : null,
            ArtefactRole = strategy == WorkflowResultStrategy.Artefact ? Require(declared.Artefact!.Role, at + ".artefact.role", source) : null,
            ArtefactKind = strategy == WorkflowResultStrategy.Artefact ? Require(declared.Artefact!.Kind, at + ".artefact.kind", source) : null,
            XCom = strategy == WorkflowResultStrategy.XCom
                ? MapOutput(new WorkflowOutputYaml { Xcom = declared.Xcom }, at, source)
                : null,
            Minimum = declared.Minimum ?? (strategy is WorkflowResultStrategy.None or WorkflowResultStrategy.Anchor ? 0 : 1),
            WaitSeconds = declared.WaitSeconds,
            MaxRecorded = declared.Keep ?? defaults.MaxRecorded,
            Remove = declared.Remove ?? defaults.Remove,
        };
    }

    /// <summary>A context template: the YAML mapping as JSON, every scalar keeping the type YAML read it as.</summary>
    private static JsonObject ContextTemplate(Dictionary<string, object?>? declared, string at, string source)
    {
        var context = new JsonObject();
        foreach (var (key, value) in declared ?? [])
        {
            context[key] = ContextValue(value, $"{at}.{key}", source);
        }

        return context;
    }

    private static JsonNode? ContextValue(object? value, string at, string source) => value switch
    {
        null => null,
        string text => JsonValue.Create(text),
        bool flag => JsonValue.Create(flag),
        byte or sbyte or short or ushort or int or uint or long => JsonValue.Create(Convert.ToInt64(value, CultureInfo.InvariantCulture)),
        ulong number => JsonValue.Create(number),
        double number when !double.IsFinite(number) => throw new FlowValidationException($"{source}: {at} is NaN or Infinity, which JSON cannot carry."),
        double number => JsonValue.Create(number),
        float number when !float.IsFinite(number) => throw new FlowValidationException($"{source}: {at} is NaN or Infinity, which JSON cannot carry."),
        float number => JsonValue.Create(Rendering.NumberValues.Widen(number)),
        decimal number => JsonValue.Create(number),
        IDictionary<object, object?> map => new JsonObject(map.Select(kv => new KeyValuePair<string, JsonNode?>(
            Convert.ToString(kv.Key, CultureInfo.InvariantCulture) ?? string.Empty,
            ContextValue(kv.Value, $"{at}.{kv.Key}", source)))),
        IEnumerable<object?> list => new JsonArray(list.Select((item, n) => ContextValue(item, string.Create(CultureInfo.InvariantCulture, $"{at}[{n}]"), source)).ToArray()),
        _ => JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture)),
    };

    /// <summary>
    /// The payload sets of a route that sends parts: each part the route needs is declared, with the columns a plan
    /// reads it by, and nothing is declared the route would not send.
    /// </summary>
    private static void ValidateParts(FlowDefinition flow, IReadOnlyList<PayloadPart> parts, string source, KeyPaths paths)
    {
        var route = DeliveryProtocols.Name(flow.Target.Protocol);
        if (flow.Target.ProtocolOptions.Payload is { } selected)
        {
            throw new FlowValidationException(
                $"{source}: {paths.Shared("target.protocolOptions.payload")} names '{selected}', and the {route} route sends its parts by their own names ({string.Join(", ", parts.Select(p => p.Payload))}). Remove it.");
        }

        var required = flow.Target.Protocol switch
        {
            DeliveryProtocol.FileAndDdms => new[] { FilesPayload, BulkPayload },
            DeliveryProtocol.ManifestAndDdms => [BulkPayload],
            _ => [],
        };
        foreach (var name in required)
        {
            if (!flow.Source.Payloads.ContainsKey(name))
            {
                throw new FlowValidationException($"{source}: the {route} route sends {string.Join(" and ", required)}, and {paths.Payload(name)} is not declared.");
            }
        }

        foreach (var name in flow.Source.Payloads.Keys)
        {
            if (!parts.Any(p => string.Equals(p.Payload, name, StringComparison.Ordinal)))
            {
                throw new FlowValidationException(
                    $"{source}: {paths.Payload(name)} is declared, and the {route} route sends {(parts.Count == 0 ? "no payload set" : string.Join(", ", parts.Select(p => p.Payload)))}; it would never be sent. Remove it.");
            }
        }

        foreach (var part in parts)
        {
            RequirePartColumns(flow, part.Payload, source, paths);
        }
    }

    /// <summary>A payload set a route sends names where each record's files are, and how a change to them is seen.</summary>
    private static void RequirePartColumns(FlowDefinition flow, string payloadName, string source, KeyPaths paths)
    {
        var payload = flow.Source.Payloads[payloadName];
        var key = PartKey(flow, payloadName, paths);
        if (payload.LocationColumn is null)
        {
            throw new FlowValidationException(
                $"{source}: the {flow.Target.Protocol} protocol streams payload '{payloadName}', so {key}.locationColumn must name the record column holding each record's payload folder.");
        }

        if (payload.HashColumn is null && flow.Change.PayloadDetect != ChangeDetection.LastModified)
        {
            throw new FlowValidationException(
                $"{source}: the flow decides payload changes by content hash, so {key}.hashColumn must name the record column holding it; or take the files' modified times instead with {paths.Shared("change.payloadDetect")}: lastModified.");
        }
    }

    /// <summary>True when the flow's route sends files a payload watermark can be taken from.</summary>
    private static bool SendsFiles(FlowDefinition flow)
        => DeliveryProtocols.CarriesPayload(flow.Target.Protocol) && flow.Source.Payloads.Count > 0;

    /// <summary>The manifest-by-reference options, and the Dataset service paths, as far as the document shows them.</summary>
    private static void ValidateReferenceOptions(FlowDefinition flow, string source, KeyPaths paths)
    {
        var options = flow.Target.ProtocolOptions;
        if (options.ManifestByReference != ManifestReference.Never
            && flow.Target.Protocol is not (DeliveryProtocol.Manifest or DeliveryProtocol.ManifestAndDdms))
        {
            throw new FlowValidationException(
                $"{source}: {paths.Shared("target.protocolOptions.manifestByReference")} says how manifests reach the ingestion workflow, and the flow's route ({DeliveryProtocols.Name(flow.Target.Protocol)}) sends none.");
        }

        if (options.FilesContentType is not null && flow.Target.Protocol is DeliveryProtocol.Storage or DeliveryProtocol.Ddms)
        {
            throw new FlowValidationException(
                $"{source}: {paths.Shared("target.protocolOptions.filesContentType")} says what type a record's files are uploaded as, and the flow's route ({DeliveryProtocols.Name(flow.Target.Protocol)}) uploads no files; payloadContentType names the type of what it sends.");
        }

        if (options.ManifestInlineLimitKb is < 1 or > MaxManifestInlineLimitKb)
        {
            throw new FlowValidationException(
                string.Create(CultureInfo.InvariantCulture, $"{source}: {paths.Shared("target.protocolOptions.manifestInlineLimitKb")} must be between 1 and {MaxManifestInlineLimitKb}."));
        }

        if (!WorkflowCatalog.IsWorkflowName(options.ByReferenceWorkflowName))
        {
            throw new FlowValidationException($"{source}: {paths.Shared("target.protocolOptions.byReferenceWorkflowName")} '{options.ByReferenceWorkflowName}' is not a workflow name.");
        }

        if (!WorkflowCatalog.IsWorkflowName(options.WorkflowName))
        {
            throw new FlowValidationException($"{source}: {paths.Shared("target.protocolOptions.workflowName")} '{options.WorkflowName}' is not a workflow name.");
        }

        foreach (var (key, path, token) in new[]
        {
            ("datasetInstructionsPath", options.DatasetInstructionsPath, (string?)null),
            ("datasetRegisterPath", options.DatasetRegisterPath, null),
            ("datasetRetrievalPath", options.DatasetRetrievalPath, null),
            ("datasetSoftDeletePath", options.DatasetSoftDeletePath, "{id}"),
            ("workflowPath", options.WorkflowPath, "{workflow}"),
        })
        {
            if (path is null)
            {
                continue;
            }

            var absolute = Uri.TryCreate(path, UriKind.Absolute, out var url) && url.Scheme is "http" or "https";
            if ((path[0] != '/' && !absolute) || (token is not null && !path.Contains(token, StringComparison.Ordinal)))
            {
                throw new FlowValidationException(
                    $"{source}: {paths.Shared("target.protocolOptions." + key)} '{path}' must be a path under the endpoint starting with '/', or an absolute http(s) URL"
                    + (token is null ? "." : $", with {token} where it goes."));
            }
        }
    }

    /// <summary>
    /// The workflow route's declaration, as far as the document shows it: the stages name workflows that are delivery
    /// targets, their contexts read and hold what each workflow's contract requires, every placeholder names something
    /// the route has, credentials are references, and the results are found a way the flow can reach.
    /// </summary>
    private static void ValidateWorkflow(FlowDefinition flow, string source, KeyPaths paths)
    {
        if (flow.Target.Airflow is { } airflow)
        {
            if (flow.Target.Protocol != DeliveryProtocol.Workflow)
            {
                throw new FlowValidationException($"{source}: target.airflow says where the Airflow behind the Workflow service is, which only the workflow route reads, and the flow's route is {DeliveryProtocols.Name(flow.Target.Protocol)}.");
            }

            if (!airflow.Endpoint.Contains("${", StringComparison.Ordinal)
                && !(Uri.TryCreate(airflow.Endpoint, UriKind.Absolute, out var endpoint) && endpoint.Scheme is "http" or "https"))
            {
                throw new FlowValidationException($"{source}: target.airflow.endpoint must be an absolute http(s) URL or a reference to one.");
            }
        }

        // Each enrichment's workflow is checked as the route's is, with no inputs of its own (osdu/docs/reference/flow/enrich-workflow.md).
        foreach (var (name, enrichWorkflow) in flow.EnrichWorkflows)
        {
            ValidateWorkflowRoute(enrichWorkflow, new HashSet<string>(StringComparer.Ordinal), $"{paths.Workflows}.{name}", source);
        }

        if (flow.Target.Workflow is not { } workflow)
        {
            return;
        }

        var at = WorkflowKey(paths);
        var files = flow.Source.Payloads.ContainsKey(FilesPayload);
        if (workflow.Anchor == WorkflowAnchor.Dataset && !files)
        {
            throw new FlowValidationException(
                $"{source}: {at}.anchor is dataset, which registers each record with its files through the dataset service, and {paths.Payload(FilesPayload)} declares none. Declare the files, or anchor the workflow on a storage record.");
        }

        var inputs = workflow.Inputs.Select(i => i.Name).ToHashSet(StringComparer.Ordinal);
        if (files)
        {
            inputs.Add(FilesPayload);
        }

        ValidateWorkflowRoute(workflow, inputs, at, source);
    }

    /// <summary>
    /// One workflow declaration under <paramref name="at"/>, the route's or a step's: its inputs, secrets, anchor tag, stages
    /// and results, every placeholder naming something it has; <paramref name="inputs"/> are the inputs its templates may name.
    /// </summary>
    private static void ValidateWorkflowRoute(WorkflowRoute workflow, IReadOnlySet<string> inputs, string at, string source)
    {
        if (workflow.Inputs.Count > WorkflowRoute.MaxInputs)
        {
            throw new FlowValidationException(
                string.Create(CultureInfo.InvariantCulture, $"{source}: {at}.inputs lists {workflow.Inputs.Count} inputs; a route registers at most {WorkflowRoute.MaxInputs}."));
        }

        foreach (var input in workflow.Inputs)
        {
            if (!IsRecordKind(input.DatasetKind) || !(Identity.TargetId.EntityTypeFromKind(input.DatasetKind).StartsWith("dataset--", StringComparison.Ordinal)))
            {
                throw new FlowValidationException(
                    $"{source}: {at}.inputs.{input.Name}.datasetKind '{input.DatasetKind}' must be a dataset kind, 'authority:source:dataset--Type:major.minor.patch'.");
            }
        }

        foreach (var (name, reference) in workflow.Secrets)
        {
            if (!InputName().IsMatch(name))
            {
                throw new FlowValidationException($"{source}: {at}.secrets names '{name}'; a secret name is a letter followed by letters, digits, '_' and '-'.");
            }

            if (!SecretReference().IsMatch(reference))
            {
                throw new FlowValidationException(
                    $"{source}: {at}.secrets.{name} must be a whole secret reference, ${{env:NAME}} or ${{keyvault:NAME}}; a credential is never written into the document.");
            }
        }

        // Storage takes any tag key, and the index keeps record tags for every kind; a search names the key as a field path,
        // where '-' and '.' need escaping (osdu/specs/workflows/INTEGRATION.md section 5.2), so the key is a plain identifier.
        if (workflow.AnchorTagKey is { } tag && !TagKey().IsMatch(tag))
        {
            throw new FlowValidationException($"{source}: {at}.anchorTag '{tag}' must be a tag key a search can name: a letter followed by letters, digits and '_', at most 64 characters (osduDeliveryAnchor, for example).");
        }

        for (var n = 0; n < workflow.Stages.Count; n++)
        {
            var stage = workflow.Stages[n];
            var stageAt = string.Create(CultureInfo.InvariantCulture, $"{at}.stages[{n}]");
            if (!WorkflowCatalog.IsWorkflowName(stage.Workflow))
            {
                throw new FlowValidationException($"{source}: {stageAt}.workflow '{stage.Workflow}' is not a workflow name: letters, digits, '_', '-' and '.'.");
            }

            var contract = WorkflowCatalog.Find(stage.Workflow, stage.Contract);
            if (stage.Contract is { } named && contract is null)
            {
                throw new FlowValidationException(
                    $"{source}: {stageAt}.contract '{named}' is not a workflow OSDU Delivery knows; it knows {string.Join(", ", WorkflowCatalog.All.Select(c => c.Name))}.");
            }

            if (stage.TimeoutMinutes is < 0 or > MaxWorkflowTimeoutMinutes || stage.PollSeconds is < 0 or > 3600)
            {
                throw new FlowValidationException(
                    string.Create(CultureInfo.InvariantCulture, $"{source}: {stageAt}.timeoutMinutes must be between 0 and {MaxWorkflowTimeoutMinutes}, and pollSeconds between 0 and 3600 (0 takes the default)."));
            }

            var problems = WorkflowTemplate.Problems(stage.Context, $"{stageAt}.context").ToList();

            // A credential is never written into the document, whether or not OSDU Delivery knows the workflow's contract.
            problems.AddRange(WorkflowContextCheck.CheckCredentials(contract, stage.Context, $"{stageAt}.context"));
            if (contract is not null)
            {
                problems.AddRange(WorkflowContextCheck.CheckTemplate(contract, stage.Context, contract.AddsPayload, $"{stageAt}.context"));
                if (n == workflow.Stages.Count - 1 && contract.TranslatesOnly)
                {
                    problems.Add(
                        $"{stageAt} runs {stage.Workflow}, which only translates and ingests nothing ({contract.Source}); add a stage that ingests its manifest with {ProtocolOptions.DefaultByReferenceWorkflowName}.");
                }
            }

            // An XCom output is read through the Workflow service's latestInfo, which serves the run's latest task, or from
            // Airflow when target.airflow says where it is (osdu/specs/workflows/INTEGRATION.md section 5.2.1); which task
            // ran last is known only when the run has finished.
            foreach (var (name, output) in stage.Outputs)
            {
                if (output.Value is { } value)
                {
                    problems.AddRange(WorkflowTemplate.Problems(JsonValue.Create(value), $"{stageAt}.outputs.{name}.value"));
                    problems.AddRange(PlaceholderProblems(workflow, inputs, at, JsonValue.Create(value), n + 1, $"{stageAt}.outputs.{name}.value"));
                }
            }

            if (problems.Count == 0)
            {
                problems.AddRange(PlaceholderProblems(workflow, inputs, at, stage.Context, n + 1, $"{stageAt}.context"));
            }

            if (problems.Count > 0)
            {
                throw new FlowValidationException($"{source}: {string.Join(" ", problems.Select(p => p.EndsWith('.') ? p : p + "."))}");
            }
        }

        var results = workflow.Results;
        var resultsAt = at + ".results";
        var templates = new List<(string Where, string Text)>();
        if (results.Template is { } template)
        {
            templates.Add((results.Strategy == WorkflowResultStrategy.Ids ? resultsAt + ".ids" : resultsAt + ".manifest", template));
        }

        if (results.Kind is { } kind)
        {
            templates.Add((resultsAt + ".search.kind", kind));
        }

        if (results.Query is { } query)
        {
            templates.Add((resultsAt + ".search.query", query));
        }

        foreach (var (where, text) in templates)
        {
            var problems = WorkflowTemplate.Problems(JsonValue.Create(text), where).ToList();
            if (problems.Count == 0)
            {
                problems.AddRange(PlaceholderProblems(workflow, inputs, at, JsonValue.Create(text), workflow.Stages.Count + 1, where));
            }

            if (problems.Count > 0)
            {
                throw new FlowValidationException($"{source}: {string.Join(" ", problems)}.");
            }
        }

        if (results.Minimum < 0 || results.WaitSeconds is < 0 or > 86_400 || results.MaxRecorded is < 0 or > 1000)
        {
            throw new FlowValidationException($"{source}: {resultsAt}.minimum must not be negative, waitSeconds must be between 0 and 86400, and keep between 0 and 1000.");
        }

        if (results.Strategy == WorkflowResultStrategy.None && results.Minimum > 0)
        {
            throw new FlowValidationException($"{source}: {resultsAt}.minimum asks for records, and the results say no way to find them.");
        }
    }

    /// <summary>
    /// What a template's placeholders name that the route does not have. <paramref name="stage"/> is the stage the
    /// template belongs to, counted from 1 (one past the last for the results): it reads only the stages before it.
    /// </summary>
    private static IEnumerable<string> PlaceholderProblems(WorkflowRoute workflow, IReadOnlySet<string> inputs, string at, JsonNode? template, int stage, string where)
    {
        foreach (var (path, placeholder) in WorkflowTemplate.Placeholders(template))
        {
            var argument = placeholder.Argument;
            switch (placeholder.Name)
            {
                case "secret" when !workflow.Secrets.ContainsKey(argument!):
                    yield return $"{where}{path} names the secret '{argument}', which {at}.secrets does not declare";
                    break;
                case "input":
                    var name = argument!.Split('[')[0];
                    if (!inputs.Contains(name))
                    {
                        yield return $"{where}{path} names the input '{name}', and the route registers {(inputs.Count == 0 ? "none" : string.Join(", ", inputs))}";
                    }

                    break;
                case "stage":
                    var dot = argument!.IndexOf('.', StringComparison.Ordinal);
                    var number = int.Parse(argument[..dot], NumberStyles.None, CultureInfo.InvariantCulture);
                    var output = argument[(dot + 1)..];
                    var last = stage - 1;
                    if (number < 1 || number > last)
                    {
                        yield return $"{where}{path} reads stage {number}, and only the stages before it ({(last < 1 ? "none" : "1 to " + last.ToString(CultureInfo.InvariantCulture))}) have run by then";
                    }
                    else if (!workflow.Stages[number - 1].Outputs.ContainsKey(output))
                    {
                        yield return $"{where}{path} reads '{output}', which stage {number} does not declare under outputs";
                    }

                    break;
                case "runId" when stage > workflow.Stages.Count:
                    yield return $"{where}{path} reads a run id, and the results are found after every run";
                    break;
            }
        }
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_\-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex InputName();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_\-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex OutputName();

    [GeneratedRegex(@"^\$\{(env|keyvault):[^{}\s]+\}$", RegexOptions.CultureInvariant)]
    private static partial Regex SecretReference();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex TagKey();
}
