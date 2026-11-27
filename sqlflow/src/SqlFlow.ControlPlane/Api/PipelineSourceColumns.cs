using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Background;
using SqlFlow.Core;
using SqlFlow.Core.Compute;
using SqlFlow.Core.Lineage;
using SqlFlow.Core.Model;
using SqlFlow.Core.Secrets;
using SqlFlow.Execution;
using SqlFlow.Yaml;

namespace SqlFlow.ControlPlane.Api;

/// <summary>What a source scan reads: how many of the newest files (1 to 200, 20 when omitted), the columns asked about,
/// and the pool of nodes to read them from (the default pool when omitted), as a run of the flow is placed.</summary>
public sealed record SourceColumnsScanRequest(int? Files, IReadOnlyList<string>? Expect, string? Pool);

/// <summary>
/// A source scan accepted: the compute task that reads it (<c>GET /api/v1/datasources/tasks/{taskId}</c>, optionally
/// long-polled, whose result is the <see cref="SourceColumnsReport"/>), the pipeline and its flow.
/// </summary>
public sealed record SourceColumnsScanAccepted(Guid TaskId, string Status, Guid PipelineId, string Flow);

/// <summary>
/// The columns the files at a file flow's source carry (<c>POST /pipelines/{id}/source-columns</c>), read as a run of the
/// flow reads them. The control plane takes the flow's document as the catalog stores it (the text a run of the pipeline
/// executes), the path its relative location resolves against, and the table it loads into as the catalog holds it, and
/// queues one <see cref="ComputeOperations.ScanSourceColumns"/> task under the pipeline's repository, so the task carries
/// the values that repository's runs are supplied with. A node then reads the newest files' headers with its own
/// identity (<see cref="SourceColumnScanner"/>). The caller names the pipeline, never a location, so nothing but what a
/// flow already declares is read, and nothing of a row.
/// </summary>
public static class PipelineSourceColumns
{
    internal static async Task<Results<Accepted<SourceColumnsScanAccepted>, ProblemHttpResult>> ScanAsync(
        Guid id, SourceColumnsScanRequest? request, CatalogDbContext db, YamlDocumentLoader documents, IRunDispatcher dispatcher,
        ClaimsPrincipal user, CancellationToken ct)
    {
        var files = request?.Files ?? ComputeTaskPayload.DefaultScanFiles;
        if (files is < 1 or > ComputeTaskPayload.MaxScanFiles)
        {
            return Invalid($"files must be between 1 and {ComputeTaskPayload.MaxScanFiles}.");
        }

        var expected = (request?.Expect ?? [])
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (expected.Count > ComputeTaskPayload.MaxScanExpected || expected.Any(name => name.Length > 128))
        {
            return Invalid($"expect names at most {ComputeTaskPayload.MaxScanExpected} columns, each at most 128 characters.");
        }

        if (request?.Pool is { Length: > 128 } || (request?.Pool?.Any(char.IsControl) ?? false))
        {
            return Invalid("pool must be at most 128 characters with no control characters.");
        }

        var pipeline = await db.Pipelines.AsNoTracking()
            .Where(p => p.Id == id)
            .Select(p => new { p.Name, p.Kind, p.Yaml, p.RelativePath, p.RepoId })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (pipeline is null)
        {
            return TypedResults.Problem(detail: $"No pipeline with id '{id}'.", statusCode: StatusCodes.Status404NotFound, title: "Not found");
        }

        var root = await db.Repos.AsNoTracking().Where(r => r.Id == pipeline.RepoId).Select(r => r.RootPath)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        var flowFile = string.IsNullOrWhiteSpace(root) ? null : Path.GetFullPath(Path.Combine(root, pipeline.RelativePath));

        // Parsed here only to refuse a flow that reads no files, or a relative location with nothing to resolve it
        // against, with a 400 rather than a queued task doomed to fail. Nothing is resolved or read here.
        FlowDefinition flow;
        try
        {
            if (documents.Parse(pipeline.Yaml, flowFile ?? pipeline.RelativePath) is not FileFlowDocument document)
            {
                return Invalid($"Flow '{pipeline.Name}' is a '{pipeline.Kind}' flow. Only a file flow reads files, so only its source can be scanned.");
            }

            flow = document.Flow;
        }
        catch (SqlFlowException ex)
        {
            return Invalid($"Flow '{pipeline.Name}' could not be read as the last sync stored it: {SecretHygiene.RedactedMessage(ex)}");
        }

        if (flowFile is null && SourceColumnScanner.IsRelativeFile(flow.Source.Location))
        {
            return Invalid($"Flow '{pipeline.Name}' reads '{flow.Source.Location}', relative to its own folder, and the control plane holds no checkout of its repository to resolve it against. Sync the repository.");
        }

        var payload = new ComputeTaskPayload
        {
            Operation = ComputeOperations.ScanSourceColumns,
            SourceRef = pipeline.Name,
            Flow = pipeline.Yaml,
            FlowFile = flowFile,
            Files = files,
            Columns = expected.Count == 0 ? null : expected,
            ScanTarget = await TargetAsync(db, id, flow.Target, ct).ConfigureAwait(false),
        };
        try
        {
            payload.Validate();
        }
        catch (SqlFlowException ex)
        {
            return Invalid(ex.Message);
        }

        // The task belongs to the pipeline's repository, so it resolves the flow's references with the values that
        // repository's runs are supplied with, and is recorded with who asked.
        var pool = string.IsNullOrWhiteSpace(request?.Pool) ? null : request.Pool.Trim();
        var taskId = await dispatcher.EnqueueComputeTaskAsync(
            db,
            new ComputeTaskEnqueueRequest(
                payload.Operation, payload.SourceRef, null, payload.ToJson(), pool, RequestActor.Of(user), pipeline.RepoId),
            ct).ConfigureAwait(false);

        return TypedResults.Accepted(
            $"/api/v1/datasources/tasks/{taskId}",
            new SourceColumnsScanAccepted(taskId, RunStatuses.Queued, id, pipeline.Name));
    }

