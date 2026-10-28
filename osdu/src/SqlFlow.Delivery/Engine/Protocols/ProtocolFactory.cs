using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Core.Secrets;

namespace SqlFlow.Delivery.Engine.Protocols;

/// <summary>Builds the named protocol a flow declares (osdu/docs/reference/flow/routes.md, Naming the route).</summary>
public static class ProtocolFactory
{
    public static async Task<IDeliveryProtocol> CreateAsync(FlowDefinition flow, HttpRuntime http, ISecretResolver secrets, ILoggerFactory loggers, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(loggers);

        var client = await ClientAsync(http, flow.Target.Endpoint, flow.Target.Auth, flow.Target.Headers, secrets, ct).ConfigureAwait(false);
        var options = flow.Target.ProtocolOptions;
        var ceiling = flow.Reliability.MaxRequestBodyBytes;
        IDeliveryProtocol route = flow.Target.Protocol switch
        {
            DeliveryProtocol.Storage => new OsduRecordProtocol(client, options),
            // The DDMSs the flow names by registration are read here, so every operation routes by what they registered.
            DeliveryProtocol.Ddms => new OsduDdmsProtocol(
                client, options, loggers.CreateLogger<OsduDdmsProtocol>(), ceiling, routing: await RoutingAsync(flow, client, ct).ConfigureAwait(false)),
            DeliveryProtocol.File => new OsduFileProtocol(client, options, ceiling),
            DeliveryProtocol.Dataset => new OsduDatasetProtocol(client, options, loggers.CreateLogger<OsduDatasetProtocol>(), ceiling),
            DeliveryProtocol.Manifest => new OsduManifestProtocol(client, options, loggers.CreateLogger<OsduManifestProtocol>(), ceiling),
            DeliveryProtocol.FileAndDdms => new OsduFileAndDdmsProtocol(
                client, options, loggers.CreateLogger<OsduFileAndDdmsProtocol>(), ceiling, routing: await RoutingAsync(flow, client, ct).ConfigureAwait(false)),
            DeliveryProtocol.ManifestAndDdms => new OsduManifestAndDdmsProtocol(
                client, options, loggers.CreateLogger<OsduManifestAndDdmsProtocol>(), ceiling, routing: await RoutingAsync(flow, client, ct).ConfigureAwait(false)),
            DeliveryProtocol.Workflow => new OsduWorkflowProtocol(
                client,
                options,
                flow.Target.Workflow ?? throw new FlowValidationException($"{flow.SourcePath ?? flow.Name}: the workflow route runs the workflow the flow declares, and it declares none."),
                loggers.CreateLogger<OsduWorkflowProtocol>(),
                secrets,
                flow.Target.Airflow is { } airflow ? new Workflows.AirflowXCom(http, airflow, secrets) : new Workflows.LatestInfoXCom(client),
                ceiling),
            DeliveryProtocol.Dspdm => new OsduDspdmProtocol(client, options, flow.Target.Dspdm, loggers.CreateLogger<OsduDspdmProtocol>()),

            // The etp route speaks ETP 1.2 on a WebSocket rather than HTTP, and opens it through the flow's own stack.
            DeliveryProtocol.Etp => new OsduEtpProtocol(
                new Etp.EtpConnection(
                    http,
                    Etp.EtpConnection.WebSocketUri(client.Endpoint, flow.Target.Etp.Path),
                    flow.Target.Auth,
                    client.Headers,
                    flow.Reliability.UrlAllowlist,
                    new Etp.EtpSessionOptions
                    {
                        MaxMessageBytes = (int)Math.Min(flow.Target.Etp.MaxMessageBytes, int.MaxValue),
                        RequestTimeout = TimeSpan.FromSeconds(flow.Reliability.TimeoutSeconds),
                    },
                    loggers.CreateLogger<Etp.EtpSession>()),
                flow.Target.Etp,
                client.Header("data-partition-id"),
                loggers.CreateLogger<OsduEtpProtocol>()),
            _ => throw new FlowValidationException($"{flow.SourcePath ?? flow.Name}: target.protocol '{flow.Target.Protocol}' is not a known protocol."),
        };

        // A flow whose mapping's enrichments add records writes each record's enrichment records before its own route writes the
        // record, and one that runs workflows over them runs them over each record once it landed, each through the workflow route
        // itself.
        var workflows = (flow.Enrich ?? [])
            .Where(s => s.Workflow is not null)
            .Select(s => new StepWorkflow(
                s.Name,
                s.Workflow!.RunWhen,
                s.Records is { RegistersFiles: true },
                new OsduWorkflowProtocol(
                    client,
                    options,
                    s.Workflow,
                    loggers.CreateLogger<OsduWorkflowProtocol>(),
                    secrets,
                    flow.Target.Airflow is { } stepAirflow ? new Workflows.AirflowXCom(http, stepAirflow, secrets) : new Workflows.LatestInfoXCom(client),
                    ceiling)))
            .ToList();
        return flow.Enrich?.Any(s => s.Records is not null) == true || workflows.Count > 0
            ? new EnrichPartsProtocol(route, client, options, ceiling, workflows: workflows, registersFiles: flow.Enrich?.Any(s => s.Records is { RegistersFiles: true }) == true)
            : route;
    }

    /// <summary>Where a route that reaches a DDMS sends each record, with the registrations the flow names read.</summary>
    private static async Task<DdmsRouting> RoutingAsync(FlowDefinition flow, OsduHttpClient client, CancellationToken ct)
        => DdmsRouting.Of(await DdmsDiscovery.ResolveAsync(flow, client, ct).ConfigureAwait(false));

    /// <summary>The OSDU client over a declared endpoint, auth and headers, every secret reference resolved here and nowhere else.</summary>
    public static async Task<OsduHttpClient> ClientAsync(HttpRuntime http, string endpoint, TargetAuth auth, IReadOnlyDictionary<string, string> headers, ISecretResolver secrets, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(secrets);
        var resolvedEndpoint = await secrets.ResolveAsync(endpoint, ct).ConfigureAwait(false);
        var resolvedHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in headers)
        {
            resolvedHeaders[name] = await secrets.ResolveAsync(value, ct).ConfigureAwait(false);
        }

        return new OsduHttpClient(http, resolvedEndpoint, auth, resolvedHeaders);
    }
}
