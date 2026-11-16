namespace SqlFlow.Core.Lineage;

/// <summary>
/// A lineage description (<c>flowType: lin</c>): one step a system SQLFlow does not run, written down so lineage can be
/// followed past the estate's own flows, back to the source a file was made from or on to what consumes a table. It is
/// metadata only. A description is never executed and supports no execution: it declares no connection, takes no run
/// parameter, joins no schedule and has no place in the execution plan. What it reads and writes is kept apart from the
/// lineage SQLFlow computes from the flows it runs (<see cref="LineageRelation.DescribedReads"/> and
/// <see cref="LineageRelation.DescribedWrites"/>), so that lineage is the same with or without descriptions.
/// </summary>
public sealed record LineageDescriptionFlow
{
    /// <summary>The <c>flowType</c> value of a lineage description.</summary>
    public const string FlowType = "lin";

    /// <summary>The longest name a description may carry: the width the catalog keeps a pipeline's name in.</summary>
    public const int MaxNameLength = 400;

    /// <summary>The longest <see cref="Description"/>.</summary>
    public const int MaxDescriptionLength = 1000;

    /// <summary>The longest <see cref="Batch"/>: the width the catalog keeps a pipeline's batch in.</summary>
    public const int MaxBatchLength = 250;

    /// <summary>The longest system name (<see cref="System"/>, <see cref="LineageDescriptionEndpoint.System"/>).</summary>
    public const int MaxSystemLength = 32;

    /// <summary>The longest <see cref="Unit"/>.</summary>
    public const int MaxUnitLength = 512;

    /// <summary>The longest <see cref="Owner"/>.</summary>
    public const int MaxOwnerLength = 256;

    /// <summary>The longest <see cref="Url"/>.</summary>
    public const int MaxUrlLength = 2048;

    /// <summary>The longest <see cref="Context"/>.</summary>
    public const int MaxContextLength = 20000;

    /// <summary>The most entries <see cref="Evidence"/> may hold.</summary>
    public const int MaxEvidence = 50;

    /// <summary>The most entries <see cref="Reads"/>, and <see cref="Writes"/>, may hold.</summary>
    public const int MaxEndpoints = 200;

    /// <summary>The longest object name (<see cref="LineageDescriptionEndpoint.Name"/>): the width the catalog keeps an
    /// object's name in.</summary>
    public const int MaxObjectNameLength = 512;

    /// <summary>The longest alias (<see cref="LineageDescriptionEndpoint.Alias"/>).</summary>
    public const int MaxAliasLength = 64;

    /// <summary>The longest note (on an endpoint, a column or an evidence entry).</summary>
    public const int MaxNoteLength = 2000;

    /// <summary>The longest <see cref="LineageDescriptionEndpoint.At"/>.</summary>
    public const int MaxAtLength = 512;

    /// <summary>The most columns one written object may map.</summary>
    public const int MaxColumns = 2000;

    /// <summary>The most source columns one mapped column may name.</summary>
    public const int MaxColumnSources = 50;

    /// <summary>The most columns one description may map over all the objects it writes. The limits above bound one
    /// object and one column; this one bounds the document, however its entries are written (an anchor repeated under
    /// every write counts each time).</summary>
    public const int MaxDocumentColumns = 5000;

    /// <summary>The most source columns one description may name over all its mapped columns.</summary>
    public const int MaxDocumentColumnSources = 20000;

    /// <summary>The longest column name, on either side of a mapping.</summary>
    public const int MaxColumnNameLength = 512;

    /// <summary>The longest <see cref="LineageDescriptionEvidence.Repo"/>.</summary>
    public const int MaxRepoLength = 512;

    /// <summary>The longest <see cref="LineageDescriptionEvidence.Path"/>.</summary>
    public const int MaxPathLength = 1024;

    /// <summary>The longest <see cref="LineageDescriptionEvidence.Commit"/>.</summary>
    public const int MaxCommitLength = 64;

    /// <summary>The description's name: its identity in the estate and the flow its connections are attributed to.</summary>
    public required string Name { get; init; }

    /// <summary>What the step does, in a sentence.</summary>
    public string? Description { get; init; }

    /// <summary>A grouping label, as on every flow. It has no effect beyond grouping.</summary>
    public string? Batch { get; init; }

    /// <summary>The system that performs the step (a data platform, an orchestrator, a script, a person): lower-case
    /// letters, digits and hyphens, starting with a letter. It is also the system of every object the description names
    /// without one of its own.</summary>
    public required string System { get; init; }

