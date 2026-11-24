namespace SqlFlow.Delivery.Data;

// What the repository sync found and the OSDU order reads: the search terms a repository's pipelines give and what a
// person made of them, the mappings, the flows that keep a ledger, the interfaces of delivery flows, the template files
// and the teams of the repositories' folders, and OSDU's lineage by release
// (osdu/docs/reference/concepts/osdu-order.md).

/// <summary>
/// A search term a repository's pipelines give (osdu/docs/reference/concepts/search-terms.md) for one entity type: a
/// column of a source table the repository's active delivery flows read, with every route by which it reaches the
/// records of that type they render. A term is the table's column, whichever flows read the table and whichever
/// mappings render it; a row is the term in one repository and one entity type. The sync writes the rows again whenever
/// it runs, from the flows and the mappings they pin; what a person makes of a term is kept apart, in
/// <see cref="DeliverySearchTermRefinement"/>, by the term's identity, so a sync never undoes it.
/// </summary>
public sealed class DeliverySearchTerm
{
    /// <summary>The longest table, entity type and column a term names.</summary>
    public const int MaxSourceLength = 400;

    public const int MaxEntityTypeLength = 200;

    public const int MaxColumnLength = 128;

    /// <summary>Stable id: derived from the repo id, the entity type and the term's key.</summary>
    public Guid Id { get; set; }

    public Guid RepoId { get; set; }

    /// <summary>The term's identity, the same in every repository, entity type and host: derived from its key alone.</summary>
    public Guid TermId { get; set; }

    /// <summary>The term's key as text: <c>osdudata.arc.welllog/wellbore_uwi</c>.</summary>
    public string TermKey { get; set; } = string.Empty;

    /// <summary>The table the column is read from, its parts without brackets (<c>OsduData.arc.WellLog</c>).</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>The entity type the routes fill (<c>work-product-component--WellLog</c>).</summary>
    public string EntityType { get; set; } = string.Empty;

    /// <summary>The column as the mappings name it.</summary>
    public string Column { get; set; } = string.Empty;

    /// <summary>The routes by which the column reaches the records of the entity type, as JSON (<c>SearchRoute</c>).</summary>
    public string RoutesJson { get; set; } = "[]";

    /// <summary>The active delivery flows whose mappings read the column, as a JSON list of names.</summary>
    public string FlowsJson { get; set; } = "[]";

    public DateTime FirstSeenUtc { get; set; }

    public DateTime LastSeenUtc { get; set; }
}

/// <summary>
/// What a person made of a search term (osdu/docs/reference/concepts/search-terms.md): the name it is searched by,
/// whether it is left out of the search, which of its routes it is searched through, and a note on it, with who changed
/// it last and when. Kept by the term's identity, apart from the terms a sync writes, so it holds across every sync and
/// every mapping version that keeps the column; one whose term no mapping gives any longer stays until it is removed,
/// and applies again if the term comes back.
/// </summary>
public sealed class DeliverySearchTermRefinement
{
    /// <summary>The longest name a term is given, and the longest note on it.</summary>
    public const int MaxNameLength = 100;

    public const int MaxNoteLength = 1000;

    /// <summary>The longest route id: a template variable and how the value reaches it.</summary>
    public const int MaxRouteLength = 1100;

    /// <summary>The term refined (<see cref="DeliverySearchTerm.TermId"/>).</summary>
    public Guid TermId { get; set; }

    /// <summary>The term's key when it was refined, which names it while no mapping gives it.</summary>
    public string TermKey { get; set; } = string.Empty;

    /// <summary>The entity type the term searches, within which no two terms share a name.</summary>
    public string EntityType { get; set; } = string.Empty;

    /// <summary>The name the term is searched by; null to keep the column's own.</summary>
    public string? Name { get; set; }

    /// <summary>True when the term is left out of the search.</summary>
    public bool Excluded { get; set; }

    /// <summary>The route the term is searched through (<c>SearchRoute.Id</c>); null for the one the explorer prefers.</summary>
    public string? Route { get; set; }

    /// <summary>A note on the term, shown beside it wherever it is offered; null for none.</summary>
    public string? Note { get; set; }

    /// <summary>Who changed the refinement last.</summary>
    public string UpdatedBy { get; set; } = string.Empty;

    public DateTime UpdatedUtc { get; set; }
}

