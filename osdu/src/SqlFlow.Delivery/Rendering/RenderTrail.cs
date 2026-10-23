using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Rendering;

/// <summary>
/// What one render met on its way: the questions it asked of the platform that the run could not answer yet, the answers
/// it used, the parent it read, the children it looked for, and which alternative each <c>$coalesce</c> node took its value
/// from. It belongs to one render, never to the renderer, which the plan's parallel workers share.
/// </summary>
internal sealed class RenderTrail
{
    private List<PlatformQuestion>? _unanswered;
    private List<SearchUsage>? _used;
    private List<ChildUsage>? _children;
    private Dictionary<(string Target, int Alternative), CoalesceChoice>? _chosen;

    /// <summary>
    /// Where what the mapping's assertions find of this record is collected (osdu/docs/reference/flow/mapping-assertions.md),
    /// or null when the mapping states none or the evaluation is a second look that records nothing (why a node is empty).
    /// </summary>
    public Validation.AssertionLog? Assertions { get; init; }

    /// <summary>
    /// Which row of its child dataset the node being evaluated reads, counting from zero, while the items of a repeated array
    /// are evaluated; null for the dataset's own row. An incoming assertion names the row it judged by it.
    /// </summary>
    public int? Item { get; set; }

    /// <summary>The questions to ask before the row is rendered again, each once, in the order the render met them.</summary>
    public IReadOnlyList<PlatformQuestion> Unanswered => _unanswered ?? (IReadOnlyList<PlatformQuestion>)[];

    /// <summary>The record's parent as the render read it (osdu/docs/reference/flow/mapping-parent.md); null when it read none.</summary>
    public ParentUsage? Parent { get; private set; }

    /// <summary>The record's children as the render looked for them (osdu/docs/reference/flow/mapping-child.md), each once, in the order it met them.</summary>
    public IReadOnlyList<ChildUsage> Children => _children ?? (IReadOnlyList<ChildUsage>)[];

    /// <summary>The answers the render consulted, each once, in the order it consulted them.</summary>
    public IReadOnlyList<SearchUsage> Used => _used ?? (IReadOnlyList<SearchUsage>)[];

    /// <summary>
    /// The alternative each <c>$coalesce</c> node took its value from, once per node and alternative, in the order the
    /// render met them, with how many values it gave: an item of a repeated array counts once each.
    /// </summary>
    public IReadOnlyList<CoalesceChoice> Chosen => _chosen is null ? [] : [.. _chosen.Values];

    public void Ask(PlatformQuestion question)
    {
        _unanswered ??= [];
        if (!_unanswered.Contains(question))
        {
            _unanswered.Add(question);
        }
    }

    public void Use(SearchQuestion question, SearchAnswer answer)
    {
        var usage = new SearchUsage(question.Kind, question.Field, question.Value, question.Query, answer.Outcome, answer.Id);
        _used ??= [];
        if (!_used.Contains(usage))
        {
            _used.Add(usage);
        }
    }

    /// <summary>Notes the parent <paramref name="question"/> asked for as <paramref name="answer"/> gives it.</summary>
    public void Read(ParentQuestion question, ParentAnswer answer)
        => Parent ??= new ParentUsage(question.Id, question.Kind, answer.Found, answer.Version);

    /// <summary>Notes the child <paramref name="question"/> looked for as <paramref name="answer"/> gives it.</summary>
    public void Link(ChildQuestion question, ChildAnswer answer)
    {
        var usage = new ChildUsage(question.Id, question.Kind, answer.Found, answer.Version);
        _children ??= [];
        if (!_children.Contains(usage))
        {
            _children.Add(usage);
        }
    }

    /// <summary>Notes that the node filling <paramref name="target"/> took its value from its alternative number <paramref name="alternative"/>.</summary>
    public void Choose(string target, int alternative, int of, string origin, bool unverified)
    {
        _chosen ??= [];
        var key = (target, alternative);
        _chosen[key] = _chosen.TryGetValue(key, out var seen)
            ? seen with { Values = seen.Values + 1, Unverified = seen.Unverified || unverified }
            : new CoalesceChoice(target, alternative, of, origin, 1, unverified);
    }
}
