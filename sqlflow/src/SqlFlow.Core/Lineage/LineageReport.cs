namespace SqlFlow.Core.Lineage;

/// <summary>Where a lineage fact came from. The tiers cross-check each other; every edge carries its tier so
/// the graph is honest about what it knows in each connectivity posture.</summary>
public enum LineageTier
{
    /// <summary>Stated by a flow document (the YAML): what the author intends. Always available.</summary>
    Declared = 0,

    /// <summary>Extracted from the canonical run artifacts (run.json SqlTrace): what actually executed,
    /// stamped with the run. Available offline.</summary>
    Observed = 1,

    /// <summary>Expanded from live catalog metadata and module definitions (sys.sql_modules) parsed as
    /// T-SQL: what the database adds. Needs connectivity.</summary>
    Derived = 2,
}

/// <summary>What kind of catalog object a lineage node is, when known.</summary>
public enum LineageNodeKind
{
    Unknown = 0,
    Table = 1,
    View = 2,
    Procedure = 3,
    Function = 4,
    Trigger = 5,
    Synonym = 6,

    /// <summary>A file endpoint (a file flow's source, an export flow's destination).</summary>
    File = 7,

    /// <summary>A data subscriber: a report, workbook, notebook, or application that CONSUMES the warehouse.
    /// It is the far end of the graph, the only node kind that lives outside the databases SQLFlow moves data
    /// between, and it exists so "which dashboard breaks if I change this table" is a graph query rather than
    /// tribal knowledge.</summary>
    Subscriber = 8,

    /// <summary>A dataset of an external system a registered flow kind declares it reads or writes: a record type in
    /// a partition of a data platform, a queue, a collection. It lives outside the databases SQLFlow connects to, but
    /// it always carries a namespace (as its database) and a group (as its schema), so it is identified like any other
    /// object and never mistaken for an identity waiting on its database.</summary>
    Dataset = 9,
}

/// <summary>How a flow or module relates to an object (the DeltaForge typed-relation taxonomy).</summary>
public enum LineageRelation
{
    Reads = 0,
    Writes = 1,
    Creates = 2,

    /// <summary>Needs the object to exist (EXEC of a procedure, a TVF call, ALTER).</summary>
    Requires = 3,

    Destroys = 4,

    /// <summary>A schema registration flow (flowType: sch) recorded the object's metadata in the catalog. Metadata
    /// only: it implies no dependency, orders nothing, and moves no data.</summary>
    Registers = 5,

    /// <summary>A lineage description (flowType: lin) says a step SQLFlow does not run reads the object. Metadata only:
    /// it implies no dependency, orders nothing and moves no data, and it is a relation of its own so that nothing
    /// computed over <see cref="Reads"/> (waves, dependencies, object levels, data streams) ever sees it.</summary>
    DescribedReads = 6,

    /// <summary>A lineage description (flowType: lin) says a step SQLFlow does not run writes the object. Metadata
    /// only, and apart from <see cref="Writes"/> for the same reason as <see cref="DescribedReads"/>.</summary>
    DescribedWrites = 7,
}

/// <summary>The relations a lineage description contributes, and how they read as the data movement they describe.</summary>
public static class LineageRelations
{
    /// <summary>Whether <paramref name="relation"/> comes from a lineage description (flowType: lin).</summary>
    public static bool IsDescribed(LineageRelation relation)
        => relation is LineageRelation.DescribedReads or LineageRelation.DescribedWrites;

    /// <summary>Whether <paramref name="relation"/>, as the catalog spells it, comes from a lineage description.</summary>
    public static bool IsDescribed(string? relation)
        => string.Equals(relation, nameof(LineageRelation.DescribedReads), StringComparison.Ordinal)
           || string.Equals(relation, nameof(LineageRelation.DescribedWrites), StringComparison.Ordinal);
}

/// <summary>One pipeline (flow document) participating in the graph.</summary>
public sealed record LineageFlowNode
{
    public required string Name { get; init; }

    /// <summary>The document kind: file/ing/exp/sp/inv/hc.</summary>
    public required string Kind { get; init; }

    /// <summary>The flow document path, relative to the scanned folder.</summary>
    public required string File { get; init; }

    public string? Batch { get; init; }

    /// <summary>The flow's execution mode (today only a health-check flow can declare <c>mode: manual</c>).
    /// Manual flows are excluded from automatic dispatch (local batch membership, control-plane group
    /// expansion, the scheduler); a direct single-flow trigger runs them regardless.</summary>
    public Runs.ExecutionMode Mode { get; init; } = Runs.ExecutionMode.Auto;

