using SqlFlow.Core;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Model;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The removal flow document (<c>flowType: removal</c>, osdu/docs/reference/flow/removal.md): what it declares, every rule
/// the loader holds it to with each refusal naming the place in the document it is about, how a run binds it to a partition
/// and selects its removals, the names a removal takes, the payload and operations the kind takes, and what it tells the
/// platform about itself.
/// </summary>
public sealed class RemovalDocumentTests
{
    private const string Head = """
        flowType: removal
        name: welldb-wellbore-05-removal
        batch: welldb
        partitions: [dev, test]

        """;

    private const string Keys = """
        removals:
          - flow: welldb-wellbore-03-delivery
            keys:
              type: csv
              location: ../data/wellbore
              options: { delimiter: ",", header: true }

        """;

    private static RemovalFlowDefinition Parse(string yaml) => new DeliveryDocumentLoader().ParseRemoval(yaml, "flows/removal.yaml");

    private static string Refused(string yaml) => Assert.Throws<FlowValidationException>(() => Parse(yaml)).Message;

    /// <summary>A document of one removal whose <c>keys</c> source is <paramref name="keys"/>, indented under it.</summary>
    private static string WithKeys(string keys)
        => Head + "removals:\n  - flow: welldb-wellbore-03-delivery\n    keys:\n" + string.Concat(keys.Split('\n').Where(l => l.Length > 0).Select(l => "      " + l + "\n"));

    /// <summary>A document of one removal whose <c>removed</c> source is <paramref name="removed"/>, indented under it.</summary>
    private static string WithRemoved(string removed)
        => Head + "removals:\n  - flow: welldb-wellbore-03-delivery\n    removed:\n" + string.Concat(removed.Split('\n').Where(l => l.Length > 0).Select(l => "      " + l + "\n"));

    [Fact]
    public void A_document_declares_its_guard_and_removals_each_reading_the_source_or_its_removed_keys_or_both()
    {
        var flow = Parse(Head + """
            description: The wellbores the well database no longer holds.
            parameters:
              source: { default: WELLDB }
            guard: { maxPercent: 5, maxRecords: 2000 }
            removals:
              - flow: welldb-wellbore-03-delivery
                keys:
                  type: csv
                  location: ../data/{source}/wellbore
                  options: { delimiter: ";", srcFile: "*.csv" }
                  columns:
                    - { name: WellboreName, as: wellbore_name, expr: "UPPER(@ColName)" }
              - name: logs
                flow: welldb-welllog-03-delivery
                interface: curves
                values: { logSource: "{source}" }
                keepReferenced: false
                removed:
                  connection: ${env:WELLDB_SOURCE_DB}
                  provider: postgres
                  object: welldb.public.deleted_curves
                  filter: AND kind = 'curve'
                  columns: [{ name: curve_key, as: UWI }]
                  removedAt: { name: deleted_at, expr: "DATEADD(hour, -1, @ColName)" }
            """);

        Assert.Equal(("welldb-wellbore-05-removal", "welldb", "The wellbores the well database no longer holds."), (flow.Name, flow.Batch, flow.Description));
        Assert.Equal(["dev", "test"], flow.Partitions);
        Assert.Equal((5, 2000L), (flow.Guard.MaxPercent, flow.Guard.MaxRecords!.Value));
        Assert.Equal("WELLDB", flow.Parameters["source"].Default);

        var wellbores = flow.Entries[0];
        Assert.Equal(("welldb-wellbore-03-delivery", "welldb-wellbore-03-delivery", null, true), (wellbores.Name, wellbores.Flow, wellbores.Interface, wellbores.KeepReferenced));
        var reader = wellbores.Keys!.Reader!;
        Assert.Equal(("csv", "../data/{source}/wellbore", ";"), (reader.Type, reader.Location, reader.Options["DELIMITER"]));
        var column = Assert.Single(wellbores.Keys.Columns);
        Assert.Equal(("WellboreName", "wellbore_name", "UPPER(@ColName)", "wellbore_name"), (column.Name, column.As, column.Expr, column.Output));
        Assert.Null(wellbores.Keys.RemovedAt);
        Assert.Null(wellbores.Removed);
        Assert.Equal([("keys", wellbores.Keys)], wellbores.Sources());

        var logs = flow.Entries[1];
        Assert.Equal(("logs", "welldb-welllog-03-delivery", "curves", false), (logs.Name, logs.Flow, logs.Interface, logs.KeepReferenced));
        Assert.Equal("{source}", logs.Values["logSource"]);
        var table = logs.Removed!.Table!;
        Assert.Equal(("${env:WELLDB_SOURCE_DB}", DataSourceKind.PostgreSQL, "welldb.public.deleted_curves", "AND kind = 'curve'"), (table.Connection, table.Provider, table.ObjectName, table.Filter));
        Assert.Equal(("deleted_at", "DATEADD(hour, -1, @ColName)"), (logs.Removed.RemovedAt!.Name, logs.Removed.RemovedAt.Expr));
        Assert.Null(logs.Keys);
        Assert.Equal(["removals[1].removed.connection"], flow.CredentialReferences().Select(r => r.Key));
    }