/// <summary>A mapping document as the sync found it in a repository: the read model behind the mappings page.</summary>
public sealed class DeliveryMapping
{
    /// <summary>Stable id: derived from the repo id and the mapping reference.</summary>
    public Guid Id { get; set; }

    public Guid RepoId { get; set; }

    /// <summary>Name@version.</summary>
    public string Reference { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    /// <summary>The OSDU kind of the template the mapping fills.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    /// The template version the mapping fills: the one it names, or the one the template folders on its path settle a kind
    /// named alone to. Kept for a document that fails to load when it names a version; empty when there is none.
    /// </summary>
    public string TemplateVersion { get; set; } = string.Empty;

    /// <summary>
    /// The file of the repository the template was settled from, in the template folder on the mapping's path that holds it
    /// (osdu/docs/reference/concepts/templates.md, Template folders); empty when no folder on its path holds it.
    /// </summary>
    public string TemplatePath { get; set; } = string.Empty;

    public string RelativePath { get; set; } = string.Empty;

    public string ContentHash { get; set; } = string.Empty;

    /// <summary>The document text, secret-redacted, as it was when synced.</summary>
    public string Yaml { get; set; } = string.Empty;

    /// <summary>A parsed summary (template, dataset system, key and label, child datasets, entry and fixture counts, envelope), for listings.</summary>
    public string SummaryJson { get; set; } = "{}";

    /// <summary>valid or invalid.</summary>
    public string Status { get; set; } = "valid";

    public string? Message { get; set; }

    public DateTime FirstSeenUtc { get; set; }

    public DateTime LastSeenUtc { get; set; }
}

/// <summary>
/// One flow of a repository that keeps a ledger and is no delivery flow (a retrieval, assertion, dimension, inventory or
/// download flow), as the repository sync found it (osdu/docs/reference/concepts/renaming-a-pipeline.md): the hash of its document,
/// by which the sync pairs a renamed flow with the one it replaced, and whether it keeps a ledger in each partition it works
/// in, which says the ledger identity it keeps in a partition. A flow keeps its ledger under its name, so the ledgers of a
/// flow are found by its kind and name in the directory (<see cref="DeliveryLedger"/>). The row of a flow the repository no
/// longer declares is kept, inactive, with the hash it last had, since the catalog keeps no copy of a flow that left.
/// </summary>
public sealed class DeliveryLedgerFlow
{
    /// <summary>The widest flow kind: <c>retrieval</c>, <c>assertion</c>, <c>dimension</c> or <c>inventory</c>.</summary>
    public const int MaxKindLength = 16;

    /// <summary>A stable id, derived from the repository and the flow's name.</summary>
    public Guid Id { get; set; }

    public Guid RepoId { get; set; }

    /// <summary>The flow, its pipeline in the repository.</summary>
    public string FlowName { get; set; } = string.Empty;

    /// <summary>The flow's kind, which is the kind of ledger it keeps.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    /// True when the flow keeps a ledger of its own in each partition it works in (<c>name@partition</c>, a flow that names
    /// its partitions or follows the registry), false when it keeps one ledger, in the partition its header names.
    /// </summary>
    public bool Partitioned { get; set; }

    /// <summary>SHA-256, in lowercase hex, of the flow document as parsed with its name and path left out.</summary>
    public string DocumentHash { get; set; } = string.Empty;

    public string RelativePath { get; set; } = string.Empty;

    /// <summary>False once the repository no longer declares the flow.</summary>
    public bool Active { get; set; } = true;

    public DateTime FirstSeenUtc { get; set; }

    public DateTime LastSeenUtc { get; set; }
}

/// <summary>
/// One interface of a delivery flow document, as the repository sync found it (osdu/docs/reference/flow/interfaces.md, The
/// interface read model): the pipeline it belongs to, the ledger identity its records, submissions and statistics are kept
/// under, and what it delivers and how. The API and the GUI find the pipeline of a ledger identity here, and the interfaces
/// of a pipeline, without parsing a document. A document in the single form has one row, whose interface name is empty. A
/// document that names its partitions has one row per interface and partition, each with the partition's ledger
/// (osdu/docs/reference/concepts/partitions.md, A ledger per partition). The row of an interface the repository no longer
/// declares is kept, inactive, so the records it delivered still lead to their flow.
/// </summary>
public sealed class DeliveryInterface
{
    /// <summary>The longest interface name.</summary>
    public const int MaxInterfaceLength = 64;