    /// <summary>The flow's declared lifecycle (the YAML <c>lifecycle:</c>): only production pipelines generate
    /// notification events. Execution is unaffected either way.</summary>
    public Runs.FlowLifecycle Lifecycle { get; init; } = Runs.FlowLifecycle.Production;
}

/// <summary>
/// One data subscriber participating in the graph: a report, workbook, notebook, or application that consumes
/// the warehouse. A subscriber is not a flow (it never runs and moves nothing) and not a database object, so it
/// carries its own node record; its <see cref="ObjectKey"/> is the identity of the
/// <see cref="LineageNodeKind.Subscriber"/> object node its read edges point from, which is how the consumption
/// side and the production side meet in one graph.
/// </summary>
public sealed record LineageSubscriberNode
{
    public required string Name { get; init; }

    /// <summary>What consumes the data: PowerBI, Tableau, Excel, Notebook, Application, or the estate's own label.</summary>
    public required string Type { get; init; }

    /// <summary>The subscriber's node key: the identity its <c>Reads</c> edges carry as <c>ViaModule</c>.</summary>
    public required string ObjectKey { get; init; }

    /// <summary>The subscriber library file that declares it, relative to the scanned folder.</summary>
    public required string File { get; init; }

    /// <summary>The team or person to contact before a breaking change to a table it reads.</summary>
    public string? Owner { get; init; }

    public string? Description { get; init; }

    /// <summary>Remarks about the subscriber's state: not refreshed since a given month, apparently superseded,
    /// could not be opened, an open question. Distinct from <see cref="Description"/>, which says what the
    /// subscriber is for.</summary>
    public string? Notes { get; init; }

    /// <summary>Where the subscriber lives (report URL, workbook path, repository).</summary>
    public string? Url { get; init; }

    /// <summary>Whether its library asks a sync to generate business questions for its report's visuals.</summary>
    public bool GenerateQuestions { get; init; }

    /// <summary>The queries it runs, in declaration order: the evidence behind its read edges.</summary>
    public required IReadOnlyList<LineageSubscriberQuery> Queries { get; init; }

    /// <summary>
    /// The report's own internal structure, when the subscriber is backed by a report file the collector could
    /// read: the pages a person flips through and the visuals on each. Empty for a hand-authored subscriber and
    /// for any consumer whose file is not a readable report.
    /// </summary>
    public IReadOnlyList<LineageSubscriberPage> Pages { get; init; } = [];

    /// <summary>
    /// The semantic model behind each report file the collector read: its tables with the Power Query that loads
    /// them, the columns, calculated columns, and measures defined on each, and the relationships between tables.
    /// This is how a report computes its numbers, which its pages and visuals alone do not say. Empty for a
    /// hand-authored subscriber and for a report connected live to a published dataset.
    /// </summary>
    public IReadOnlyList<LineageSubscriberModel> Models { get; init; } = [];
}

/// <summary>The semantic model of one report file.</summary>
public sealed record LineageSubscriberModel
{
    /// <summary>The report file the model came from, the same value its pages carry.</summary>
    public required string ReportFile { get; init; }

    public required IReadOnlyList<LineageSubscriberModelTable> Tables { get; init; }

    public required IReadOnlyList<LineageSubscriberModelRelationship> Relationships { get; init; }

    /// <summary>The model's shared Power Query expressions: queries it keeps without loading them as tables, which a
    /// table's own query can merge in by name.</summary>
    public IReadOnlyList<LineageSubscriberExpression> Expressions { get; init; } = [];
}

/// <summary>One shared Power Query expression of a report's semantic model.</summary>
public sealed record LineageSubscriberExpression
{
    /// <summary>The query's name, as another query refers to it.</summary>
    public required string Name { get; init; }

    /// <summary>Its Power Query (M) text.</summary>
    public required string PowerQuery { get; init; }
}

/// <summary>One table of a report's semantic model.</summary>
public sealed record LineageSubscriberModelTable
{
    /// <summary>The model entity name, as visuals and DAX refer to it (empty for fields the model declared without a
    /// home table).</summary>
    public required string Name { get; init; }

    /// <summary>The Power Query (M) expression that loads the table, when the model declares one.</summary>
    public string? PowerQuery { get; init; }

    /// <summary>The warehouse database the expression resolved to, when it resolved.</summary>
    public string? SourceDatabase { get; init; }

    /// <summary>The warehouse schema the expression resolved to, when it resolved.</summary>
    public string? SourceSchema { get; init; }

    /// <summary>The warehouse table the expression resolved to, when it resolved.</summary>
    public string? SourceName { get; init; }

    /// <summary>The connection identity the report reads through (the server segment of a node key), set by the
    /// collector when the table's source resolved.</summary>
    public string? ServerRef { get; init; }

