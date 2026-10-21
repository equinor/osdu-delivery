using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.SearchTerms;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The search terms in the module's database (osdu/docs/reference/concepts/search-terms.md): the repository sync
/// extracts them from its pipelines, every active delivery flow's tables with the mapping it renders them with, after
/// the mappings, cache declarations and interfaces; a term is a table's column, so two flows rendering one table give
/// one term; what a person makes of a term (a name, leaving it out, the route it is searched through, a note) holds
/// across every sync, moves with the term from the key it had before terms were keyed by their table, and stays, named
/// by its key, when no pipeline gives the term any longer; and a condition on a term becomes the condition the explorer
/// asks, through the term's route.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class SearchTermCatalogTests : IDisposable
{
    private const string WellLog = "work-product-component--WellLog";

    /// <summary>The tables the sample flow reads: a log's row, and its curves' rows under the dataset <c>curves</c>.</summary>
    private const string Header = "OsduData.silver.WellLog";
    private const string Curves = "OsduData.silver.WellLogCurve";

    private readonly OsduTestDatabase _module = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sqlflow-search-terms-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _repo = Guid.NewGuid();
    private readonly TestClock _clock = new();

    public SearchTermCatalogTests()
    {
        // The sample estate as a repository holds it: the flows, the mappings they pin, the template folder holding the
        // templates those fill, and the cache flows.
        CopyDirectory(Samples.Source, _root);
    }

    public void Dispose()
    {
        _module.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private async Task<(IReadOnlyList<string> Warnings, SqlFlow.Catalog.CatalogSyncExtensionResult Result)> SyncAsync()
    {
        var warnings = new List<string>();
        await using var db = _module.CreateDbContext();
        var result = await new DeliveryCatalogSync(new DeliveryDocumentLoader()).ReconcileAsync(db, _repo, _root, _clock.GetUtcNow().UtcDateTime, warnings, CancellationToken.None);
        return (warnings, result);
    }

    private async Task<T> WithDirectoryAsync<T>(Func<SearchTermDirectory, Task<T>> read)
    {
        await using var db = _module.CreateDbContext();
        return await read(new SearchTermDirectory(db, new DeliveryDocumentLoader(), _clock));
    }

    private Task<IReadOnlyList<SearchTermView>> TermsAsync(bool orphans = false) => WithDirectoryAsync(d => d.ListAsync(WellLog, null, orphans, CancellationToken.None));

    private static Guid IdOf(string column, string? dataset = null) => SearchTermKey.Of(dataset is null ? Header : Curves, column).Id;

    private Task<SearchTermView> RefineAsync(Guid id, SearchTermRefinementRequest request)
        => WithDirectoryAsync(d => d.RefineAsync(id, null, request, "tester", CancellationToken.None));

    /// <summary>
    /// A second delivery flow beside the sample's, as the estate holds one: the same tables rendered by the next version of
    /// the mapping, under a source system of its own, so its records are records of their own, filling the next version of
    /// the template, which the template folder holds beside the current one.
    /// </summary>
    private void AddNextVersionFlow()
    {
        var current = Samples.SampleTemplate(WellLogVersions.CurrentKind);
        var next = Samples.SampleTemplate(WellLogVersions.NextKind);
        var file = WellLogVersions.NextKind.Replace(':', '_') + ".json";
        File.Copy(Path.Combine(Samples.FixtureTemplateFiles, file), Path.Combine(_root, "templates", file), overwrite: true);
        var mapping = File.ReadAllText(Path.Combine(_root, "mappings", "WellLog@1.4.0.yaml"))
            .Replace("\nversion: 1.4.0", "\nversion: 1.5.0", StringComparison.Ordinal)
            .Replace("  kind: osdu:wks:work-product-component--WellLog:1.4.0", "  kind: " + WellLogVersions.NextKind, StringComparison.Ordinal)
            .Replace("  version: " + current.Version, "  version: " + next.Version, StringComparison.Ordinal)
            .Replace("  system: welldb\n", "  system: welldb-welllog-1.5.0\n", StringComparison.Ordinal)
            .Replace("  system: welldb\r\n", "  system: welldb-welllog-1.5.0\r\n", StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(_root, "mappings", "WellLog@1.5.0.yaml"), mapping);
        var flow = File.ReadAllText(Path.Combine(_root, "flows", "welldb-welllog-03-header-delivery.yaml"))
            .Replace("name: welldb-welllog-03-header-delivery", "name: welldb-welllog-03-header-delivery-v150", StringComparison.Ordinal)
            .Replace("mapping: WellLog@1.4.0", "mapping: WellLog@1.5.0", StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(_root, "flows", "welldb-welllog-03-header-delivery-v150.yaml"), flow);
    }

    [Fact]
    public async Task The_sync_extracts_the_terms_of_the_mappings_the_active_delivery_flows_pin()
    {
        var (warnings, _) = await SyncAsync();
        Assert.DoesNotContain(warnings, w => w.Contains("search term", StringComparison.OrdinalIgnoreCase));

        await using var db = _module.CreateDbContext();
        var rows = await db.DeliverySearchTerms.AsNoTracking().Where(t => t.RepoId == _repo).ToListAsync();
        var wellbore = Assert.Single(rows, r => r.Column == "wellbore_uwi" && r.EntityType == WellLog);
        Assert.Equal((IdOf("wellbore_uwi"), "osdudata.silver.welllog/wellbore_uwi", WellLog, Header), (wellbore.TermId, wellbore.TermKey, wellbore.EntityType, wellbore.Source));
        Assert.Contains("welldb-welllog-03-header-delivery", wellbore.FlowsJson, StringComparison.Ordinal);
        Assert.Equal(SearchRouteKind.Search, Assert.Single(SearchRoute.FromJson(wellbore.RoutesJson)).Kind);
        Assert.Contains(rows, r => r.Source == Curves && r.Column == "curve_unit");

        // A sync of the same repository changes nothing.
        var again = (await SyncAsync()).Result;
        await using var after = _module.CreateDbContext();
        Assert.Equal(rows.Count, await after.DeliverySearchTerms.CountAsync(t => t.RepoId == _repo));
        Assert.Equal(0, again.Added);
    }

    [Fact]
    public async Task A_term_is_listed_by_its_columns_name_with_the_route_it_is_searched_through_classified_by_the_templates_its_mapping_reads()
    {
        // The repository's template folder describes no unit of measure.
        File.Delete(Path.Combine(_root, "templates", "osdu_wks_reference-data--UnitOfMeasure_1.0.0.json"));
        await SyncAsync();

        // A term's template is the one the template folders the last sync recorded give its mapping, read from the copy the
        // sync kept of the file. While that copy cannot be read (a row recorded before copies were kept), nothing of the
        // record's content can be compared, and each term says why; the next sync keeps it again.
        await using (var db = _module.CreateDbContext())
        {
            await db.DeliveryRepositoryTemplates.Where(t => t.RepoId == _repo && t.Kind == WellLogVersions.CurrentKind)
                .ExecuteUpdateAsync(set => set.SetProperty(t => t.SchemaJson, (string?)null));
        }

        var unread = Assert.Single(await TermsAsync(), t => t.Column == "log_run");
        Assert.Null(unread.Route);
        Assert.Contains("the template of WellLog@1.4.0 cannot be read", unread.Problem, StringComparison.Ordinal);
        Assert.Contains("the copy the sync keeps of it is missing; sync the repository again", unread.Problem, StringComparison.Ordinal);

        await SyncAsync();
        var terms = await TermsAsync();
        var run = Assert.Single(terms, t => t.Column == "log_run");
        Assert.Equal(("WellLog.log_run", $"{WellLog}|osdu.data.LogRun|Copy", (string?)null, false), (run.Name, run.Route, run.Problem, run.Renamed));
        Assert.Equal((Header, "WellLog"), (run.Source, run.Table));
        Assert.Equal([WellLogVersions.CurrentKind], run.Routes[0].Kinds);
        Assert.Equal("text", run.Routes[0].Index);
        Assert.Equal(new SearchTermSuggest(null, "data.LogRun", "text", null), run.Suggest);

        // Through the wellbores a search finds, which the Wellbore template says how to match: its values are their names.
        var wellbore = Assert.Single(terms, t => t.Column == "wellbore_uwi");
        Assert.Equal(new SearchTermSuggest("osdu:wks:master-data--Wellbore:*", "data.FacilityName", "text", null), wellbore.Suggest);
        Assert.Contains("is", wellbore.Routes[0].Conditions);

        // A route through a table of the database is listed and never chosen; one through the partition's units waits for a
        // template of them on the mapping's path, which says how a unit's code is indexed.
        var unit = Assert.Single(terms, t => t.ColumnLabel == "WellLogCurve.curve_unit");
        Assert.Null(unit.Route);
        Assert.Contains(unit.Routes, r => r.Kind == "steps" && r.Problem!.Contains("UnitAlias", StringComparison.Ordinal));
        Assert.Contains(unit.Routes, r => r.Kind == "lookup" && r.Problem!.Contains("no template the mapping reads of osdu:wks:reference-data--UnitOfMeasure:*", StringComparison.Ordinal));

        // Every kind the sample delivers is listed: the logs and surveys, the wells and wellbores they hang off, and the
        // reference data their records refer to; all but the units, whose mapping does not load while no template folder
        // on its path holds their template.
        var types = await WithDirectoryAsync(d => d.EntityTypesAsync(CancellationToken.None));
        Assert.Equal(
            [
                "master-data--Well", "master-data--Wellbore", "reference-data--LogCurveBusinessValue", "reference-data--LogCurveFamily",
                "reference-data--LogCurveMainFamily", "reference-data--LogCurveType", "reference-data--LogType",
                "reference-data--TechnicalAssuranceType", "reference-data--TrajectoryStationPropertyType", "reference-data--UnitQuantity",
                "reference-data--VerticalMeasurementType", "reference-data--WellLogSamplingDomainType", WellLog,
                "work-product-component--WellboreMarkerSet", "work-product-component--WellboreTrajectory",
            ],
            types.Select(t => t.EntityType).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task What_a_person_makes_of_a_term_holds_across_syncs_and_names_it_once_no_mapping_gives_it()
    {
        await SyncAsync();

        var renamed = await RefineAsync(IdOf("wellbore_uwi"), new SearchTermRefinementRequest("Wellbore name", false, null, "The UWI the well database files the log under."));
        Assert.Equal(("Wellbore name", true, "The UWI the well database files the log under.", "tester"), (renamed.Name, renamed.Renamed, renamed.Note, renamed.UpdatedBy));
        await RefineAsync(IdOf("log_run"), new SearchTermRefinementRequest("Run", true, null, null));

        // A sync writes the terms again and never what was made of them.
        await SyncAsync();
        var terms = await TermsAsync();
        Assert.Equal("Wellbore name", Assert.Single(terms, t => t.Column == "wellbore_uwi").Name);
        Assert.True(Assert.Single(terms, t => t.Column == "log_run").Excluded);

        // The mapping stops reading log_run: the term goes, and its refinement stays, named by its key, while asked for.
        var mapping = Path.Combine(_root, "mappings", "WellLog@1.4.0.yaml");
        File.WriteAllText(mapping, File.ReadAllText(mapping).Replace("    LogRun: { $from: log_run }\n", string.Empty, StringComparison.Ordinal).Replace("    LogRun: { $from: log_run }\r\n", string.Empty, StringComparison.Ordinal));
        await SyncAsync();
        Assert.DoesNotContain(await TermsAsync(), t => t.Column == "log_run");
        var orphan = Assert.Single(await TermsAsync(orphans: true), t => t.Column == "log_run");
        Assert.Equal((true, "Run", true), (orphan.Orphan, orphan.Name, orphan.Excluded));
        await Assert.ThrowsAsync<DeliveryException>(() => RefineAsync(IdOf("log_run"), new SearchTermRefinementRequest("Again", false, null, null)));

        // Removing the refinement removes the orphan.
        Assert.True(await WithDirectoryAsync(d => d.ResetAsync(IdOf("log_run"), CancellationToken.None)));
        Assert.DoesNotContain(await TermsAsync(orphans: true), t => t.Column == "log_run");
        Assert.False(await WithDirectoryAsync(d => d.ResetAsync(IdOf("log_run"), CancellationToken.None)));
    }

    [Fact]
    public async Task A_name_belongs_to_one_term_of_an_entity_type_and_a_route_picked_must_be_one_the_term_is_searched_through()
    {
        await SyncAsync();
        await RefineAsync(IdOf("log_run"), new SearchTermRefinementRequest("Run", false, null, null));

        var taken = await Assert.ThrowsAsync<DeliveryException>(() => RefineAsync(IdOf("log_version"), new SearchTermRefinementRequest("run", false, null, null)));
        Assert.Contains("is the search term WellLog.log_run's already", taken.Message, StringComparison.Ordinal);

        var table = await Assert.ThrowsAsync<DeliveryException>(() => RefineAsync(
            IdOf("curve_unit", "curves"), new SearchTermRefinementRequest(null, false, $"{WellLog}|osdu.data.Curves[].CurveUnit|Steps", null)));
        Assert.Contains("cannot be searched through osdu.data.Curves[].CurveUnit", table.Message, StringComparison.Ordinal);

        // The project makes the id only with the log id, which the record does not hold: that route is listed, never searched.
        var key = await Assert.ThrowsAsync<DeliveryException>(() => RefineAsync(
            IdOf("source_project"), new SearchTermRefinementRequest(null, false, $"{WellLog}|id|Key", null)));
        Assert.Contains("does not hold log_id", key.Message, StringComparison.Ordinal);

        // A curve's mnemonic is written as its id and as its mnemonic: either is searched through, as picked.
        Assert.Equal($"{WellLog}|osdu.data.Curves[].CurveID|Copy", Assert.Single(await TermsAsync(), t => t.ColumnLabel == "WellLogCurve.curve_id").Route);
        var picked = await RefineAsync(IdOf("curve_id", "curves"), new SearchTermRefinementRequest(null, false, $"{WellLog}|osdu.data.Curves[].Mnemonic|Copy", null));
        Assert.Equal(($"{WellLog}|osdu.data.Curves[].Mnemonic|Copy", $"{WellLog}|osdu.data.Curves[].Mnemonic|Copy"), (picked.PickedRoute, picked.Route));

        // A refinement that keeps nothing of its own is removed: the column's own name given back is no name.
        var plain = await RefineAsync(IdOf("log_run"), new SearchTermRefinementRequest("WellLog.log_run", false, null, " "));
        Assert.Equal((false, (string?)null), (plain.Renamed, plain.UpdatedBy));

        await Assert.ThrowsAsync<DeliveryException>(() => RefineAsync(IdOf("log_run"), new SearchTermRefinementRequest(new string('n', 101), false, null, null)));
    }

    [Fact]
    public async Task A_condition_on_a_term_resolves_through_its_route_and_one_left_out_is_refused()
    {
        await SyncAsync();
        await RefineAsync(IdOf("wellbore_uwi"), new SearchTermRefinementRequest("Wellbore name", false, null, null));

        var wellbore = await WithDirectoryAsync(d => d.ResolveAsync(IdOf("wellbore_uwi"), new SearchTermCondition(ExplorerCondition.Is, "WB D/4-A-30"), "dev", null, CancellationToken.None));
        Assert.Equal(("data.WellboreID", "Wellbore name", "osdu:wks:master-data--Wellbore:*"), (wellbore.Path, wellbore.Via!.Term, wellbore.Via.Kind));

        var domain = await WithDirectoryAsync(d => d.ResolveAsync(IdOf("index_type"), new SearchTermCondition(ExplorerCondition.Is, "DEPTH"), "test", WellLogVersions.CurrentKind, CancellationToken.None));
        Assert.Equal("data.SamplingDomainTypeID.keyword:\"test:reference-data--WellLogSamplingDomainType:Depth:\"", domain.Clause());

        // A kind of a type the column does not reach is refused, as the type's terms do not offer it.
        var elsewhere = await Assert.ThrowsAsync<DeliveryException>(() => WithDirectoryAsync(d => d.ResolveAsync(
            IdOf("index_type"), new SearchTermCondition(ExplorerCondition.Is, "DEPTH"), "dev", "*:*:master-data--Wellbore:*", CancellationToken.None)));
        Assert.Contains("not of master-data--Wellbore's", elsewhere.Message, StringComparison.Ordinal);

        await RefineAsync(IdOf("index_type"), new SearchTermRefinementRequest(null, true, null, null));
        var excluded = await Assert.ThrowsAsync<DeliveryException>(() => WithDirectoryAsync(d => d.ResolveAsync(IdOf("index_type"), new SearchTermCondition(ExplorerCondition.Is, "DEPTH"), "dev", null, CancellationToken.None)));
        Assert.Contains("is deleted from the search terms", excluded.Message, StringComparison.Ordinal);

        await Assert.ThrowsAsync<DeliveryException>(() => WithDirectoryAsync(d => d.ResolveAsync(Guid.NewGuid(), new SearchTermCondition(ExplorerCondition.Is, "x"), "dev", null, CancellationToken.None)));
    }

    [Fact]
    public async Task Terms_deleted_together_leave_the_search_until_restored_and_one_no_pipeline_reads_is_removed_for_good()
    {
        await SyncAsync();
        await RefineAsync(IdOf("wellbore_uwi"), new SearchTermRefinementRequest("Wellbore name", false, null, "The UWI the well database files the log under."));

        // What is left of a term no pipeline reads any longer: its refinement alone.
        var retired = Guid.NewGuid();
        await using (var db = _module.CreateDbContext())
        {
            db.DeliverySearchTermRefinements.Add(new SqlFlow.Delivery.Data.DeliverySearchTermRefinement
            {
                TermId = retired, TermKey = "osdudata.silver.welllog/retired_column", EntityType = WellLog, Name = "Retired",
                UpdatedBy = "tester", UpdatedUtc = _clock.GetUtcNow().UtcDateTime,
            });
            await db.SaveChangesAsync();
        }

        // One deletion, each id once: the terms a pipeline reads leave the search, the retired one goes, an unknown id is missing.
        var unknown = Guid.NewGuid();
        var deletion = await WithDirectoryAsync(d => d.DeleteAsync([IdOf("wellbore_uwi"), IdOf("log_run"), retired, unknown, IdOf("log_run")], WellLog, "remover", CancellationToken.None));
        Assert.Equal([IdOf("wellbore_uwi"), IdOf("log_run")], deletion.Deleted);
        Assert.Equal([retired], deletion.Removed);
        Assert.Equal([unknown], deletion.Missing);

        // Deleted, a term keeps its name and note through a sync, which extracts it again, and the explorer refuses it.
        await SyncAsync();
        var terms = await TermsAsync(orphans: true);
        var uwi = Assert.Single(terms, t => t.Id == IdOf("wellbore_uwi"));
        Assert.Equal(("Wellbore name", "The UWI the well database files the log under.", true, "remover"), (uwi.Name, uwi.Note, uwi.Excluded, uwi.UpdatedBy));
        var run = Assert.Single(terms, t => t.Id == IdOf("log_run"));
        Assert.Equal((true, false, WellLog), (run.Excluded, run.Renamed, run.EntityType));
        Assert.DoesNotContain(terms, t => t.Id == retired);
        var refused = await Assert.ThrowsAsync<DeliveryException>(() => WithDirectoryAsync(d => d.ResolveAsync(
            IdOf("log_run"), new SearchTermCondition(ExplorerCondition.Is, "1"), "dev", null, CancellationToken.None)));
        Assert.Contains("is deleted from the search terms", refused.Message, StringComparison.Ordinal);

        // Deleting it again changes nothing of who deleted it.
        _clock.Advance(TimeSpan.FromMinutes(1));
        await WithDirectoryAsync(d => d.DeleteAsync([IdOf("log_run")], WellLog, "someone else", CancellationToken.None));
        Assert.Equal("remover", Assert.Single(await TermsAsync(), t => t.Id == IdOf("log_run")).UpdatedBy);

        // Restored, each is offered as it was: the renamed one by its name and note; one with nothing else made of it keeps no
        // refinement. A term offered already is restored as it is; the retired one has nothing to restore.
        var restoration = await WithDirectoryAsync(d => d.RestoreAsync([IdOf("wellbore_uwi"), IdOf("log_run"), IdOf("log_source"), retired], "restorer", CancellationToken.None));
        Assert.Equal([IdOf("wellbore_uwi"), IdOf("log_run"), IdOf("log_source")], restoration.Restored);
        Assert.Equal([retired], restoration.Missing);
        terms = await TermsAsync();
        uwi = Assert.Single(terms, t => t.Id == IdOf("wellbore_uwi"));
        Assert.Equal(("Wellbore name", "The UWI the well database files the log under.", false, "restorer"), (uwi.Name, uwi.Note, uwi.Excluded, uwi.UpdatedBy));
        run = Assert.Single(terms, t => t.Id == IdOf("log_run"));
        Assert.Equal((false, null), (run.Excluded, run.UpdatedBy));
        await using (var db = _module.CreateDbContext())
        {
            Assert.False(await db.DeliverySearchTermRefinements.AnyAsync(r => r.TermId == IdOf("log_run") || r.TermId == IdOf("log_source")));
        }

        // Nothing named, or more than one change takes, is refused.
        await Assert.ThrowsAsync<DeliveryException>(() => WithDirectoryAsync(d => d.DeleteAsync([], WellLog, "remover", CancellationToken.None)));
        var many = Enumerable.Range(0, SearchTermDirectory.MaxBatch + 1).Select(_ => Guid.NewGuid()).ToList();
        var tooMany = await Assert.ThrowsAsync<DeliveryException>(() => WithDirectoryAsync(d => d.RestoreAsync(many, "restorer", CancellationToken.None)));
        Assert.Contains($"at most {SearchTermDirectory.MaxBatch}", tooMany.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_flows_rendering_one_table_under_two_versions_give_one_term_with_the_routes_and_systems_of_both()
    {
        AddNextVersionFlow();
        var (warnings, _) = await SyncAsync();
        Assert.DoesNotContain(warnings, w => w.Contains("search term", StringComparison.OrdinalIgnoreCase));

        await using var db = _module.CreateDbContext();
        var rows = await db.DeliverySearchTerms.AsNoTracking().Where(t => t.RepoId == _repo && t.Column == "wellbore_uwi" && t.EntityType == WellLog).ToListAsync();
        var wellbore = Assert.Single(rows);
        Assert.Contains("welldb-welllog-03-header-delivery\"", wellbore.FlowsJson, StringComparison.Ordinal);
        Assert.Contains("welldb-welllog-03-header-delivery-v150", wellbore.FlowsJson, StringComparison.Ordinal);
        var route = Assert.Single(SearchRoute.FromJson(wellbore.RoutesJson));
        Assert.Equal([WellLogVersions.CurrentMapping, WellLogVersions.NextMapping], route.Mappings);
        Assert.Equal([WellLogVersions.CurrentKind, WellLogVersions.NextKind], route.Kinds);

        // The log's id, made as each version's delivery makes it.
        var id = Assert.Single(SearchRoute.FromJson(Assert.Single(await db.DeliverySearchTerms.AsNoTracking().Where(t => t.RepoId == _repo && t.Column == "log_id").ToListAsync()).RoutesJson));
        Assert.Equal(["welldb", "welldb-welllog-1.5.0"], id.Key!.Systems);
        Assert.Equal(1, await db.DeliverySearchTerms.CountAsync(t => t.RepoId == _repo && t.Column == "curve_unit"));
    }

    [Fact]
    public async Task What_people_made_of_the_terms_keyed_by_their_source_system_moves_to_the_term_its_column_is_now()
    {
        // Two refinements of the time terms were keyed by the mapping's source system: one per version's system, the newer kept.
        await using (var db = _module.CreateDbContext())
        {
            db.DeliverySearchTermRefinements.Add(new SqlFlow.Delivery.Data.DeliverySearchTermRefinement
            {
                TermId = Guid.NewGuid(), TermKey = $"welldb/{WellLog}//wellbore_uwi", EntityType = WellLog, Name = "Old UWI",
                Route = "osdu.data.WellboreID|Search", UpdatedBy = "tester", UpdatedUtc = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            db.DeliverySearchTermRefinements.Add(new SqlFlow.Delivery.Data.DeliverySearchTermRefinement
            {
                TermId = Guid.NewGuid(), TermKey = $"welldb-welllog-1.5.0/{WellLog}//wellbore_uwi", EntityType = WellLog, Name = "UWI",
                Excluded = true, UpdatedBy = "tester", UpdatedUtc = new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc),
            });
            db.DeliverySearchTermRefinements.Add(new SqlFlow.Delivery.Data.DeliverySearchTermRefinement
            {
                TermId = Guid.NewGuid(), TermKey = $"welldb/{WellLog}/curves/curve_unit", EntityType = WellLog, Name = "Curve unit",
                UpdatedBy = "tester", UpdatedUtc = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            await db.SaveChangesAsync();
        }

        AddNextVersionFlow();
        await SyncAsync();

        var terms = await TermsAsync(orphans: true);
        var wellbore = Assert.Single(terms, t => t.Id == IdOf("wellbore_uwi"));
        Assert.Equal(("UWI", true, false), (wellbore.Name, wellbore.Excluded, wellbore.Orphan));
        Assert.Equal("Curve unit", Assert.Single(terms, t => t.Id == IdOf("curve_unit", "curves")).Name);

        // The older of the two made for one term stays, listed as no longer found, to be removed.
        var left = Assert.Single(terms, t => t.Orphan);
        Assert.Equal(("Old UWI", "wellbore_uwi"), (left.Name, left.Column));

        // A sync again moves nothing more.
        await SyncAsync();
        Assert.Single(await TermsAsync(orphans: true), t => t.Orphan);
    }

    [Fact]
    public async Task The_terms_are_written_again_from_the_module_rows_alone_as_the_control_plane_does_when_it_starts()
    {
        await SyncAsync();
        await using (var db = _module.CreateDbContext())
        {
            await db.DeliverySearchTerms.Where(t => t.RepoId == _repo).ExecuteDeleteAsync();
        }

        // The delivery flows as the catalog keeps copies of them: every flow document of the repository, parsed.
        var loader = new DeliveryDocumentLoader();
        var sources = Directory.EnumerateFiles(_root, "*.yaml", SearchOption.AllDirectories)
            .Select(file => (File: file, Yaml: File.ReadAllText(file)))
            .Where(f => f.Yaml.Contains($"flowType: {SqlFlow.Delivery.Model.FlowDefinition.FlowTypeName}", StringComparison.Ordinal))
            .Select(f => new RepositorySource(Path.GetRelativePath(_root, f.File), loader.ParseSource(f.Yaml, Path.GetRelativePath(_root, f.File))))
            .ToList();
        var warnings = new List<string>();
        await using var context = _module.CreateDbContext();
        var counts = await DeliverySearchTermCatalog.ReconcileAsync(context, _repo, sources, loader, _clock.GetUtcNow().UtcDateTime, warnings, CancellationToken.None);

        Assert.True(counts.Added > 10);
        Assert.Empty(warnings);
    }

    private static void CopyDirectory(string from, string to)
    {
        foreach (var directory in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(directory.Replace(from, to, StringComparison.Ordinal));
        }

        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, file.Replace(from, to, StringComparison.Ordinal), overwrite: true);
        }
    }
}
