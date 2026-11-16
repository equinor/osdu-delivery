using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Identity;
using SqlFlow.Core.Lineage;
using SqlFlow.Lineage.Collection;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// A column traced back toward its source, end to end: an estate on disk is synced into the real catalog, then asked
/// about through the API. The estate is a landing flow (which renames one column and computes another), an ingestion
/// flow reading its typed view, and two lineage descriptions (<c>flowType: lin</c>) of the steps another system runs
/// before the landed file. The test holds the trace to the rule it exists for: every step says what it rests on (a
/// description's mapping, a flow's declaration, or only the column's name), and wherever the lineage does not say
/// where a value comes from, the answer says that it is missing and never fills it in. Without a description, the
/// trace ends at the landed file and says so.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ColumnTraceApiTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_coltrace_" + Guid.NewGuid().ToString("N"));
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..8];

    public ColumnTraceApiTests() => Directory.CreateDirectory(_dir);

    private string Land => "ct_land_" + _suffix;

    private string Ing => "ct_ing_" + _suffix;

    private string Curate => "ct_u1_curate_" + _suffix;

    private string Publish => "ct_u2_publish_" + _suffix;

    private string CuratedKey => Described("lakehouse", "curated.ct_orders_" + _suffix);

    private string OrdersKey => Described("erp", "sales.ct_orders_" + _suffix);

    private string RatesKey => Described("erp", "sales.ct_rates_" + _suffix);

    private static string Described(string system, string name)
        => NodeKey.For(LineageDescribedObject.ServerRef(system), null, null, name);

    private void Write(string relativePath, string yaml)
    {
        var path = Path.Combine(_dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, yaml.Replace("__S__", _suffix, StringComparison.Ordinal));
    }

    private void WriteFlows()
    {
        Write("flows/10-land.flow.yaml", """
            name: ct_land___S__
            source:
              type: csv
              location: data/ct_orders___S__
            target:
              connection: ${env:SQLFLOW_CT_DB}
              schema: dbo
              table: CtOrdersPre___S__
            transform:
              inferTypes: true
              columns:
                - name: order_no
                  as: order_id
                  type: int
                - name: loaded_by
                  virtual: true
                  expr: "SUSER_SNAME()"
                  type: varchar(128)
            """);
        Write("flows/20-ing.flow.yaml", """
            flowType: ing
            name: ct_ing___S__
            connections:
              src: ${env:SQLFLOW_CT_DB}
              dwh: ${env:SQLFLOW_CT_DB}
            source:
              server: src
              object: CtTraceDb.dbo.v_CtOrdersPre___S__
            target:
              server: dwh
              object: CtTraceDb.dbo.CtOrders___S__
            load:
              keyColumns: [order_id]
            """);
    }

    private void WriteDescriptions()
    {
        Write("lineage/u1-curate.yaml", """
            flowType: lin
            name: ct_u1_curate___S__
            description: Builds the curated orders table.
            system: lakehouse
            unit: job orders-daily, task curate
            reads:
              - { system: erp, name: sales.ct_orders___S__, as: src, origin: true }
              - { system: erp, name: sales.ct_rates___S__, as: fx }
            writes:
              - name: curated.ct_orders___S__
                columns:
                  order_no: src.order_no
                  amount_eur:
                    from: [src.amount, fx.rate]
                    note: amount * rate
            """);
        Write("lineage/u2-publish.yaml", """
            flowType: lin
            name: ct_u2_publish___S__
            system: lakehouse
            unit: job orders-daily, task publish
            reads:
              - { name: curated.ct_orders___S__, as: orders }
            writes:
              - feeds: ct_land___S__
                columns:
                  order_no: orders.order_no
                  amount: { from: orders.amount_eur, note: Rounded to two decimals. }
                  exported_at:
            """);
    }

    [SkippableFact]
    public async Task AColumn_IsFollowedToItsSource_AndWhatTheLineageDoesNotSayIsReportedAsMissing()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var repo = "ct_trace_" + _suffix;
        var repoId = FlowIdentity.FromName(repo);
        var seededColumns = new List<string>();

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
        try
        {
            using var client = factory.CreateClient();
            var token = await IssueTokenAsync(client, ["read"]);

            // ---- The flows alone: no lineage description says where the landed file comes from.
            WriteFlows();
            await SyncAsync(cs, repo);
            string tableKey;
            string viewKey;
            string fileKey;
            await using (var db = CatalogDatabase.Create(cs))
            {
                tableKey = (await db.LineageEdges.AsNoTracking().SingleAsync(
                    e => e.RepoId == repoId && e.Flow == Ing && e.Relation == nameof(LineageRelation.Writes))).ObjectKey;
                viewKey = (await db.LineageEdges.AsNoTracking().SingleAsync(
                    e => e.RepoId == repoId && e.Flow == Ing && e.Relation == nameof(LineageRelation.Reads) && e.ViaModule == null)).ObjectKey;
                fileKey = (await db.LineageEdges.AsNoTracking().SingleAsync(
                    e => e.RepoId == repoId && e.Flow == Land && e.Relation == nameof(LineageRelation.Reads))).ObjectKey;
            }

            var alone = await TraceAsync(client, token, tableKey, "amount");
            Assert.Equal(ColumnTrace.OutcomeMissing, alone.Outcome);
            Assert.False(alone.DescribedLineageUsed);
            Assert.False(alone.Truncated);
            Assert.Null(alone.ColumnKnown);
            var first = alone.Hops[0];
            Assert.Equal((1, ColumnTrace.BasisName, Ing, tableKey, "amount", viewKey, "amount"),
                (first.Depth, first.Basis, first.Flow, first.ObjectKey, first.Column, first.SourceObjectKey, first.SourceColumn));
            Assert.Null(first.SourceColumnKnown);
            Assert.Contains(alone.Hops, h => h.Flow == Land && h.Basis == ColumnTrace.BasisName && h.SourceObjectKey == fileKey && h.SourceColumn == "amount");
            var end = Assert.Single(alone.Ends);
            Assert.Equal((ColumnTrace.EndNotDescribed, fileKey, "amount"), (end.Kind, end.ObjectKey, end.Column));
            Assert.Contains("no lineage description", Assert.Single(alone.Missing), StringComparison.Ordinal);

            // What the landing flow declares for a column is stated, with the column it reads under another name.
            var renamed = await TraceAsync(client, token, tableKey, "order_id");
            var declared = Assert.Single(renamed.Hops, h => h.Flow == Land);
            Assert.Equal(ColumnTrace.BasisDeclared, declared.Basis);
            Assert.Equal(("order_id", "order_no", "int"), (declared.Column, declared.SourceColumn, declared.DataType));
            Assert.Contains("[order_no]", declared.Expression, StringComparison.Ordinal);
            Assert.Equal((ColumnTrace.EndNotDescribed, fileKey, "order_no"),
                (Assert.Single(renamed.Ends).Kind, renamed.Ends[0].ObjectKey, renamed.Ends[0].Column));

            // A column the flow computes starts in the flow: that is a source, and nothing is missing.
            var computed = await TraceAsync(client, token, tableKey, "loaded_by");
            Assert.Equal(ColumnTrace.OutcomeComplete, computed.Outcome);
            var made = Assert.Single(computed.Ends);
            Assert.Equal((ColumnTrace.EndMade, Land), (made.Kind, made.Flow));
            Assert.Contains("SUSER_SNAME()", made.Detail, StringComparison.Ordinal);
            Assert.Empty(computed.Missing);

            // ---- With the descriptions synced, the same question goes back to where the data starts.
            WriteDescriptions();
            var synced = await SyncAsync(cs, repo);
            Assert.DoesNotContain(synced.Warnings, w => w.StartsWith("described lineage:", StringComparison.Ordinal));

            var traced = await TraceAsync(client, token, tableKey, "order_id");
            Assert.Equal(ColumnTrace.OutcomeComplete, traced.Outcome);
            Assert.True(traced.DescribedLineageUsed);
            Assert.Empty(traced.Missing);
            var origin = Assert.Single(traced.Ends);
            Assert.Equal((ColumnTrace.EndOrigin, OrdersKey, "order_no", "erp"), (origin.Kind, origin.ObjectKey, origin.Column, origin.System));
            Assert.Equal(
                [Ing, Land, Publish, Curate],
                traced.Hops.OrderBy(h => h.Depth).Select(h => h.Flow));
            var publish = Assert.Single(traced.Hops, h => h.Flow == Publish);
            Assert.Equal(
                (ColumnTrace.BasisDescribed, "lakehouse", "job orders-daily, task publish", fileKey, "order_no", CuratedKey, "order_no"),
                (publish.Basis, publish.System, publish.Unit, publish.ObjectKey, publish.Column, publish.SourceObjectKey, publish.SourceColumn));

            // A column made from two, one of them from an object nothing describes: the known part is given, and the
            // other is reported as missing by name.
            var amount = await TraceAsync(client, token, tableKey, "amount");
            Assert.Equal(ColumnTrace.OutcomePartial, amount.Outcome);
            Assert.Equal("Rounded to two decimals.", Assert.Single(amount.Hops, h => h.Flow == Publish).Note);
            Assert.Equal(2, amount.Hops.Count(h => h.Flow == Curate && h.Note == "amount * rate"));
            Assert.Contains(amount.Ends, e => e.Kind == ColumnTrace.EndOrigin && e.ObjectKey == OrdersKey && e.Column == "amount");
            var open = Assert.Single(amount.Ends, e => e.Kind == ColumnTrace.EndNotDescribed);
            Assert.Equal((RatesKey, "rate"), (open.ObjectKey, open.Column));
            Assert.Contains("sales.ct_rates_" + _suffix, Assert.Single(amount.Missing), StringComparison.Ordinal);

            // A column the description of the file does not map: the step is described, the column is not.
            var unmapped = await TraceAsync(client, token, tableKey, "status");
            Assert.Equal(ColumnTrace.OutcomeMissing, unmapped.Outcome);
            var notMapped = Assert.Single(unmapped.Ends);
            Assert.Equal((ColumnTrace.EndNotMapped, fileKey, Publish), (notMapped.Kind, notMapped.ObjectKey, notMapped.Flow));
            Assert.Contains("does not cover column 'status'", notMapped.Detail, StringComparison.Ordinal);
            Assert.Contains("curated.ct_orders_" + _suffix, notMapped.Detail, StringComparison.Ordinal);

            // A column a described step makes itself, asked from the object the description writes.
            var exported = await TraceAsync(client, token, fileKey, "exported_at");
            Assert.Equal(ColumnTrace.OutcomeComplete, exported.Outcome);
            Assert.Equal((ColumnTrace.EndMade, Publish), (Assert.Single(exported.Ends).Kind, exported.Ends[0].Flow));

            // A trace may start from an object only descriptions name.
            var described = await TraceAsync(client, token, CuratedKey, "order_no");
            Assert.Equal(DescribedLineage.DescribedKind, described.ObjectKind);
            Assert.Equal(ColumnTrace.EndOrigin, Assert.Single(described.Ends).Kind);

            // ---- The catalog's column lists narrow a step by name: a column an object is known not to hold is not
            // followed into it, and the answer says the flow derives it.
            await using (var db = CatalogDatabase.Create(cs))
            {
                // A list read from the database replaces whatever the flows declared of the two objects.
                await db.ObjectColumns.Where(c => c.ObjectKey == viewKey || c.ObjectKey == tableKey).ExecuteDeleteAsync();
                db.ObjectColumns.AddRange(
                    Column(viewKey, 1, "order_id"), Column(viewKey, 2, "amount"), Column(viewKey, 3, "loaded_by"),
                    Column(tableKey, 1, "order_id"), Column(tableKey, 2, "amount"));
                await db.SaveChangesAsync();
                seededColumns.AddRange([viewKey, tableKey]);
            }

            var known = await TraceAsync(client, token, tableKey, "amount");
            Assert.True(known.ColumnKnown);
            Assert.True(Assert.Single(known.Hops, h => h.Flow == Ing).SourceColumnKnown);

            var ghost = await TraceAsync(client, token, tableKey, "ghost");
            Assert.False(ghost.ColumnKnown);
            Assert.Equal(ColumnTrace.OutcomeMissing, ghost.Outcome);
            Assert.Empty(ghost.Hops);
            Assert.Equal((ColumnTrace.EndDerived, Ing), (Assert.Single(ghost.Ends).Kind, ghost.Ends[0].Flow));
            Assert.Contains(ghost.Missing, m => m.Contains("holds no column named 'ghost'", StringComparison.Ordinal));

            // ---- A depth limit ends the trace with what it reached, and says it was cut.
            var shallow = await TraceAsync(client, token, tableKey, "order_id", depth: 2);
            Assert.True(shallow.Truncated);
            Assert.Equal(2, shallow.Depth);
            Assert.Equal((ColumnTrace.EndLimit, fileKey), (Assert.Single(shallow.Ends).Kind, shallow.Ends[0].ObjectKey));
            Assert.Equal(ColumnTrace.OutcomeMissing, shallow.Outcome);

            // ---- A request that names no column, many columns, or an object the catalog does not hold is refused.
            Assert.Equal(HttpStatusCode.BadRequest, await StatusAsync(client, token, $"/api/v1/lineage/objects/column-trace?key={Uri.EscapeDataString(tableKey)}"));
            Assert.Equal(HttpStatusCode.BadRequest, await StatusAsync(client, token, $"/api/v1/lineage/objects/column-trace?key={Uri.EscapeDataString(tableKey)}&column=*"));
            Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, token, "/api/v1/lineage/objects/column-trace?key=no-such-object&column=a"));
            using var anonymous = await client.GetAsync(new Uri($"/api/v1/lineage/objects/column-trace?key={Uri.EscapeDataString(tableKey)}&column=amount", UriKind.Relative));
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            var objectKeys = await db.LineageEdges.Where(e => e.RepoId == repoId).Select(e => e.ObjectKey).Distinct().ToListAsync();
            await db.ObjectColumns.Where(c => seededColumns.Contains(c.ObjectKey)).ExecuteDeleteAsync();
            await db.PipelineColumns.Where(c => c.RepoId == repoId).ExecuteDeleteAsync();
            await db.LineageColumnMappings.Where(m => m.RepoId == repoId).ExecuteDeleteAsync();
            await db.FlowDependencies.Where(d => d.RepoId == repoId).ExecuteDeleteAsync();
            await db.LineageEdges.Where(e => e.RepoId == repoId).ExecuteDeleteAsync();
            await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
            await db.Schedules.Where(s => s.RepoId == repoId).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await db.Objects.Where(o => objectKeys.Contains(o.Key)).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        }
    }

    private static CatalogObjectColumn Column(string objectKey, int ordinal, string name)
        => new() { ObjectKey = objectKey, Ordinal = ordinal, Name = name, DataType = "varchar(50)", Nullable = true, Tier = "Derived" };

    private async Task<CatalogSyncResult> SyncAsync(string cs, string repo)
    {
        await using var db = CatalogDatabase.Create(cs);
        return await new CatalogSync().SyncAsync(db, _dir, repo, null, DateTime.UtcNow);
    }

    private static Task<ColumnTraceDto> TraceAsync(HttpClient client, string token, string key, string column, int? depth = null)
        => GetJsonAsync<ColumnTraceDto>(
            client, token,
            $"/api/v1/lineage/objects/column-trace?key={Uri.EscapeDataString(key)}&column={Uri.EscapeDataString(column)}"
            + (depth is { } levels ? $"&depth={levels}" : string.Empty));

    private static async Task<string> IssueTokenAsync(HttpClient client, IReadOnlyList<string> scopes)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, scopes));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token.AccessToken;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string token, string relativeUri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(relativeUri, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<HttpStatusCode> StatusAsync(HttpClient client, string token, string relativeUri)
    {
        using var response = await SendAsync(client, token, relativeUri);
        return response.StatusCode;
    }

    private static async Task<T> GetJsonAsync<T>(HttpClient client, string token, string relativeUri)
    {
        using var response = await SendAsync(client, token, relativeUri);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"GET {relativeUri} answered {(int)response.StatusCode}: {text}");
        var value = JsonSerializer.Deserialize<T>(text, Web);
        Assert.NotNull(value);
        return value;
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