    /// <summary>The node key of the warehouse object the table loads from, resolved by the graph builder through the
    /// same identity resolution every lineage edge takes, so it is the key the catalog and the semantic layer use for
    /// that object. Null when the source did not resolve.</summary>
    public string? ObjectKey { get; init; }

    /// <summary>Its columns, calculated columns, and measures, in that order.</summary>
    public required IReadOnlyList<LineageSubscriberModelField> Fields { get; init; }
}

/// <summary>One column, calculated column, or measure defined on a model table.</summary>
public sealed record LineageSubscriberModelField
{
    public required string Name { get; init; }

    /// <summary><c>column</c>, <c>calculatedColumn</c>, or <c>measure</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>A column's data type.</summary>
    public string? DataType { get; init; }

    /// <summary>A calculated column's or measure's DAX expression.</summary>
    public string? Expression { get; init; }

    /// <summary>A measure's author-written description.</summary>
    public string? Description { get; init; }
}

/// <summary>One relationship between two tables of a report's semantic model.</summary>
public sealed record LineageSubscriberModelRelationship
{
    public required string FromTable { get; init; }

    public string? FromColumn { get; init; }

    public required string ToTable { get; init; }

    public string? ToColumn { get; init; }

    /// <summary><c>1:1</c>, <c>M:1</c>, <c>1:M</c>, or <c>M:M</c>.</summary>
    public string? Cardinality { get; init; }

    /// <summary>False when the relationship applies only where a measure invokes it with USERELATIONSHIP.</summary>
    public required bool IsActive { get; init; }
}

/// <summary>
/// One page of a subscriber's report. A page groups the questions asked together, which is itself a fact worth
/// keeping: visuals on one page are usually facets of a single business question rather than unrelated ones.
/// </summary>
public sealed record LineageSubscriberPage
{
    /// <summary>The report file this page came from, relative to the subscriber's declared <c>pbix:</c> path.
    /// A subscriber backed by one file has every page carry that file's own name; a subscriber backed by a
    /// DIRECTORY of reports has each page carry which of the directory's files it belongs to, since two
    /// different reports routinely both have a "Page 1" and the pages must not be confused in the catalog.</summary>
    public required string ReportFile { get; init; }

    /// <summary>The page's internal identifier, as the report file spells it.</summary>
    public required string Name { get; init; }

    /// <summary>The page's title as a person sees it on the tab.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The page's position within the report, 1-based.</summary>
    public required int Ordinal { get; init; }

    public required IReadOnlyList<LineageSubscriberVisual> Visuals { get; init; }
}

/// <summary>
/// One visual on a page: a business question with its shape already decided. Only visuals that project at
/// least one field appear here, since a shape or a textbox asks nothing.
/// </summary>
public sealed record LineageSubscriberVisual
{
    /// <summary>The visual's position within its page, 1-based.</summary>
    public required int Ordinal { get; init; }

    /// <summary>The chart type: <c>areaChart</c>, <c>pivotTable</c>, <c>slicer</c>, and so on.</summary>
    public required string VisualType { get; init; }

    /// <summary>The visual's authored title, when it has one. Often the question in the author's own words.</summary>
    public string? Title { get; init; }

    /// <summary>The name of the subscriber query synthesized from this visual, linking the structure to the SQL
    /// that carries its lineage. Matches a <see cref="LineageSubscriberQuery.Name"/> on the same subscriber.</summary>
    public required string QueryName { get; init; }

    public required IReadOnlyList<LineageSubscriberField> Fields { get; init; }
}

/// <summary>
/// One field or measure a visual projects, and the ROLE it plays in the question: the difference between "sales
/// by month" and "months by sales". The role is the report author's own statement of the question's shape, and
/// is the one fact parsed SQL cannot recover, since a query's column list has no notion of axis versus value.
/// </summary>
public sealed record LineageSubscriberField
{
    /// <summary>The projection bucket the field sits in: <c>Category</c>, <c>Y</c>, <c>Rows</c>, <c>Values</c>,
    /// <c>Size</c>, and so on, exactly as the report file labels it.</summary>
    public required string Role { get; init; }

    /// <summary>The table or entity the field belongs to, as the report's model names it.</summary>
    public required string TableName { get; init; }

    /// <summary>The column or measure name within <see cref="TableName"/>.</summary>
    public required string ColumnOrMeasure { get; init; }

    /// <summary>True when this is a measure (model-computed business logic) rather than a plain column.</summary>
    public required bool IsMeasure { get; init; }
}

/// <summary>One query a subscriber runs, and the objects parsing it proved that query reads.</summary>
public sealed record LineageSubscriberQuery
{
    public required string Name { get; init; }

