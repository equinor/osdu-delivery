using SqlFlow.Core;
using SqlFlow.Core.Lineage;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Model;
using SqlFlow.Yaml;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// A removal flow as the platform sees it: the key sources it reads are its source reference, the delivery flows whose ledgers
/// it writes to its target, and the connections of its source tables are what the hygiene check inspects. It needs the
/// repository tree, since it opens the delivery flows it names and reads their mappings. Its lineage is what it reads (the
/// key sources, and the records the delivery flows deliver), so it runs after the flows that land the keys and deliver the
/// records (<see cref="RemovalLineage"/>).
/// </summary>
public sealed record RemovalFlowDocument : RegisteredFlowDocument
{
    public required RemovalFlowDefinition Flow { get; init; }

    public override string Name => Flow.Name;

    public override string Kind => RemovalFlowDefinition.FlowTypeName;

    public override string? Batch => Flow.Batch;

    /// <summary>The key sources the removals read, in document order: a reader's location, or a table's name.</summary>
    public override string? SourceReference => string.Join(", ", Flow.Entries
        .SelectMany(e => e.Sources())
        .Select(s => s.Source.Table?.ObjectName ?? s.Source.Reader?.Location ?? s.Source.Reader?.Type ?? string.Empty)
        .Where(s => s.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase));

    /// <summary>The delivery flows whose records it takes out and whose ledgers it writes to.</summary>
    public override string? TargetReference => string.Join(", ", Flow.Entries.Select(e => e.Flow).Distinct(StringComparer.OrdinalIgnoreCase));

    public override IEnumerable<KeyValuePair<string, string>> CredentialReferences => Flow.CredentialReferences();

    public override bool RequiresRepoTree => true;

    public override IReadOnlyList<DeclaredDataObject> DeclaredObjects => RemovalLineage.DeclaredObjects(Flow);

    public override RegisteredFlowLineage DescribeLineage(RegisteredLineageContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return RemovalLineage.Describe(Flow, context);
    }
}

/// <summary>The <c>flowType: removal</c> document kind, registered in every host next to its executor.</summary>
public sealed class RemovalFlowKind : IFlowDocumentKind
{
    private readonly DeliveryDocumentLoader _loader;

    public RemovalFlowKind(DeliveryDocumentLoader loader)
    {
        ArgumentNullException.ThrowIfNull(loader);
        _loader = loader;
    }

    public string FlowType => RemovalFlowDefinition.FlowTypeName;

    public string Description => "read the keys a source holds, and the keys it lists as removed, compare them with the ledgers of the delivery flows it names, and take out of OSDU, into those ledgers, the records whose keys the source no longer holds";

    public IReadOnlyList<FlowKindOperation> Operations { get; } =
    [
        new(DeliveryOperations.Remove, "Remove", "Read every removal's key sources, compare them with its delivery flow's ledger, and take out of OSDU (reversibly) the records whose keys the source no longer holds, unless the guard stops the run for an operator's approval.", WritesTarget: true),
        new(DeliveryOperations.Plan, "Plan", "Read the key sources and compare them with the ledgers as a removal does, and report what it would take out, changing nothing in OSDU or a ledger.", WritesTarget: false),
    ];

    public RegisteredFlowDocument Parse(string yaml, string source)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        return new RemovalFlowDocument { Flow = _loader.ParseRemoval(yaml, source) };
    }

    public void ValidateParameters(RunParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        DeliveryOperations.RefuseSeveralValues(parameters, RemovalFlowDefinition.FlowTypeName, scopeTakesSeveral: false);
        DeliveryOperations.RefuseBuiltInOverrides(parameters, RemovalFlowDefinition.FlowTypeName, "a removal reads every key its sources hold; name the removals to carry out in the payload.");
        var payload = DeliveryRunPayload.Parse(parameters);
        payload.RefuseOtherThan(
            RemovalFlowDefinition.FlowTypeName,
            [DeliveryRunPayload.EntriesProperty, DeliveryRunPayload.ApproveProperty, DeliveryRunPayload.ConfirmProperty]);

        var operation = Operation(parameters);
        if (payload.Approve is not null)
        {
            if (operation != DeliveryOperations.Remove)
            {
                throw new SqlFlowException(
                    $"payload {DeliveryRunPayload.ApproveProperty} does not apply to the {operation} operation: only a remove run carries out what an operator approved.");
            }

            if (string.IsNullOrWhiteSpace(payload.Confirm) || payload.Entries.Count != 1)
            {
                throw new SqlFlowException(
                    "An approved remove run names the one removal whose held keys it approves (entries), how many the operator was shown waiting for approval (approve.expected), and the partition it acts in (confirm); nothing is approved without all three.");
            }
        }
        else if (payload.Confirm is not null)
        {
            throw new SqlFlowException(
                $"payload {DeliveryRunPayload.ConfirmProperty} does not apply to a removal run without an approval: only an approved run names the partition it acts in.");
        }
    }

    /// <summary>The operation a removal flow runs: remove (its default) or plan.</summary>
    public static string Operation(RunParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return (parameters.Operation ?? DeliveryOperations.Remove) switch
        {
            DeliveryOperations.Remove => DeliveryOperations.Remove,
            DeliveryOperations.Plan => DeliveryOperations.Plan,
            var other => throw new SqlFlowException(
                $"A removal flow runs the {DeliveryOperations.Remove} and {DeliveryOperations.Plan} operations; '{other}' is not one of them."),
        };
    }
}