    /// <summary>
    /// Stable id: derived from the repository, the flow's name and the interface's name, and for a flow that names its
    /// partitions the partition's name as well.
    /// </summary>
    public Guid Id { get; set; }

    public Guid RepoId { get; set; }

    /// <summary>The flow's name: its pipeline in the repository.</summary>
    public string FlowName { get; set; } = string.Empty;

    /// <summary>The interface's name; empty for a document in the single form.</summary>
    public string Interface { get; set; } = string.Empty;

    /// <summary>The partition whose ledger the row describes; empty for a flow that names no partitions.</summary>
    public string Partition { get; set; } = string.Empty;

    /// <summary>
    /// The partition the interface's records reach, as the OSDU order groups deliveries (osdu/docs/reference/concepts/
    /// osdu-order.md, The order of a partition): the one its <c>target.headers</c> name, as the document writes it, or <c>*</c>
    /// for a source that leaves its partitions to the registry while none is registered. Empty when the headers name
    /// none the order can read, which leaves the interface out of the order, as lineage leaves it out.
    /// </summary>
    public string TargetPartition { get; set; } = string.Empty;

    /// <summary>Where the interface is in its document, from 0.</summary>
    public int Ordinal { get; set; }

    /// <summary>The ledger identity (the flow id every ledger row of the interface carries).</summary>
    public Guid LedgerFlowId { get; set; }

    /// <summary>The name the ledger identity is derived from: the flow, <c>flow/interface</c>, or the ledger it adopts.</summary>
    public string LedgerName { get; set; } = string.Empty;

    /// <summary>The route the interface is delivered by: storage, file, dataset, manifest, ddms, fileAndDdms, manifestAndDdms or workflow.</summary>
    public string Route { get; set; } = string.Empty;

    /// <summary>Why it goes by that route; null for the single form, whose document names its protocol.</summary>
    public string? RouteReason { get; set; }

    /// <summary>The mapping it pins (Name@version).</summary>
    public string MappingReference { get; set; } = string.Empty;

    /// <summary>
    /// The version of the partition's cache it renders against, as its <c>render.cacheVersion</c> says: <c>current</c>, or
    /// the label of the version it pins. The cache's retention keeps the records of every version an active interface pins.
    /// Null for a row the sync wrote before it recorded this: until the next sync records it, the retention of the partitions
    /// the interface may read prunes nothing, since it cannot tell which versions are pinned.
    /// </summary>
    public string? CacheVersion { get; set; }

    /// <summary>The OSDU kind the mapping fills, when the repository holds a valid mapping of that reference; empty otherwise.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The record table it reads.</summary>
    public string RecordObject { get; set; } = string.Empty;

    /// <summary>The interfaces of the document it waits for (<c>after:</c>), as a JSON array of names.</summary>
    public string AfterJson { get; set; } = "[]";

    public string RelativePath { get; set; } = string.Empty;

    /// <summary>
    /// SHA-256, in lowercase hex, of the flow document as parsed with its name and path left out, and of the mapping document
    /// it pins: the same for a pipeline renamed, so the repository sync moves the ledger of each of a pipeline's names to the
    /// one it found last. Null for a row written before it was recorded, until the control plane's backfill or the next sync
    /// records it.
    /// </summary>
    public string? DocumentHash { get; set; }

    /// <summary>False once the repository no longer declares the interface.</summary>
    public bool Active { get; set; } = true;

    public DateTime FirstSeenUtc { get; set; }

    public DateTime LastSeenUtc { get; set; }
}

/// <summary>
/// A file of a template folder of a repository, as the last sync found it (osdu/docs/reference/concepts/templates.md,
/// Template folders): which template it holds, or why it holds none. A template folder is a directory named
/// <c>templates</c>, and a document reads those on its path; the control plane's pages settle a document's template from
/// these rows by the same rule a run settles it by from the files, and the Templates page lists them. The row indexes the
/// file and holds none of it: whoever reads the template reads the file, in the folder the repository was synced into. A
/// file the repository no longer holds loses its row.
/// </summary>
public sealed class DeliveryRepositoryTemplate
{
    /// <summary>The longest path of a template file, or of its folder, in its repository.</summary>
    public const int MaxPathLength = 400;

    /// <summary>The longest reason a file holds no template.</summary>
    public const int MaxProblemLength = 2000;

    /// <summary>Stable id: derived from the repo id and the file's path.</summary>
    public Guid Id { get; set; }

    public Guid RepoId { get; set; }

