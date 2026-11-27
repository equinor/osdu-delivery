using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Core;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Templates;
using SqlFlow.Delivery.Validation;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>
/// Two versions of a kind from the OSDU data definitions compared (osdu/docs/reference/concepts/template-versions.md): variable
/// by variable and as their files are published, and as a report on moving a mapping from one to the other, which sets every
/// synced mapping of the kind against the newer version. Both read the same comparison.
/// </summary>
public static partial class DeliveryTemplateEndpoints
{
    /// <summary>The most synced mappings of one kind a report sets against the newer version.</summary>
    private const int MaxReportedMappings = 200;

    /// <summary>The status the repository sync gives a mapping document that loads.</summary>
    private const string ValidMapping = "valid";

    private static async Task<Results<Ok<DeliveryOsduComparisonDto>, ProblemHttpResult>> CompareOsduSchemasAsync(
        string? fromRelease, string? fromKind, string? toRelease, string? toKind, OsduDataDefinitions definitions, CancellationToken ct)
    {
        var (compared, problem) = await CompareAsync(fromRelease, fromKind, toRelease, toKind, definitions, ct).ConfigureAwait(false);
        if (compared is null)
        {
            return problem!;
        }

        var comparison = compared.Comparison;
        return TypedResults.Ok(new DeliveryOsduComparisonDto(
            Side(definitions, compared.Before),
            Side(definitions, compared.After),
            compared.SameFile,
            compared.OnlyIdentifiersDiffer,
            string.Equals(compared.Before.Bundled.Schema.Version, compared.After.Bundled.Schema.Version, StringComparison.Ordinal),
            comparison.Count(TemplateChangeImpact.Breaking),
            comparison.Count(TemplateChangeImpact.Additive),
            comparison.Count(TemplateChangeImpact.Wording),
            comparison.Unchanged,
            comparison.Changes.Select(c => new DeliveryTemplateVariableChangeDto(
                c.Path, c.Change.ToString(), c.Impact.ToString(), c.Role.ToString(),
                c.Fields.Select(f => new DeliveryTemplateFieldChangeDto(f.Field, f.Before, f.After, f.Impact.ToString())).ToList())).ToList(),
            compared.SameShared,
            compared.Shared));
    }

    /// <summary>
    /// The report on moving from one version of a kind to another (<see cref="TemplateMoveReport"/>), as a file to save in the
    /// format asked for: the comparison <see cref="CompareOsduSchemasAsync"/> answers, with every synced mapping of the kind set
    /// against the newer version. Nothing is rendered, no cache is read and nothing is kept.
    /// </summary>
    private static async Task<Results<FileContentHttpResult, ProblemHttpResult>> ReportOsduComparisonAsync(
        string? fromRelease,
        string? fromKind,
        string? toRelease,
        string? toKind,
        string? format,
        OsduDataDefinitions definitions,
        OsduDbContext osdu,
        CatalogDbContext db,
        IRepositoryFolders folders,
        DeliveryDocumentLoader documents,
        TimeProvider time,
        CancellationToken ct)
    {
        if (TemplateMoveReport.FormatOf(format) is not { } chosen)
        {
            return Problem($"format '{format}' is not one of json, md.", StatusCodes.Status400BadRequest, "Unknown report format");
        }

        var (compared, problem) = await CompareAsync(fromRelease, fromKind, toRelease, toKind, definitions, ct).ConfigureAwait(false);
        if (compared is null)
        {
            return problem!;
        }

        var mappings = await MappingMovesAsync(OsduTemplate.From(compared.After.Bundled.Schema), osdu, db, folders, documents, ct).ConfigureAwait(false);
        var report = new TemplateMoveReport(
            MoveSide(definitions, compared.Before),
            MoveSide(definitions, compared.After),
            compared.SameFile,
            compared.OnlyIdentifiersDiffer,
            compared.Comparison,
            compared.SameShared,
            compared.Shared.Select(f => new TemplateMoveSharedSchema(f.Name, f.FromPath, f.ToPath, f.FromWebUrl, f.ToWebUrl)).ToList(),
            mappings,
            time.GetUtcNow().UtcDateTime);
        var (mediaType, _) = TemplateMoveReport.Describe(chosen);
        return TypedResults.File(Encoding.UTF8.GetBytes(report.Render(chosen)), mediaType, report.FileName(chosen));
    }