    [Fact]
    public void The_guard_defaults_to_a_fifth_with_no_count_and_holds_its_bounds_to_their_ranges()
    {
        var flow = Parse(Head + Keys);
        Assert.Equal((RemovalGuard.DefaultMaxPercent, (long?)null), (flow.Guard.MaxPercent, flow.Guard.MaxRecords));
        Assert.Equal(20, RemovalGuard.DefaultMaxPercent);
        Assert.Equal(0, Parse(Head + "guard: { maxPercent: 0 }\n" + Keys).Guard.MaxPercent);

        Assert.Contains("guard.maxPercent is 101", Refused(Head + "guard: { maxPercent: 101 }\n" + Keys), StringComparison.Ordinal);
        Assert.Contains("guard.maxPercent is -1", Refused(Head + "guard: { maxPercent: -1 }\n" + Keys), StringComparison.Ordinal);
        Assert.Contains("guard.maxRecords is -5", Refused(Head + "guard: { maxRecords: -5 }\n" + Keys), StringComparison.Ordinal);
    }

    [Fact]
    public void A_removal_is_named_after_its_delivery_flow_and_interface_unless_it_names_itself_and_each_name_is_used_once()
    {
        var flow = Parse(Head + """
            removals:
              - { flow: a-delivery, keys: { type: csv, location: a } }
              - { flow: b-delivery, interface: logs, keys: { type: csv, location: b } }
              - { name: "  spaced  ", flow: c-delivery, keys: { type: csv, location: c } }
            """);
        Assert.Equal(["a-delivery", "b-delivery/logs", "spaced"], flow.Entries.Select(e => e.Name));
        Assert.Equal("b-delivery/logs", flow.Entry("B-DELIVERY/LOGS")!.Name);
        Assert.Null(flow.Entry("nope"));

        Assert.Contains("which another removal of the flow is named already (ignoring case)", Refused(Head + """
            removals:
              - { name: Wellbores, flow: a, keys: { type: csv, location: a } }
              - { name: wellbores, flow: b, keys: { type: csv, location: b } }
            """), StringComparison.Ordinal);
        Assert.Contains("removals[0] is named", Refused(Head + $"removals:\n  - {{ name: {new string('n', 301)}, flow: a, keys: {{ type: csv, location: a }} }}\n"), StringComparison.Ordinal);
        Assert.Contains("is not an interface name", Refused(Head + "removals:\n  - { flow: a, interface: \"1logs\", keys: { type: csv, location: a } }\n"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_removals_name_may_carry_its_interface_after_a_slash_and_a_run_names_it_so()
    {
        Assert.True(RemovalNames.IsName("welldb-welllog-03-delivery/curves"));
        Assert.True(RemovalNames.IsName("Wellbores (nightly)"));
        Assert.True(RemovalNames.IsName(new string('n', RemovalNames.MaxLength)));
        Assert.False(RemovalNames.IsName(new string('n', RemovalNames.MaxLength + 1)));
        Assert.False(RemovalNames.IsName(" padded"));
        Assert.False(RemovalNames.IsName("a\tb"));
        Assert.False(RemovalNames.IsName(string.Empty));
        Assert.False(RemovalNames.IsName(null));

        var payload = DeliveryRunPayload.Parse("""{ "entries": ["welldb-welllog-03-delivery/curves", "Wellbores (nightly)"] }""");
        Assert.Equal(["welldb-welllog-03-delivery/curves", "Wellbores (nightly)"], payload.Entries);
        Assert.Equal("""{"entries":["welldb-welllog-03-delivery/curves","Wellbores (nightly)"]}""", payload.ToJson());
        Assert.Contains("names 'A' more than once", Assert.Throws<SqlFlowException>(() => DeliveryRunPayload.Parse("""{ "entries": ["a", "A"] }""")).Message, StringComparison.Ordinal);
        Assert.Contains("which is not a removal's name", Assert.Throws<SqlFlowException>(() => DeliveryRunPayload.Parse("""{ "entries": ["a\tb"] }""")).Message, StringComparison.Ordinal);
        Assert.Contains("at most 50 removal names", Assert.Throws<SqlFlowException>(() => DeliveryRunPayload.Parse(
            "{ \"entries\": [" + string.Join(",", Enumerable.Range(0, 51).Select(i => $"\"r{i}\"")) + "] }")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_flow_names_at_least_one_and_at_most_fifty_removals_each_reading_at_least_one_source()
    {
        Assert.Contains("removals names no removal", Refused(Head), StringComparison.Ordinal);
        Assert.Contains("removals names no removal", Refused(Head + "removals: []\n"), StringComparison.Ordinal);
        var many = Head + "removals:\n" + string.Concat(Enumerable.Range(0, 51).Select(i => $"  - {{ flow: f{i}, keys: {{ type: csv, location: d{i} }} }}\n"));
        Assert.Contains("names 51 removals; a flow names at most 50", Refused(many), StringComparison.Ordinal);
        Assert.Contains("removals[0] declares neither keys", Refused(Head + "removals:\n  - { flow: a }\n"), StringComparison.Ordinal);
        Assert.Contains("removals[0].flow", Refused(Head + "removals:\n  - { keys: { type: csv, location: a } }\n"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_key_source_is_a_reader_or_a_table_never_both_and_never_neither()
    {
        Assert.Contains("names both a source reader", Refused(WithKeys("type: csv\nlocation: a\nobject: WellDB.dbo.T\nconnection: ${env:DB}\n")), StringComparison.Ordinal);
        Assert.Contains("names no source", Refused(WithKeys("columns: [{ name: a }]\n")), StringComparison.Ordinal);
        Assert.Contains("removals[0].keys.type", Refused(WithKeys("location: a\n")), StringComparison.Ordinal);
        Assert.Contains("names no location (or options.srcPath)", Refused(WithKeys("type: csv\noptions: { header: true }\n")), StringComparison.Ordinal);
        Assert.Equal("parquet", Parse(WithKeys("type: PARQUET\noptions: { srcPath: ../lake/wellbores }\n")).Entries[0].Keys!.Reader!.Type);
        Assert.Contains("removals[0].keys.object", Refused(WithKeys("connection: ${env:DB}\n")), StringComparison.Ordinal);
        Assert.Contains("removals[0].keys.connection", Refused(WithKeys("object: WellDB.dbo.Wellbore\n")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("copyToPath")]
    [InlineData("zipToPath")]
    [InlineData("srcDeleteIngested")]
    [InlineData("srcDeleteAtPath")]
    public void Reading_keys_never_moves_or_deletes_a_sources_files(string option)
    {
        Assert.Contains($"options.{option} is set; reading keys never copies, archives or deletes", Refused(WithKeys($"type: csv\nlocation: a\noptions: {{ {option}: x }}\n")), StringComparison.Ordinal);
        Assert.Contains($"options.{option} is set; reading keys never copies", Refused(WithRemoved($"type: csv\nlocation: a\noptions: {{ {option}: x }}\n")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("initFromFileDate")]
    [InlineData("initToFileDate")]
    [InlineData("incrementalAfterDate")]
    public void A_date_window_reads_part_of_the_key_set_so_keys_refuse_it_and_a_list_of_removed_keys_takes_it(string option)
    {
        Assert.Contains("every key outside the window would look removed", Refused(WithKeys($"type: csv\nlocation: a\noptions: {{ {option}: \"2026-01-01\" }}\n")), StringComparison.Ordinal);
        Assert.Equal("2026-01-01", Parse(WithRemoved($"type: csv\nlocation: a\noptions: {{ {option}: \"2026-01-01\" }}\n")).Entries[0].Removed!.Reader!.Options[option]);
        Assert.Contains("is the engine's to set", Refused(WithKeys("type: csv\nlocation: a\noptions: { settledFilesOnly: true }\n")), StringComparison.Ordinal);
    }

    [Fact]
    public void A_removal_moment_belongs_to_removed_keys_a_table_source_names_it_and_a_reader_source_defaults_to_the_files_date()
    {
        Assert.Contains("only a source of removed keys (removed) says when a key was removed", Refused(WithKeys("type: csv\nlocation: a\nremovedAt: { name: deleted_at }\n")), StringComparison.Ordinal);
        Assert.Contains("removedAt is required for a table source of removed keys", Refused(WithRemoved("connection: ${env:DB}\nobject: WellDB.dbo.Deleted\n")), StringComparison.Ordinal);
        Assert.Contains("removedAt.as is set; the removal moment fills no key column", Refused(WithRemoved("connection: ${env:DB}\nobject: WellDB.dbo.Deleted\nremovedAt: { name: deleted_at, as: x }\n")), StringComparison.Ordinal);
        Assert.Null(Parse(WithRemoved("type: csv\nlocation: removed\n")).Entries[0].Removed!.RemovedAt);
        Assert.Equal("FileDate_DW", RemovalMapper.FileDateColumn);
    }

    [Theory]
    [InlineData("UPPER(@ColName); DROP TABLE x")]
    [InlineData("@ColName -- comment")]
    [InlineData("@ColName /* comment */")]
    [InlineData("(SELECT TOP 1 name FROM sys.tables)")]
    [InlineData("@ColName + (select 1)")]
    [InlineData("EXEC('x')")]
    [InlineData("OPENROWSET('a', 'b', 'c')")]
    [InlineData("waitfor delay '00:00:05'")]
    public void A_key_columns_expression_is_one_expression_over_its_value_never_a_statement_query_or_comment(string expr)
        => Assert.Contains("it is one SQL expression over the column's value (@ColName)", Refused(WithKeys($"type: csv\nlocation: a\ncolumns: [{{ name: Name, expr: \"{expr}\" }}]\n")), StringComparison.Ordinal);

    [Fact]
    public void Each_key_column_is_read_once_and_an_empty_list_is_refused()
    {
        Assert.Contains("which another column of the list fills already", Refused(WithKeys("type: csv\nlocation: a\ncolumns: [{ name: A, as: uwi }, { name: B, as: UWI }]\n")), StringComparison.Ordinal);
        Assert.Contains("columns is empty", Refused(WithKeys("type: csv\nlocation: a\ncolumns: []\n")), StringComparison.Ordinal);
        Assert.Contains("columns[0].name", Refused(WithKeys("type: csv\nlocation: a\ncolumns: [{ as: uwi }]\n")), StringComparison.Ordinal);
        Assert.Equal("RIGHT('0000' + @ColName, 4)", Parse(WithKeys("type: csv\nlocation: a\ncolumns: [{ name: Number, expr: \"RIGHT('0000' + @ColName, 4)\" }]\n")).Entries[0].Keys!.Columns[0].Expr);
        Assert.Equal("LOWER(@colname)", Parse(WithKeys("type: csv\nlocation: a\ncolumns: [{ name: Number, expr: \"LOWER(@colname)\" }]\n")).Entries[0].Keys!.Columns[0].Expr);

        // The value is @ColName whatever the column is called: a column's own name, or any other variable, is refused at load.
        Assert.Contains("uses '@Number'; an expression names the column's value @ColName", Refused(WithKeys("type: csv\nlocation: a\ncolumns: [{ name: Number, expr: \"UPPER(@Number)\" }]\n")), StringComparison.Ordinal);
        Assert.Contains("uses '@@SPID'", Refused(WithKeys("type: csv\nlocation: a\ncolumns: [{ name: Number, expr: \"@ColName + @@SPID\" }]\n")), StringComparison.Ordinal);
        Assert.Contains("removedAt.expr uses '@deleted'", Refused(WithRemoved("type: csv\nlocation: a\nremovedAt: { name: deleted, expr: \"@deleted\" }\n")), StringComparison.Ordinal);
    }

    [Fact]
    public void A_table_sources_filter_carries_its_own_and_and_its_provider_and_connection_are_checked()
    {
        Assert.Contains("carries its own leading AND", Refused(WithKeys("connection: ${env:DB}\nobject: WellDB.dbo.Wellbore\nfilter: status = 'active'\n")), StringComparison.Ordinal);
        Assert.Equal("and status = 'active'", Parse(WithKeys("connection: ${env:DB}\nobject: WellDB.dbo.Wellbore\nfilter: and status = 'active'\n")).Entries[0].Keys!.Table!.Filter);
        Assert.Contains("unknown provider 'db2'", Refused(WithKeys("connection: ${env:DB}\nprovider: db2\nobject: WellDB.dbo.Wellbore\n")), StringComparison.Ordinal);
        Assert.Equal(DataSourceKind.MSSQL, Parse(WithKeys("connection: ${env:DB}\nprovider: sqlserver\nobject: WellDB.dbo.Wellbore\n")).Entries[0].Keys!.Table!.Provider);
        Assert.Equal(DataSourceKind.AZDB, Parse(WithKeys("connection: ${keyvault:kv/welldb}\nprovider: AZDB\nobject: WellDB.dbo.Wellbore\n")).Entries[0].Keys!.Table!.Provider);

        // A SQL Server connection string holding no secret is a connection; one holding a password is a literal secret, and
        // another source system's connection is a whole reference, since its string is that system's to parse.
        Parse(WithKeys("connection: \"Server=welldb.example.test;Database=WellDB;Authentication=Active Directory Default\"\nobject: WellDB.dbo.Wellbore\n"));
        Assert.Contains("carries a literal password", Refused(WithKeys("connection: \"Server=welldb;Database=WellDB;User Id=a;Password=hunter2\"\nobject: WellDB.dbo.Wellbore\n")), StringComparison.Ordinal);
        Assert.Contains("is not a ${keyvault:vault/secret} or ${env:NAME} reference", Refused(WithKeys("connection: \"Host=pg;Database=welldb\"\nprovider: postgres\nobject: welldb.public.wellbore\n")), StringComparison.Ordinal);
        Assert.Contains("must be a three-part [Database].[Schema].[Object] name", Refused(WithKeys("connection: ${env:DB}\nobject: dbo.Wellbore\n")), StringComparison.Ordinal);
    }

    [Fact]
    public void Tokens_name_declared_parameters_and_the_partition_is_the_runs()
    {
        Assert.Contains("uses '{region}', which is not declared under parameters", Refused(WithKeys("type: csv\nlocation: data/{region}\n")), StringComparison.Ordinal);
        Assert.Equal("data/{partition}", Parse(WithKeys("type: csv\nlocation: data/{partition}\n")).Entries[0].Keys!.Reader!.Location);
        Assert.Contains("parameters declares 'partition'", Refused(Head + "parameters:\n  partition: { default: dev }\n" + Keys), StringComparison.Ordinal);
        Assert.Contains("values.partition is set", Refused(Head + "removals:\n  - { flow: a, values: { partition: dev }, keys: { type: csv, location: a } }\n"), StringComparison.Ordinal);
        Assert.Contains("uses '{country}', which is not declared", Refused(Head + "removals:\n  - { flow: a, values: { country: \"{country}\" }, keys: { type: csv, location: a } }\n"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_loader_refuses_a_key_it_does_not_know_and_a_literal_secret_in_a_location()
    {
        Assert.Throws<FlowValidationException>(() => Parse(Head + "removals:\n  - { flow: a, keys: { type: csv, location: a }, purge: true }\n"));
        Assert.Throws<FlowValidationException>(() => Parse(Head + "guard: { maxShare: 3 }\n" + Keys));
        Assert.Throws<FlowValidationException>(() => Parse(WithKeys("type: csv\nlocation: \"https://user:hunter2@lake.example.test/wellbore\"\n")));
        Assert.Contains("name", Refused("flowType: removal\n" + Keys), StringComparison.Ordinal);
    }

    [Fact]
    public void A_run_removes_in_one_partition_the_one_it_names_or_the_registrys_default_among_the_flows()
    {
        var two = Parse(Head + Keys);
        var registry = new RegisteredPartitions([new RegisteredPartition("dev", null, false), new RegisteredPartition("test", null, true)]);
        Assert.True(two.NeedsRegistry(null));
        Assert.False(two.NeedsRegistry("dev"));
        Assert.Equal("dev", two.ForRun("DEV", registry).Partition);
        Assert.Equal("test", two.ForRun(null, registry).Partition);
        Assert.Contains("does not remove in partition 'prod'", Assert.Throws<DeliveryException>(() => two.ForRun("prod", registry)).Message, StringComparison.Ordinal);
        Assert.Contains("removes in one partition per run", Assert.Throws<DeliveryException>(() => two.ForRun(PartitionNames.Every, registry)).Message, StringComparison.Ordinal);
        Assert.Contains("name the partition this run removes in", Assert.Throws<DeliveryException>(() => two.ForRun(null, RegisteredPartitions.None)).Message, StringComparison.Ordinal);

        var one = Parse(Head.Replace("partitions: [dev, test]", "partitions: [dev]", StringComparison.Ordinal) + Keys);
        Assert.False(one.NeedsRegistry(null));
        Assert.Equal("dev", one.ForRun(null, RegisteredPartitions.None).Partition);

        // A flow that names no partitions removes in every registered one, the run naming which.
        var every = Parse(Head.Replace("partitions: [dev, test]\n", string.Empty, StringComparison.Ordinal) + Keys);
        Assert.True(every.FollowsRegistry);
        Assert.True(every.NeedsRegistry("dev"));
        Assert.Equal(["dev", "test"], every.Served(registry));
        Assert.Equal("dev", every.ForRun("dev", registry).Partition);
        Assert.Throws<DeliveryException>(() => every.ForRun("prod", registry));
        Assert.Contains("No partition is registered", Assert.Throws<DeliveryException>(() => every.ForRun(null, RegisteredPartitions.None)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_run_carries_out_the_removals_it_names_in_document_order_or_every_one()
    {
        var flow = Parse(Head + """
            removals:
              - { name: a, flow: a, keys: { type: csv, location: a } }
              - { name: b, flow: b, keys: { type: csv, location: b } }
              - { name: c, flow: c, keys: { type: csv, location: c } }
            """);
        Assert.Equal(["a", "b", "c"], flow.Select([]).Select(e => e.Name));
        Assert.Equal(["a", "c"], flow.Select(["C", "a"]).Select(e => e.Name));
        Assert.Contains("has no removal named 'x'", Assert.Throws<DeliveryException>(() => flow.Select(["x"])).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_kind_offers_remove_and_plan_needs_the_repository_and_names_its_sources_and_delivery_flows()
    {
        var kind = new RemovalFlowKind(new DeliveryDocumentLoader());
        Assert.Equal("removal", kind.FlowType);
        Assert.Equal([("remove", true), ("plan", false)], kind.Operations.Select(o => (o.Name, o.WritesTarget)));

        var document = Assert.IsType<RemovalFlowDocument>(kind.Parse(Head + Keys + """
              - name: logs
                flow: welldb-welllog-03-delivery
                removed: { connection: "${env:WELLDB_DB}", object: WellDB.dbo.DeletedLogs, removedAt: { name: deleted_at } }
            """, "flows/removal.yaml"));
        Assert.Equal(("welldb-wellbore-05-removal", "removal", "welldb"), (document.Name, document.Kind, document.Batch));
        Assert.True(document.RequiresRepoTree);
        Assert.Contains("../data/wellbore", document.SourceReference, StringComparison.Ordinal);
        Assert.Contains("WellDB.dbo.DeletedLogs", document.SourceReference, StringComparison.Ordinal);
        Assert.Contains("welldb-wellbore-03-delivery", document.TargetReference, StringComparison.Ordinal);
        Assert.Contains("welldb-welllog-03-delivery", document.TargetReference, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "remove")]
    [InlineData("remove", "remove")]
    [InlineData("plan", "plan")]
    public void A_removal_flow_removes_by_default_and_plans_when_asked(string? operation, string expected)
        => Assert.Equal(expected, RemovalFlowKind.Operation(new RunParameters { Operation = operation }));

    [Theory]
    [InlineData("deliver")]
    [InlineData("build")]
    [InlineData("reconcile")]
    [InlineData("undo")]
    public void A_removal_flow_runs_no_other_operation(string operation)
        => Assert.Contains("'" + operation + "' is not one of them", Assert.Throws<SqlFlowException>(() => RemovalFlowKind.Operation(new RunParameters { Operation = operation })).Message, StringComparison.Ordinal);

    [Fact]
    public void An_approval_names_one_removal_its_count_and_the_partition_and_only_a_remove_run_carries_one()
    {
        var kind = new RemovalFlowKind(new DeliveryDocumentLoader());
        var approval = DeliveryRunPayload.Parse("""{ "entries": ["wellbores"], "approve": { "expected": 640 }, "confirm": "dev" }""");
        Assert.Equal((640L, "dev"), (approval.Approve!.Expected, approval.Confirm));
        Assert.Contains("\"approve\":{\"expected\":640}", approval.ToJson(), StringComparison.Ordinal);
        var again = DeliveryRunPayload.Parse(approval.ToJson());
        Assert.Equal((640L, "dev", "wellbores"), (again.Approve!.Expected, again.Confirm, Assert.Single(again.Entries)));
        kind.ValidateParameters(new RunParameters { Payload = approval.ToJson() });
        kind.ValidateParameters(new RunParameters { Operation = DeliveryOperations.Remove, Payload = approval.ToJson() });

        Assert.Contains("does not apply to the plan operation", Assert.Throws<SqlFlowException>(() => kind.ValidateParameters(new RunParameters
        {
            Operation = DeliveryOperations.Plan, Payload = approval.ToJson(),
        })).Message, StringComparison.Ordinal);
        Assert.Contains("nothing is approved without all three", Assert.Throws<SqlFlowException>(() => kind.ValidateParameters(new RunParameters
        {
            Payload = """{ "entries": ["wellbores"], "approve": { "expected": 640 } }""",
        })).Message, StringComparison.Ordinal);
        Assert.Contains("nothing is approved without all three", Assert.Throws<SqlFlowException>(() => kind.ValidateParameters(new RunParameters
        {
            Payload = """{ "entries": ["wellbores", "logs"], "approve": { "expected": 640 }, "confirm": "dev" }""",
        })).Message, StringComparison.Ordinal);
        Assert.Contains("nothing is approved without all three", Assert.Throws<SqlFlowException>(() => kind.ValidateParameters(new RunParameters
        {
            Payload = """{ "approve": { "expected": 640 }, "confirm": "dev" }""",
        })).Message, StringComparison.Ordinal);
        Assert.Contains("payload confirm does not apply to a removal run without an approval", Assert.Throws<SqlFlowException>(() => kind.ValidateParameters(new RunParameters
        {
            Payload = """{ "entries": ["wellbores"], "confirm": "dev" }""",
        })).Message, StringComparison.Ordinal);

        Assert.Throws<SqlFlowException>(() => DeliveryRunPayload.Parse("""{ "approve": { "expected": 0 } }"""));
        Assert.Throws<SqlFlowException>(() => DeliveryRunPayload.Parse("""{ "approve": { "expected": "640" } }"""));
        Assert.Throws<SqlFlowException>(() => DeliveryRunPayload.Parse("""{ "approve": { "expected": 640, "keys": [] } }"""));
        Assert.Throws<SqlFlowException>(() => DeliveryRunPayload.Parse("""{ "approve": 640 }"""));
    }

    [Fact]
    public void A_removal_reads_every_key_its_sources_hold_so_the_built_in_run_overrides_are_refused()
    {
        var kind = new RemovalFlowKind(new DeliveryDocumentLoader());
        Assert.Throws<SqlFlowException>(() => kind.ValidateParameters(new RunParameters { FullLoad = true }));
        Assert.Throws<SqlFlowException>(() => kind.ValidateParameters(new RunParameters { FilePattern = "*.csv" }));
        Assert.Throws<SqlFlowException>(() => kind.ValidateParameters(new RunParameters { SourceFilter = "1 = 1" }));
        kind.ValidateParameters(new RunParameters());
    }

    [Fact]
    public void The_loader_knows_the_kind_and_says_so_in_its_summary()
    {
        var loader = new DeliveryDocumentLoader();
        Assert.Contains(RemovalFlowDefinition.FlowTypeName, DeliveryDocumentLoader.FlowTypes);
        Assert.Equal("removal", loader.Probe(Head + Keys, "flows/removal.yaml"));
    }
}