    /// <summary>The server identity the query runs against (the connection reference behind its alias).</summary>
    public required string ServerRef { get; init; }

    /// <summary>The query text as declared. Kept so the catalog can show WHY a table is linked to a report.</summary>
    public required string Sql { get; init; }

    /// <summary>The node keys this query reads, ordinal-sorted and deduplicated.</summary>
    public required IReadOnlyList<string> ObjectKeys { get; init; }

    /// <summary>For a query a report visual asks: the same question as T-SQL over the source tables the report's
    /// model loads from, runnable on the connection those tables were resolved to. Null when the query is not a
    /// visual's, or when it could not be translated (see <see cref="TranslationProblem"/>).</summary>
    public string? SourceSql { get; init; }

    /// <summary>Why a visual's query has no <see cref="SourceSql"/>; null otherwise.</summary>
    public string? TranslationProblem { get; init; }
}

/// <summary>One catalog or file object participating in the graph. The key is the canonical node identity:
/// server reference, database, schema, and name, case-folded; raw spellings are preserved here.</summary>
public sealed record LineageObjectNode
{
    public required string Key { get; init; }

    /// <summary>The server identity: the connection REFERENCE the object was reached through (a
    /// ${env:...} reference, an @alias, or an inline string's redacted identity). Two documents naming the
    /// same reference name the same server; distinct references are distinct servers by construction.</summary>
    public required string ServerRef { get; init; }

    public string? Database { get; init; }

    public string? Schema { get; init; }

    public required string Name { get; init; }

    public LineageNodeKind Kind { get; init; } = LineageNodeKind.Unknown;

    /// <summary>The module body (the <c>sys.sql_modules</c> definition) for a view/procedure/function/trigger,
    /// captured by the derived (connected) tier. Null for a plain table, an offline tier, or an encrypted module.</summary>
    public string? Definition { get; init; }

    /// <summary>The generating DDL of the object (the <c>CREATE TABLE</c>/<c>CREATE VIEW</c> we ran), captured
    /// from the observed run trace or a declared hook so the catalog holds the script offline. Null when no
    /// tier saw the object created (a pre-existing source table). Distinct from <see cref="Definition"/>: that
    /// is the live module body read from the database, this is the script the engine emitted.</summary>
    public string? Script { get; init; }

    /// <summary>The tier that supplied <see cref="Script"/>, or null when there is no script.</summary>
    public LineageTier? ScriptTier { get; init; }

    /// <summary>The object's columns: the derived (connected) tier reads them live; offline they are parsed
    /// from the CREATE TABLE the run executed. Empty when no tier saw them.</summary>
    public IReadOnlyList<LineageColumn> Columns { get; init; } = [];

    /// <summary>The tier that supplied <see cref="Columns"/>, or null when there are none. The catalog uses it
    /// to keep a live (derived) dictionary from being overwritten by an offline (observed) one.</summary>
    public LineageTier? ColumnsTier { get; init; }

    /// <summary>The object's interpreted key columns (its primary/business key), in key order: parsed from an
    /// explicit PRIMARY KEY clause, declared by the loading flow's YAML key columns, or inferred from the
    /// MERGE that loads it. Empty when nothing in the codebase names a key.</summary>
    public IReadOnlyList<string> KeyColumns { get; init; } = [];

    /// <summary>How <see cref="KeyColumns"/> was interpreted, or null when no key is known.</summary>
    public LineageModelOrigin? KeyOrigin { get; init; }

    /// <summary>Node-scoped findings: an encrypted module whose definition is unreadable, an ambiguous
    /// database resolution, a linked-server reference that could not be expanded.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>One column of a catalog object, as the derived (connected) tier read it from the live database.</summary>
public sealed record LineageColumn
{
    /// <summary>1-based column position.</summary>
    public required int Ordinal { get; init; }

    public required string Name { get; init; }

    /// <summary>The SQL Server type rendered with length/precision (for example <c>nvarchar(100)</c>).</summary>
    public string? DataType { get; init; }

    public bool Nullable { get; init; }
}

/// <summary>Where an object's inferred key (or a model relationship) came from. Warehouses rarely declare
/// physical constraints, so the data model is INTERPRETED from the codebase; the origin says how strong the
/// interpretation is.</summary>
public enum LineageModelOrigin
{
    /// <summary>An explicit PRIMARY KEY / FOREIGN KEY clause parsed from DDL in the codebase.</summary>
    Constraint = 0,

    /// <summary>Declared in a flow document (the YAML key columns of the target table).</summary>
    Declared = 1,

    /// <summary>The ON clause of a MERGE loading the table (its upsert match key).</summary>
    Merge = 2,