/// <summary>
/// What a removal flow contributes to SQLFlow's lineage (osdu/docs/reference/concepts/lineage.md, What each kind reads and
/// writes): it reads its key sources (the files a reader reads, the tables of a source database) and the records each delivery
/// flow it names delivers, so it runs after the flows landing the keys and after the deliveries whose ledgers it compares. It
/// writes nothing another flow reads: what it takes out of OSDU is the delivery flow's records, in the delivery flow's ledger.
/// </summary>
public static class RemovalLineage
{
    /// <summary>The tables of source databases the removals read, once each.</summary>
    public static IReadOnlyList<DeclaredDataObject> DeclaredObjects(RemovalFlowDefinition flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var objects = new List<DeclaredDataObject>();
        foreach (var (_, source) in flow.Entries.SelectMany(e => e.Sources()))
        {
            if (source.Table is not { } table)
            {
                continue;
            }

            DeclaredDataObject read;
            try
            {
                read = DeclaredDataObject.Reads(table.Connection, table.ObjectName) with { Provider = table.Provider };
            }
            catch (SqlFlowException)
            {
                // The mapper refuses an object that is not a three-part name; one that could not be parsed declares nothing.
                continue;
            }

            if (!objects.Contains(read))
            {
                objects.Add(read);
            }
        }

        return objects;
    }

    /// <summary>
    /// Everything the flow contributes: the files and tables its key sources read, and, for each delivery flow it names that the
    /// scan found in the estate, the OSDU records that flow writes, read here. A delivery flow the scan did not find (a document
    /// described on its own, or one another repository holds) is warned about; the run itself finds it.
    /// </summary>
    public static RegisteredFlowLineage Describe(RemovalFlowDefinition flow, RegisteredLineageContext context)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(context);
        var files = new List<DeclaredFileLocation>();
        foreach (var (_, source) in flow.Entries.SelectMany(e => e.Sources()))
        {
            if (source.Reader is { Location: { } location } reader && !location.Contains("${", StringComparison.Ordinal))
            {
                var file = new DeclaredFileLocation { Relation = LineageRelation.Reads, Location = location, FilePattern = FilePattern(reader) };
                if (!files.Contains(file))
                {
                    files.Add(file);
                }
            }
        }

        var warnings = new List<string>();
        var datasets = new List<DeclaredDataset>();
        foreach (var entry in flow.Entries)
        {
            var delivery = context.Estate.FirstOrDefault(d => d.Document is DeliveryFlowDocument doc && string.Equals(doc.Source.Name, entry.Flow, StringComparison.OrdinalIgnoreCase));
            if (delivery is null)
            {
                if (context.Estate.Count > 0)
                {
                    warnings.Add($"removal '{entry.Name}' names delivery flow '{entry.Flow}', which the scan did not find in this repository; the run looks for it where the catalog says it is.");
                }

                continue;
            }

            var written = delivery.Document.DescribeLineage(new RegisteredLineageContext(delivery.Path, context.EstateRoot) { Estate = context.Estate });
            foreach (var dataset in written.Datasets.Where(d => d.Relation == LineageRelation.Writes))
            {
                var read = dataset with { Relation = LineageRelation.Reads };
                if (!datasets.Contains(read))
                {
                    datasets.Add(read);
                }
            }
        }

        return new RegisteredFlowLineage { Objects = DeclaredObjects(flow), Files = files, Datasets = datasets, Warnings = warnings };
    }

    private static string? FilePattern(RemovalReaderSource reader)
        => reader.Options.TryGetValue("srcFile", out var pattern) && !string.IsNullOrWhiteSpace(pattern) ? pattern : null;
}