    /// <summary>
    /// The table the flow loads into, as the catalog holds it: the object the flow's lineage writes (or creates) whose schema
    /// and name are the target's, with its columns in order. Null when lineage names no such object or the catalog holds no
    /// columns of it.
    /// </summary>
    private static async Task<SourceScanTarget?> TargetAsync(CatalogDbContext db, Guid pipelineId, TargetSpec target, CancellationToken ct)
    {
        var writes = nameof(LineageRelation.Writes);
        var creates = nameof(LineageRelation.Creates);
        var keys = await db.LineageEdges.AsNoTracking()
            .Where(e => e.PipelineId == pipelineId && (e.Relation == writes || e.Relation == creates))
            .Select(e => e.ObjectKey)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);
        if (keys.Count == 0)
        {
            return null;
        }

        var objects = await db.Objects.AsNoTracking()
            .Where(o => keys.Contains(o.Key) && o.Schema == target.Schema && o.Name == target.Table)
            .Select(o => new { o.Key, o.Database, o.Schema, o.Name })
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var item in objects.OrderByDescending(o => o.Database is not null).ThenBy(o => o.Key, StringComparer.Ordinal))
        {
            var columns = await db.ObjectColumns.AsNoTracking()
                .Where(c => c.ObjectKey == item.Key)
                .OrderBy(c => c.Ordinal)
                .Select(c => c.Name)
                .ToListAsync(ct).ConfigureAwait(false);
            if (columns.Count > 0)
            {
                return new SourceScanTarget(item.Key, item.Database, item.Schema ?? target.Schema, item.Name, columns);
            }
        }

        return null;
    }

    private static ProblemHttpResult Invalid(string detail)
        => TypedResults.Problem(detail: detail, statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
}