    /// <summary>An equality join predicate observed in the codebase's SQL (JOIN ... ON / WHERE equi-join).</summary>
    Join = 3,
}

/// <summary>One interpreted data-model relationship between two catalog objects, distinct from the flow and
/// module lineage in <see cref="LineageEdge"/>: how the tables JOIN, not which flow moves data. Both ends are
/// node keys (<see cref="LineageObjectNode.Key"/>); the column lists pair positionally (FromColumns[i] joins
/// ToColumns[i]). A <see cref="LineageModelOrigin.Constraint"/> relationship was parsed from an explicit
/// FOREIGN KEY clause; a <see cref="LineageModelOrigin.Join"/> relationship was inferred from the equality
/// predicates the codebase actually joins on, with <see cref="Occurrences"/> counting how many distinct
/// scripts exhibited it (the canonical join path scores highest).</summary>
public sealed record LineageModelRelationship
{
    /// <summary>The constraint name, when parsed from an explicit FOREIGN KEY clause; null for an inferred join.</summary>
    public string? Name { get; init; }

    /// <summary>The referencing side's node key (for a join without key knowledge, simply the first side seen).</summary>
    public required string FromObjectKey { get; init; }

    /// <summary>The referencing columns, in predicate/constraint order.</summary>
    public required IReadOnlyList<string> FromColumns { get; init; }

    /// <summary>The referenced side's node key.</summary>
    public required string ToObjectKey { get; init; }

    public required IReadOnlyList<string> ToColumns { get; init; }

    /// <summary>The comparison operator per column pair, same arity as the column lists. Empty, or all "=",
    /// is a key match; a range join (an interval containment, typically a temporal dimension lookup) carries
    /// the real operators so a consumer can tell the two apart.</summary>
    public IReadOnlyList<string> Operators { get; init; } = [];

    /// <summary>The distinct join types the codebase uses for this relationship, in a stable order. More than
    /// one means different scripts disagree, which is worth knowing before writing another query: INNER where
    /// the estate writes LEFT silently drops rows.</summary>
    public IReadOnlyList<string> JoinTypes { get; init; } = [];

    public required LineageModelOrigin Origin { get; init; }

    public required LineageTier Tier { get; init; }

    /// <summary>How many distinct scripts exhibited this relationship (1 for an explicit constraint).</summary>
    public required int Occurrences { get; init; }
}

/// <summary>One attributed lineage fact: a flow or module relating to an object.</summary>
public sealed record LineageEdge
{
    /// <summary>The flow this fact belongs to; null for a module-derived fact.</summary>
    public string? Flow { get; init; }

    /// <summary>The module (view/procedure/function key) whose definition produced this fact; null for a
    /// flow-level fact.</summary>
    public string? ViaModule { get; init; }

    public required LineageRelation Relation { get; init; }

    /// <summary>The object the relation points at (a <see cref="LineageObjectNode.Key"/>).</summary>
    public required string ObjectKey { get; init; }

    public required LineageTier Tier { get; init; }

    /// <summary>The run that observed this fact (observed tier only).</summary>
    public Guid? ObservedRunId { get; init; }

    public DateTime? ObservedAtUtc { get; init; }

    /// <summary>The trace step that produced the statement (observed tier only).</summary>
    public string? Step { get; init; }
}

/// <summary>A flow-level dependency: ToFlow must wait for FromFlow, because of the named objects.</summary>
public sealed record LineageFlowDependency
{
    public required string FromFlow { get; init; }

    public required string ToFlow { get; init; }

    /// <summary>The object keys that mediate the dependency.</summary>
    public required IReadOnlyList<string> ViaObjects { get; init; }
}

/// <summary>One concurrency wave: everything in it can run together once the previous wave finished.</summary>
public sealed record LineageWave
{
    public required int Wave { get; init; }

    public required IReadOnlyList<string> Flows { get; init; }
}

/// <summary>The objective of the calculation: what order, what runs concurrently, what must wait.</summary>
public sealed record LineageExecutionPlan
{
    public required IReadOnlyList<LineageWave> Waves { get; init; }

    /// <summary>Flows whose mutual order could not be resolved (dependency-cycle members). They are
    /// scheduled together in the final fallback wave, with the cycle reported: the DeltaForge behavior of
    /// degrading loudly instead of refusing to plan.</summary>
    public required IReadOnlyList<string> Unordered { get; init; }
}

/// <summary>A dependency cycle: ordering is undecidable for these flows until an edge is broken.</summary>
public sealed record LineageCycle
{
    /// <summary>The flows in the cycle, in path order (the last depends on the first).</summary>
    public required IReadOnlyList<string> Flows { get; init; }