    /// <summary>Where the step is found in that system (a job and task, a pipeline and activity, a notebook), as a
    /// person would look it up. Free text.</summary>
    public string? Unit { get; init; }

    /// <summary>Who to ask about the step.</summary>
    public string? Owner { get; init; }

    /// <summary>Where the step can be looked at. Shown as written, never fetched.</summary>
    public string? Url { get; init; }

    /// <summary>What the description was written from: the code or the definitions of the step, so a reader can check a
    /// statement and tell an old description from a current one.</summary>
    public IReadOnlyList<LineageDescriptionEvidence> Evidence { get; init; } = [];

    /// <summary>What the step reads, in declaration order.</summary>
    public IReadOnlyList<LineageDescriptionEndpoint> Reads { get; init; } = [];

    /// <summary>What the step writes, in declaration order.</summary>
    public IReadOnlyList<LineageDescriptionEndpoint> Writes { get; init; } = [];

    /// <summary>What happens to the data in the step, in prose: which rows are left out or added and why, how keys are
    /// formed, when it runs, what becomes of a deleted record. Read by people and by an assistant, never parsed.</summary>
    public string? Context { get; init; }
}

/// <summary>One thing a lineage description was written from.</summary>
public sealed record LineageDescriptionEvidence
{
    /// <summary>The repository holding it: a name or an address without credentials.</summary>
    public string? Repo { get; init; }

    /// <summary>The file or folder, relative to the repository.</summary>
    public required string Path { get; init; }

    /// <summary>The commit the description was written from.</summary>
    public string? Commit { get; init; }

    /// <summary>What the entry holds (the job definition, the transformation, the configuration).</summary>
    public string? Note { get; init; }
}

/// <summary>
/// One object a lineage description reads or writes. It is named in one of two ways. A system and a name give an object
/// the estate does not manage, known only from descriptions. A flow gives an object the estate already holds: what that
/// flow reads (a written object that feeds it) or what it writes (a read object fed by it), so a description meets the
/// estate's own lineage on the node the flow already declares, without restating a location.
/// </summary>
public sealed record LineageDescriptionEndpoint
{
    /// <summary>The object's system, when the object is named by a system and a name: the description's own system unless
    /// the entry names another. Null when the object is named by a flow.</summary>
    public string? System { get; init; }

    /// <summary>The object's name as its system writes it, without anything that differs between environments. A
    /// <c>*</c> stands for a family of objects named alike. Null when the object is named by a flow.</summary>
    public string? Name { get; init; }

    /// <summary>The flow of the estate whose object this is: the flow a written object feeds, or the flow a read object
    /// is fed by. Null when the object is named by a system and a name.</summary>
    public string? Flow { get; init; }

    /// <summary>Which of the flow's objects is meant, when it reads (or writes) more than one: a part of that object's
    /// name. Null when the flow has one.</summary>
    public string? At { get; init; }

    /// <summary>The short name a column mapping refers to a read object by. Null when the entry declares none.</summary>
    public string? Alias { get; init; }

    /// <summary>What the object holds, in a sentence or two.</summary>
    public string? Note { get; init; }

    /// <summary>True for a read object that is where the data starts: nothing upstream of it is expected to be
    /// described.</summary>
    public bool Origin { get; init; }

    /// <summary>The column mappings of a written object, in declaration order. Optional and possibly partial: a column
    /// that is not listed is not mapped, which says nothing about whether it exists.</summary>
    public IReadOnlyList<LineageDescriptionColumn> Columns { get; init; } = [];

    /// <summary>True when the object is named by a flow of the estate.</summary>
    public bool NamesFlow => Flow is not null;
}

/// <summary>One column of a written object, and the columns it comes from.</summary>
public sealed record LineageDescriptionColumn
{
    /// <summary>The column's name. A <c>*</c> stands for every column not listed by name.</summary>
    public required string Name { get; init; }

    /// <summary>The columns it comes from, in declaration order; empty for a column the step makes itself.</summary>
    public IReadOnlyList<LineageDescriptionColumnSource> From { get; init; } = [];

    /// <summary>How the value is made, in words or as an expression.</summary>
    public string? Note { get; init; }
}

/// <summary>One source column of a mapped column.</summary>
/// <param name="Alias">The alias of the read object holding the column.</param>
/// <param name="Column">The column's name there. A <c>*</c> stands for the column of the same name.</param>
public sealed record LineageDescriptionColumnSource(string Alias, string Column);