    /// <summary>
    /// Two versions of one kind read from the OSDU data definitions and compared: variable by variable, as their files are
    /// published, and the shared schemas each refers to, paired by name. Null with the problem to answer with when the request
    /// does not name two versions of one kind, or the data definitions cannot answer.
    /// </summary>
    private static async Task<(ComparedVersions? Compared, ProblemHttpResult? Problem)> CompareAsync(
        string? fromRelease, string? fromKind, string? toRelease, string? toKind, OsduDataDefinitions definitions, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(fromKind) || string.IsNullOrWhiteSpace(toKind)
            || fromKind.Trim().Split(':').Length != 4 || toKind.Trim().Split(':').Length != 4)
        {
            return (null, Problem("A comparison names both versions, fromKind and toKind, as authority:source:entityType:version.", StatusCodes.Status400BadRequest));
        }

        var from = fromKind.Trim();
        var to = toKind.Trim();
        if (!string.Equals(from[..from.LastIndexOf(':')], to[..to.LastIndexOf(':')], StringComparison.Ordinal))
        {
            return (null, Problem($"A comparison is between versions of one kind; '{from}' and '{to}' are different kinds.", StatusCodes.Status400BadRequest));
        }

        try
        {
            var before = new ComparedSide(
                await definitions.PublishedFileAsync(fromRelease, from, ct).ConfigureAwait(false),
                await definitions.FetchAsync(fromRelease, from, ct).ConfigureAwait(false));
            var after = new ComparedSide(
                await definitions.PublishedFileAsync(toRelease, to, ct).ConfigureAwait(false),
                await definitions.FetchAsync(toRelease, to, ct).ConfigureAwait(false));
            var comparison = TemplateComparer.Compare(OsduTemplate.From(before.Bundled.Schema), OsduTemplate.From(after.Bundled.Schema));

            // The shared schemas each version refers to, as published: a kind's own file can be the same in two releases
            // while a schema it refers to changed under the same version, and that is where its template's changes come from.
            var referenced = new List<DeliveryOsduReferencedFileDto>();
            var sameReferenced = 0;
            foreach (var pair in TemplateComparer.PairReferencedFiles(before.Bundled.Files, after.Bundled.Files))
            {
                var beforeText = pair.BeforePath is null ? null : await definitions.FileTextAsync(before.File.Release, pair.BeforePath, ct).ConfigureAwait(false);
                var afterText = pair.AfterPath is null ? null : await definitions.FileTextAsync(after.File.Release, pair.AfterPath, ct).ConfigureAwait(false);
                if (beforeText is not null && afterText is not null && TemplateComparer.SameContent(beforeText, afterText))
                {
                    sameReferenced++;
                    continue;
                }

                referenced.Add(new DeliveryOsduReferencedFileDto(
                    pair.Name,
                    pair.BeforePath,
                    pair.AfterPath,
                    pair.BeforePath is null ? null : definitions.FileWebUrl(before.File.Release, pair.BeforePath),
                    pair.AfterPath is null ? null : definitions.FileWebUrl(after.File.Release, pair.AfterPath),
                    beforeText,
                    afterText));
            }

            return (new ComparedVersions(
                before,
                after,
                comparison,
                TemplateComparer.SameContent(before.File.Text, after.File.Text),
                TemplateComparer.DifferOnlyInIdentifiers(before.File.Text, from, after.File.Text, to),
                sameReferenced,
                referenced), null);
        }
        catch (FlowValidationException ex)
        {
            return (null, Problem(ex.Message, StatusCodes.Status400BadRequest));
        }
        catch (DataDefinitionsException ex)
        {
            return (null, Unavailable(ex));
        }
        catch (DeliveryException ex)
        {
            // A release's file is there but is not a record schema a template can be laid out from.
            return (null, Problem(ex.Message, StatusCodes.Status422UnprocessableEntity, "Not a record schema"));
        }
    }

    /// <summary>
    /// Every mapping of a synced repository that fills the kind of <paramref name="target"/>, at any version of it, set against
    /// <paramref name="target"/> (<see cref="MappingMoves"/>): read from the document its repository's last sync kept, with the
    /// templates that repository's folders settle it to. At most <see cref="MaxReportedMappings"/>, by name.
    /// </summary>
    private static async Task<IReadOnlyList<TemplateMoveMapping>> MappingMovesAsync(
        OsduTemplate target, OsduDbContext osdu, CatalogDbContext db, IRepositoryFolders folders, DeliveryDocumentLoader documents, CancellationToken ct)
    {
        var prefix = target.Kind[..(target.Kind.LastIndexOf(':') + 1)];
        var rows = await osdu.DeliveryMappings.AsNoTracking()
            .Where(m => m.Kind.StartsWith(prefix))
            .OrderBy(m => m.Name).ThenBy(m => m.Version).ThenBy(m => m.RepoId)
            .Take(MaxReportedMappings)
            .ToListAsync(ct).ConfigureAwait(false);

        // A kind is matched as OSDU spells it, whatever the database's collation makes of case.
        rows = rows.Where(m => m.Kind.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        if (rows.Count == 0)
        {
            return [];
        }

        var repoIds = rows.Select(m => m.RepoId).Distinct().ToList();
        var names = await db.Repos.AsNoTracking()
            .Where(r => repoIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, r => r.Name, ct).ConfigureAwait(false);
        var moves = new List<TemplateMoveMapping>();
        foreach (var repo in rows.GroupBy(m => m.RepoId))
        {
            var templates = await SyncedTemplates.ReadAsync(osdu, folders, repo.Key, ct).ConfigureAwait(false);
            var repository = names.GetValueOrDefault(repo.Key) ?? repo.Key.ToString("D");
            foreach (var row in repo)
            {
                moves.Add(await MoveAsync(row, repository, templates, target, documents, ct).ConfigureAwait(false));
            }
        }

        return moves;
    }

    /// <summary>
    /// One synced mapping set against <paramref name="target"/>. A document its last sync found invalid, one that no longer
    /// loads, and one whose template cannot be read are listed with why, as not checked.
    /// </summary>
    private static async Task<TemplateMoveMapping> MoveAsync(
        DeliveryMapping row, string repository, SyncedTemplates templates, OsduTemplate target, DeliveryDocumentLoader documents, CancellationToken ct)
    {
        TemplateMoveMapping NotChecked(string kind, string? version, string problem)
            => new(repository, row.Reference, row.RelativePath, kind, version, new MappingMoveCheck(MappingMoveState.Unchecked, []), problem);

        var recorded = string.IsNullOrEmpty(row.TemplateVersion) ? null : row.TemplateVersion;
        if (!string.Equals(row.Status, ValidMapping, StringComparison.OrdinalIgnoreCase))
        {
            return NotChecked(row.Kind, recorded, row.Message ?? "its repository's last sync found the document invalid, and kept no reason.");
        }

        MappingDefinition mapping;
        try
        {
            mapping = documents.ParseMapping(row.Yaml, row.RelativePath, templates.For(row.RelativePath));
        }
        catch (Exception ex) when (ex is FlowValidationException or DeliveryException)
        {
            return NotChecked(row.Kind, recorded, ex.Message);
        }

        var resolved = await templates.ResolveAsync(mapping.TemplateDocument, mapping.Template.Kind, mapping.Template.Version, ct).ConfigureAwait(false);
        if (resolved.Schema is not { } schema)
        {
            return NotChecked(mapping.Template.Kind, mapping.Template.Version, $"its template cannot be read: {resolved.Problem}");
        }

        return new TemplateMoveMapping(
            repository, row.Reference, row.RelativePath, mapping.Template.Kind, mapping.Template.Version, MappingMoves.Check(mapping, OsduTemplate.From(schema), target));
    }

    private static DeliveryOsduComparisonSideDto Side(OsduDataDefinitions definitions, ComparedSide side)
        => new(
            side.Bundled.Schema.Kind,
            Release(definitions, side.File.Release),
            side.File.Path,
            definitions.FileWebUrl(side.File.Release, side.File.Path),
            side.File.Status,
            side.Bundled.Schema.Version,
            side.File.Text);

    private static TemplateMoveSide MoveSide(OsduDataDefinitions definitions, ComparedSide side)
        => new(
            side.Bundled.Schema.Kind,
            side.File.Release.Name,
            side.File.Path,
            definitions.FileWebUrl(side.File.Release, side.File.Path),
            side.File.Status,
            side.Bundled.Schema.Version);

    /// <summary>A version as a comparison reads it: its file as published, and bundled as a template.</summary>
    private sealed record ComparedSide(DataDefinitionsPublishedFile File, DataDefinitionsSchemaFile Bundled);

    /// <summary>
    /// Two versions compared: each side, what differs variable by variable, whether the published files are the same or differ
    /// only in their own version identifiers, and the shared schemas they refer to that differ, with how many are the same.
    /// </summary>
    private sealed record ComparedVersions(
        ComparedSide Before,
        ComparedSide After,
        TemplateComparison Comparison,
        bool SameFile,
        bool OnlyIdentifiersDiffer,
        int SameShared,
        IReadOnlyList<DeliveryOsduReferencedFileDto> Shared);
}