    /// <summary>The objects mediating the cycle's edges.</summary>
    public required IReadOnlyList<string> ViaObjects { get; init; }
}

/// <summary>One flow name declared by more than one document. The graph collapses the colliding documents to a
/// single flow node (first wins) and warns, so the report still plans; this record keeps the collision in a
/// structured form so a stricter consumer (batch membership) can refuse it without parsing warning text.</summary>
public sealed record LineageDuplicateFlowName
{
    /// <summary>The shared flow name (the casing of the first declaring document).</summary>
    public required string Name { get; init; }

    /// <summary>Every document that declared the name, as repository-relative paths, ordinal-sorted.</summary>
    public required IReadOnlyList<string> Files { get; init; }
}

/// <summary>
/// The canonical lineage artifact (<c>lineage.json</c>): the aggregated graph of every pipeline and object,
/// with full provenance, plus the execution plan derived from it. Like the run artifacts, the envelope is a
/// stable, versioned contract: the file can be charted, diffed, or bulk-loaded without per-version parsing,
/// and full mode persists the same shape to the control database later.
/// </summary>
public sealed record LineageReport
{
    /// <summary>The current lineage.json schema version written by this build. Version 2 adds the object
    /// <see cref="LineageObjectNode.Script"/> and its offline columns; version 3 adds the interpreted data
    /// model (the per-object <see cref="LineageObjectNode.KeyColumns"/> and the <see cref="Relationships"/>
    /// list); version 4 adds the consumption side (<see cref="Subscribers"/> and their read edges); version 5
    /// adds the described lineage (<see cref="Described"/>), which leaves every other member as it was. Each
    /// version is additive: an older reader sees the same shape plus new fields it can ignore.</summary>
    public const int CurrentSchemaVersion = 5;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required DateTime GeneratedAtUtc { get; init; }

    /// <summary>The scanned flow directory.</summary>
    public required string FlowDirectory { get; init; }

    /// <summary>Which tiers fed this graph (the connectivity posture of the computation).</summary>
    public required IReadOnlyList<LineageTier> TiersUsed { get; init; }

    public required IReadOnlyList<LineageFlowNode> Flows { get; init; }

    public required IReadOnlyList<LineageObjectNode> Objects { get; init; }

    public required IReadOnlyList<LineageEdge> Edges { get; init; }

    /// <summary>The consumption side of the estate: every declared data subscriber, sorted by name, with the
    /// queries that link it to the warehouse. Their read edges are in <see cref="Edges"/> like any other fact
    /// (carrying the subscriber's node key as <c>ViaModule</c>), so "what consumes this table" needs no special
    /// query path. Empty when the estate declares no <c>subscribers.yaml</c>; not required in the envelope so a
    /// version-3 document still deserializes.</summary>
    public IReadOnlyList<LineageSubscriberNode> Subscribers { get; init; } = [];

    /// <summary>The interpreted data model: every PK/FK-style relationship the codebase's SQL exhibits
    /// (explicit constraint clauses plus inferred join predicates), both ends resolved to node keys and
    /// aggregated with occurrence counts. Not required in the envelope so a version-2 document (which
    /// predates the field) still deserializes.</summary>
    public IReadOnlyList<LineageModelRelationship> Relationships { get; init; } = [];

    public required IReadOnlyList<LineageFlowDependency> FlowDependencies { get; init; }

    public required LineageExecutionPlan ExecutionPlan { get; init; }

    public required IReadOnlyList<LineageCycle> Cycles { get; init; }

    /// <summary>Flow names declared by more than one document, with every declaring file. The graph keeps only
    /// the first declaration (the other facts merge under it) and also warns; this is the same collision in a
    /// structured form. Empty when every flow name is unique.</summary>
    public required IReadOnlyList<LineageDuplicateFlowName> DuplicateFlowNames { get; init; }

    /// <summary>Graph-scoped findings: multi-writer objects, stale observations, parse warnings, unhandled
    /// statement kinds. Never silently dropped.</summary>
    public required IReadOnlyList<string> Warnings { get; init; }

    /// <summary>Server identities whose DERIVED collection was requested but failed (unreachable server,
    /// unresolvable secret): this computation carries no module knowledge for them, so a consumer persisting
    /// the report must keep previously-derived edges for these servers instead of wiping them with the
    /// degraded pass. Empty when the derived tier was not requested or every server was reached.</summary>
    public IReadOnlyList<string> DegradedDerivedServers { get; init; } = [];

    /// <summary>Server identities whose live inventory this computation read in full (the derived tier reached them),
    /// every reference proven an alias of one included: an object of such a server that <see cref="Objects"/> does not
    /// hold is no longer in its database, so a consumer persisting the report may forget it once nothing references it.
    /// Empty when the derived tier was not requested or reached no server.</summary>
    public IReadOnlyList<string> InventoriedServers { get; init; } = [];

