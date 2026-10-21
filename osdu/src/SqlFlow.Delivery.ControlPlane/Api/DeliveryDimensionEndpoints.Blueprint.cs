using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.ControlPlane.Hosting;
using SqlFlow.Core;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Dimensions;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Templates;
using SqlFlow.Delivery.Catalog;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>
/// The dimension's declaration as its flow document writes it: the file, the lines of its item under <c>dimensions</c>
/// (comments included) from <c>FirstLine</c>, where each thing it declares is written, and whether the item was cut.
/// </summary>
public sealed record DeliveryDimensionYamlDto(string? File, int FirstLine, IReadOnlyList<string> Lines, IReadOnlyList<DimensionYamlSpan> Spans, bool Cut);

/// <summary>How much of an attribute the dimension's last build read: the keys holding a value read for it, and how many values those are.</summary>
public sealed record DeliveryDimensionCoverageDto(string Name, int Keys, int Values);

/// <summary>
/// How a dimension is built (osdu/docs/reference/concepts/dimension-blueprint.md): the flow and partition it is read
/// in, whether the flow declares it, its number in the ledger once built, its declaration as the flow's YAML writes it
/// (or why it is not shown), the blueprint the declaration and the templates the flow reads make, and how much of each attribute
/// its last build read.
/// </summary>
public sealed record DeliveryDimensionBlueprintDto(
    string Flow, string Dimension, string? Partition, bool Declared, int? DimensionId, DeliveryDimensionYamlDto? Yaml, string? YamlMissing,
    DimensionBlueprint Blueprint, IReadOnlyList<DeliveryDimensionCoverageDto> Coverage);

public static partial class DeliveryDimensionEndpoints
{
    /// <summary>
    /// How a dimension of a flow is built, in the partition the request reads: its YAML beside what each line does, the records
    /// it reads and the template describing each, step by step through the records its keys name, and the table's columns
    /// with the reads each is written from. A dimension the flow no longer declares is laid out from what its last build read
    /// with. It reads the catalog, the templates the flow's repository holds as its last sync kept them, and the ledger;
    /// nothing here talks to OSDU.
    /// </summary>
    private static async Task<Results<Ok<DeliveryDimensionBlueprintDto>, ProblemHttpResult>> GetBlueprintAsync(
        Guid pipelineId, string name, string? partition, HttpRequest request, CatalogDbContext db, DeliveryDocumentLoader documents, ILedger ledger,
        IPartitionRegistry registry, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > DeliveryDimension.MaxNameLength)
        {
            return Problem(StatusCodes.Status400BadRequest, "No dimension named", "Name the dimension whose blueprint to read.");
        }

        var pipeline = await db.Pipelines.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pipelineId, ct).ConfigureAwait(false);
        if (pipeline is null)
        {
            return Problem(StatusCodes.Status404NotFound, "Not found", $"No pipeline '{pipelineId}'.");
        }

        if (!string.Equals(pipeline.Kind, DimensionFlowDefinition.FlowTypeName, StringComparison.OrdinalIgnoreCase))
        {
            return Problem(StatusCodes.Status409Conflict, "Not a dimension flow", $"Pipeline '{pipeline.Name}' is a '{pipeline.Kind}' flow, not a dimension flow.");
        }

        DimensionFlowDefinition flow;
        try
        {
            flow = documents.ParseDimension(pipeline.Yaml, pipeline.RelativePath);
        }
        catch (FlowValidationException ex)
        {
            return Problem(StatusCodes.Status409Conflict, "Flow does not parse", $"The catalog's copy of {pipeline.Name} does not parse: {ex.Message} Re-sync the repository.");
        }

        var named = WorkbenchPartition.Named(partition, request);
        var registered = await registry.ReadAsync(ct).ConfigureAwait(false);
        var (bound, kept, _) = await SettleAsync(flow, named, ledger, registered, ct).ConfigureAwait(false);
        var spec = flow.Dimension(name);
        var state = bound is null ? null : await ledger.FindDimensionAsync(bound.LedgerId, spec?.Name ?? name.Trim(), ct).ConfigureAwait(false);
        if (spec is null && state is null)
        {
            return Problem(StatusCodes.Status404NotFound, "Not found", $"Dimension flow {flow.Name} declares no dimension '{name.Trim()}', and its ledger keeps none of that name.");
        }

        // The kinds the last build read describe the records while the declaration still reads that kind; a pattern changed
        // since is described by the templates the flow reads that it matches, as its next build will be: those of the
        // template folders its repository's last sync recorded on the flow's path.
        var described = spec ?? FromState(state!);
        IReadOnlyList<DimensionKind> kinds = [];
        if (state?.LastRunId is { } runId && string.Equals(described.Kind, state.Kind, StringComparison.Ordinal))
        {
            var runs = await ledger.GetDimensionRunsAsync([runId], ct).ConfigureAwait(false);
            kinds = runs.Count == 0 ? [] : DimensionRunner.KindsOf(runs[0].Read.Templates);
        }

        var folders = await SyncedTemplates.ReadAsync(ledger, pipeline.RepoId, ct).ConfigureAwait(false);
        var blueprint = await DimensionBlueprints.DescribeAsync(described, folders, pipeline.RelativePath, kinds, ct).ConfigureAwait(false);
        var coverage = state is null ? [] : await ledger.DimensionAttributeCoverageAsync(state.DimensionId, ct).ConfigureAwait(false);

        DeliveryDimensionYamlDto? yaml = null;
        string? yamlMissing;
        if (spec is null)
        {
            yamlMissing = $"{flow.Name} no longer declares {described.Name}: what is shown is what its last build read with.";
        }
        else if (DimensionYamlSource.Locate(pipeline.Yaml, spec.Name) is { } block)
        {
            yaml = new DeliveryDimensionYamlDto(pipeline.RelativePath, block.FirstLine, block.Lines, block.Spans, block.Cut);
            yamlMissing = null;
        }
        else
        {
            yamlMissing = $"The YAML of {pipeline.RelativePath} could not be laid out line by line; the pipeline's page shows the whole file.";
        }

        return TypedResults.Ok(new DeliveryDimensionBlueprintDto(
            flow.Name, described.Name, kept ?? named, spec is not null, state?.DimensionId, yaml, yamlMissing, blueprint,
            coverage.Select(c => new DeliveryDimensionCoverageDto(c.Name, c.Keys, c.Values)).ToList()));
    }

    /// <summary>A dimension its flow no longer declares, as its last build read it.</summary>
    private static DimensionSpec FromState(DimensionState state)
    {
        var label = DimensionRunner.LabelOf(state.LabelJson);
        var valueColumn = state.ValueColumn ?? DimensionColumnNames.ValueOf(label, state.Name);
        return new DimensionSpec
        {
            Name = state.Name,
            Description = state.Description,
            Kind = state.Kind,
            Query = state.Query,
            Path = state.Path,
            Label = label,
            Attributes = DimensionRunner.AttributesOf(state.AttributesJson),
            Clean = DimensionRunner.StepsOf(state.CleanJson),
            KeyColumn = state.KeyColumn ?? DimensionColumnNames.KeyOf(state.Path, valueColumn),
            ValueColumn = valueColumn,
        };
    }
}