    /// <summary>The file's path in the repository, with forward slashes (Welllog/templates/osdu_wks_work-product-component--WellLog_1.4.0.json).</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>The template folder the file is in, its path in the repository (Welllog/templates).</summary>
    public string Folder { get; set; } = string.Empty;

    /// <summary>The kind the file's schema declares (x-osdu-schema-source); empty when the file holds no template.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The template version the file holds, the hash of its canonical schema; empty when it holds no template.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Why the file holds no template; null when it holds one.</summary>
    public string? Problem { get; set; }

    /// <summary>
    /// The kind the template is linked to as its parent (x-osdu-inheriting-from-kind, osdu/docs/reference/concepts/templates.md,
    /// Linked templates); null for a template linked to none, and for a file that holds no template.
    /// </summary>
    public string? Parent { get; set; }

    public DateTime FirstSeenUtc { get; set; }

    public DateTime LastSeenUtc { get; set; }
}

/// <summary>
/// OSDU's lineage as one release of the OSDU data definitions declares it (osdu/docs/reference/concepts/osdu-order.md,
/// OSDU's lineage by release): which release and commit it was read from, when and by whom it was built, and how much it
/// holds. What each kind refers to is in <see cref="SchemaLineageKind"/>. A release is built once and kept; building it
/// again replaces its kinds.
/// </summary>
public sealed class SchemaLineage
{
    /// <summary>The longest release name a lineage keeps.</summary>
    public const int MaxReleaseLength = 100;

    public Guid Id { get; set; }

    /// <summary>The release's tag (<c>v0.29.0</c>).</summary>
    public string Release { get; set; } = string.Empty;

    /// <summary>The commit the release's tag names.</summary>
    public string Commit { get; set; } = string.Empty;

    /// <summary>When the release was published, when the repository says.</summary>
    public DateTime? PublishedUtc { get; set; }

    public DateTime BuiltUtc { get; set; }

    /// <summary>Who built it: a user, or the command line's local account.</summary>
    public string BuiltBy { get; set; } = string.Empty;

    /// <summary>How many kinds it holds.</summary>
    public int Kinds { get; set; }

    /// <summary>How many properties of those kinds refer to other records.</summary>
    public int Relationships { get; set; }

    /// <summary>The kinds the release lists that could not be read, each with why: a JSON array of <c>{ "kind", "reason" }</c>.</summary>
    public string FailuresJson { get; set; } = "[]";
}

/// <summary>
/// One kind of a release's lineage (<see cref="SchemaLineage"/>): its entity type and version, its status in the release, and
/// every property of its schema that refers to other records, with the entity types each names: a JSON array of
/// <c>{ "property", "targets": [ ... ] }</c>, as the OSDU order reads a schema.
/// </summary>
public sealed class SchemaLineageKind
{
    public Guid LineageId { get; set; }

    /// <summary>The kind (<c>osdu:wks:master-data--Wellbore:1.3.0</c>).</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The entity type it is of (<c>master-data--Wellbore</c>).</summary>
    public string EntityType { get; set; } = string.Empty;

    /// <summary>The kind's version (<c>1.3.0</c>).</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>The kind's status in the release (<c>PUBLISHED</c>, <c>DEVELOPMENT</c>), when the release says.</summary>
    public string? Status { get; set; }

    /// <summary>The properties that refer to other records, each with the entity types its relationship names.</summary>
    public string RelationshipsJson { get; set; } = "[]";
}

/// <summary>
/// The team a folder of a synced repository belongs to: the <c>team</c> its repository document
/// (<c>osdu-delivery.yaml</c>, osdu/docs/reference/flow/repository.md) names, written by every sync of the repository. A
/// flow belongs to the team of the nearest document in its folder or a folder above it, and the work a node does for the
/// flow is charged to that team (<see cref="DeliveryComputeUsage.Team"/>). A document that names no team has no row.
/// </summary>
public sealed class DeliveryRepositoryTeam
{
    /// <summary>The longest folder path a row keeps.</summary>
    public const int MaxFolderLength = 400;

    public Guid RepoId { get; set; }

    /// <summary>The folder holding the document, relative to the repository's root with forward slashes; empty for the root.</summary>
    public string Folder { get; set; } = string.Empty;

    /// <summary>The team the document names.</summary>
    public string Team { get; set; } = string.Empty;

    /// <summary>When a sync last wrote the row.</summary>
    public DateTime UpdatedUtc { get; set; }
}