    /// <summary>What the estate's lineage descriptions (flowType: lin) add: the steps other systems run, the objects
    /// only they name, and how both connect to the graph above. It is computed after everything else and from it, so
    /// every other member of the report is the same whether the estate holds descriptions or not, and a reader that
    /// wants the lineage SQLFlow computed ignores this member. Empty when the estate declares none; not required in the
    /// envelope so a version-4 document still deserializes.</summary>
    public LineageDescribed Described { get; init; } = LineageDescribed.Empty;
}

/// <summary>
/// The described lineage of an estate: what its lineage descriptions (flowType: lin) say happens outside the flows
/// SQLFlow runs. Kept apart from the computed graph on purpose. Its edges carry
/// <see cref="LineageRelation.DescribedReads"/> and <see cref="LineageRelation.DescribedWrites"/>, a description is in
/// no wave and no dependency, and an object only a description names is not in <see cref="LineageReport.Objects"/>.
/// </summary>
public sealed record LineageDescribed
{
    /// <summary>An estate with no lineage description.</summary>
    public static LineageDescribed Empty { get; } = new();

    /// <summary>The described steps, by name.</summary>
    public IReadOnlyList<LineageDescribedStep> Steps { get; init; } = [];

    /// <summary>The objects only descriptions name, by key. An object the estate's own flows touch is in
    /// <see cref="LineageReport.Objects"/> and is not repeated here.</summary>
    public IReadOnlyList<LineageDescribedObject> Objects { get; init; } = [];

    /// <summary>One edge per object a step reads or writes, with <see cref="LineageEdge.Flow"/> the step's name,
    /// <see cref="LineageTier.Declared"/> as the tier, and a described relation.</summary>
    public IReadOnlyList<LineageEdge> Edges { get; init; } = [];

    /// <summary>The column mappings the steps declare, in step, object and declaration order. An optional level of
    /// detail: a step that maps no column is complete without it.</summary>
    public IReadOnlyList<LineageColumnMapping> Columns { get; init; } = [];

    /// <summary>What a description says that could not be used and why, and where a described chain is left open, one
    /// sentence each. A finding never removes a step: what could be used still counts.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>True when the estate declares no lineage description. Computed, so it is no part of the artifact.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEmpty => Steps.Count == 0 && Warnings.Count == 0;

    /// <summary>
    /// The same described lineage with every object key passed through <paramref name="map"/>: what a consumer that
    /// gives an object of the computed graph another key (a catalog adopting the database-qualified identity of an
    /// object the report knows without its database) applies here too, so a description stays connected to the object
    /// it named. Two connections that become one are kept once.
    /// </summary>
    public LineageDescribed Rekey(Func<string, string> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (IsEmpty)
        {
            return this;
        }

        LineageDescribedEndpoint Endpoint(LineageDescribedEndpoint endpoint) => endpoint with { ObjectKey = map(endpoint.ObjectKey) };
        return this with
        {
            Steps =
            [
                .. Steps.Select(s => s with
                {
                    Reads = [.. s.Reads.Select(Endpoint)],
                    Writes = [.. s.Writes.Select(Endpoint)],
                }),
            ],
            Edges = [.. Edges.Select(e => e with { ObjectKey = map(e.ObjectKey) }).Distinct()],
            Columns =
            [
                .. Columns.Select(c => c with
                {
                    TargetObjectKey = map(c.TargetObjectKey),
                    Sources = [.. c.Sources.Select(s => s with { ObjectKey = map(s.ObjectKey) })],
                }),
            ],
        };
    }
}

/// <summary>One step a lineage description (flowType: lin) describes.</summary>
public sealed record LineageDescribedStep
{
    /// <summary>The description's name: the flow its edges are attributed to.</summary>
    public required string Name { get; init; }

    /// <summary>The description's document, relative to the scanned folder.</summary>
    public required string File { get; init; }

    /// <summary>The system that performs the step.</summary>
    public required string System { get; init; }

    /// <summary>Where the step is found in that system.</summary>
    public string? Unit { get; init; }

    /// <summary>What the step does, in a sentence.</summary>
    public string? Description { get; init; }

    /// <summary>Who to ask about the step.</summary>
    public string? Owner { get; init; }

    /// <summary>Where the step can be looked at.</summary>
    public string? Url { get; init; }

    public string? Batch { get; init; }

    /// <summary>What the description was written from.</summary>
    public IReadOnlyList<LineageDescriptionEvidence> Evidence { get; init; } = [];

    /// <summary>The objects the step reads that could be placed in the graph, in declaration order.</summary>
    public IReadOnlyList<LineageDescribedEndpoint> Reads { get; init; } = [];

    /// <summary>The objects the step writes that could be placed in the graph, in declaration order.</summary>
    public IReadOnlyList<LineageDescribedEndpoint> Writes { get; init; } = [];
}

/// <summary>One object a described step reads or writes, placed in the graph.</summary>
public sealed record LineageDescribedEndpoint
{
    /// <summary>The object's node key: a <see cref="LineageObjectNode.Key"/> of the computed graph when the description
    /// named a flow of the estate, or a <see cref="LineageDescribedObject.Key"/>.</summary>
    public required string ObjectKey { get; init; }

    /// <summary>The object's name, as it is shown.</summary>
    public required string ObjectName { get; init; }

    /// <summary>The flow of the estate the description named to reach the object; null for an object named by a system
    /// and a name.</summary>
    public string? ViaFlow { get; init; }

    /// <summary>The alias the description gave the object, when it gave one.</summary>
    public string? Alias { get; init; }

    /// <summary>What the object holds, as the description says it.</summary>
    public string? Note { get; init; }

    /// <summary>True for a read the description marks as where the data starts.</summary>
    public bool Origin { get; init; }
}

/// <summary>An object only lineage descriptions name: a table, a file set or an endpoint of a system the estate does
/// not manage. Its key can never collide with an object of the computed graph, whose server reference is never of this
/// form.</summary>
public sealed record LineageDescribedObject
{
    /// <summary>The prefix of the server reference every described object's key starts with.</summary>
    public const string ServerPrefix = "described:";

    /// <summary>The node key: <c>described:&lt;system&gt;|||&lt;name&gt;</c>, case-folded like every node key.</summary>
    public required string Key { get; init; }

    /// <summary>The system the object belongs to.</summary>
    public required string System { get; init; }

    /// <summary>The object's name as the first description naming it spells it.</summary>
    public required string Name { get; init; }

    /// <summary>Whether the described chain ends at this object, and how.</summary>
    public LineageDescribedBoundary Boundary { get; init; }

    /// <summary>The server reference of a described object of <paramref name="system"/>.</summary>
    public static string ServerRef(string system)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(system);
        return ServerPrefix + system.Trim().ToLowerInvariant();
    }

    /// <summary>Whether <paramref name="key"/> is the key of a described object.</summary>
    public static bool IsDescribedKey(string? key)
        => key is not null && key.StartsWith(ServerPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>The system of a described object's key, or null for any other key.</summary>
    public static string? SystemOf(string? key)
    {
        if (!IsDescribedKey(key))
        {
            return null;
        }

        var rest = key![ServerPrefix.Length..];
        var end = rest.IndexOf('|', StringComparison.Ordinal);
        return end < 0 ? rest : rest[..end];
    }
}

/// <summary>Where a described chain ends, read upstream. Descriptions are optional and may cover part of a chain, so an
/// end is a fact a reader is told, never a fault.</summary>
public enum LineageDescribedBoundary
{
    /// <summary>A description writes the object, or none reads it: the chain does not end here.</summary>
    None = 0,

    /// <summary>A description reads the object, none writes it, and a read marks it as where the data starts.</summary>
    Origin = 1,

    /// <summary>A description reads the object, none writes it, and no read marks it as an origin: what produces it is
    /// not described.</summary>
    Open = 2,
}

/// <summary>One mapped column of an object a described step writes, with the columns it comes from.</summary>
public sealed record LineageColumnMapping
{
    /// <summary>The described step declaring the mapping.</summary>
    public required string Flow { get; init; }

    /// <summary>The written object's node key.</summary>
    public required string TargetObjectKey { get; init; }

    /// <summary>The written object's name, as it is shown.</summary>
    public required string TargetObjectName { get; init; }

    /// <summary>The mapped column. A <c>*</c> stands for every column not listed by name.</summary>
    public required string TargetColumn { get; init; }

    /// <summary>The column's position among the object's mapped columns, 1-based, in declaration order.</summary>
    public required int Ordinal { get; init; }

    /// <summary>The columns it comes from, in declaration order; empty for a column the step makes itself.</summary>
    public IReadOnlyList<LineageColumnSource> Sources { get; init; } = [];

    /// <summary>How the value is made, as the description says it.</summary>
    public string? Note { get; init; }
}

/// <summary>One source column of a mapped column.</summary>
public sealed record LineageColumnSource
{
    /// <summary>The read object's node key.</summary>
    public required string ObjectKey { get; init; }

    /// <summary>The read object's name, as it is shown.</summary>
    public required string ObjectName { get; init; }

    /// <summary>The column there. A <c>*</c> stands for the column of the same name.</summary>
    public required string Column { get; init; }
}
