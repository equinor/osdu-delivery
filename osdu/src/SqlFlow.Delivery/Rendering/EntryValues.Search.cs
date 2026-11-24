using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Search;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Rendering;

// A search source (osdu/docs/reference/flow/mapping-lookups.md, searches and $search): each findBy line in turn asks
// the platform for the one record whose property is the line's value, and the first line that finds exactly one wins;
// where the partition's indexer keeps a lowercased copy of text, the value is asked for regardless of case.
internal static partial class EntryValues
{
    /// <summary>
    /// Resolves a search source: each findBy line in turn asks the platform for the one record whose property is exactly
    /// the line's value, and the first line that finds exactly one wins. A line the run has not asked yet stops the render
    /// there, naming the question, because what the later lines are asked depends on what this one finds. A value that is
    /// already an OSDU id of the kind searched names its record without a search.
    /// </summary>
    /// <remarks>
    /// Doubt holds the record whatever the entry's required flag says: a value several records answer to, a query the
    /// platform refused, or a value that could not be asked for on a line when no other line found the record. Each of
    /// those says nothing about which record was meant, and delivering the record without the reference, or with one
    /// picked from several, would put a wrong document into OSDU with nothing to show it was a guess. Only a value every
    /// line asked for and found nothing for is a clean miss, which an optional entry leaves out as a cache miss does.
    /// </remarks>
    private static object? Searched(
        MappingEntry entry, SourceRow root, SourceRow? item, MappingRenderer renderer, List<string> holds, List<CacheUsage> usages, RenderTrail searched)
    {
        var path = entry.Target.Text;
        var name = entry.Source!.CacheType!;
        var outcomes = new List<string>();
        var tries = new List<RenderMissTry>();
        var emptied = new List<EmptiedValue>();
        var unaskable = false;
        foreach (var find in entry.FindBy)
        {
            string? value;
            object? raw = null;
            if (find.Literal is not null)
            {
                // A quoted text reads the mapping's parameters, as every literal does: '{$param.dataPartition}:...'.
                value = MappingEntry.ExpandParameters(find.Literal, renderer.ParameterValue);
            }
            else
            {
                raw = Read(find.Column!, root, item);
                if (!TryModify(entry, raw, root, item, renderer, holds, usages, searched, out var modified))
                {
                    return null;
                }

                value = modified is bool flag ? (flag ? "true" : "false") : SourceRow.Stringify(modified);
            }

            value = value?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                if (find.Column is { } empty)
                {
                    emptied.Add(Emptied(empty, raw));
                }

                continue;
            }

            if (!renderer.Searches.TryField(name, find.Field, out var search, out var field))
            {
                holds.Add(
                    $"{path}: search.{name}.{find.Field} has not been resolved against the schema search '{name}' pins, so no query can be written for it; the mapping's preflight says why");
                return null;
            }

            var kind = search!.Declaration.Kind;

            // A value that already is an OSDU id names its record, so nothing is asked; an id of another entity type
            // would be a reference to the wrong kind of record, which no search would have returned.
            if (OsduId().IsMatch(value))
            {
                var entityType = value.Split(':')[1];
                var searchedType = kind.Split(':')[2];
                if (string.Equals(entityType, searchedType, StringComparison.Ordinal))
                {
                    return AsReference(value);
                }

                holds.Add($"{path}: '{value}' is already an OSDU id, of a {entityType} record, and search.{name} looks for {searchedType} records ({kind})");
                return null;
            }

            OsduQuery query;
            try
            {
                query = OsduQuery.Equal(field!, value);
            }
            catch (OsduQueryException ex)
            {
                // The value cannot be asked for on this line; a later line may still find the record, and if none does the
                // record is held, since nobody knows whether the platform holds it.
                unaskable = true;
                outcomes.Add($"{find.Field} '{value}' cannot be searched for: {ex.Message}");
                continue;
            }

            var question = new SearchQuestion(kind, find.Field, value, query.Text);
            if (!renderer.Search.TryAnswer(question, out var answer))
            {
                // Not asked yet. The render stops here rather than guessing, and the caller asks and renders again.
                searched.Ask(question);
                return null;
            }

            searched.Use(question, answer);
            switch (answer.Outcome)
            {
                case SearchOutcome.Found:
                    return WithVersionSeparator(answer.Id!);
                case SearchOutcome.NotFound:
                    break;
                default:
                    holds.Add(
                        $"{path}: searching {kind} for {find.Field} '{value}' {answer.Describe()}; a reference picked from several, or taken without an answer, would deliver a wrong document, so the record is held. Make the incoming value name one record.");
                    return null;
            }

            // Nothing holds the value exactly. Where the partition's indexer keeps a lowercased copy of text, the same value
            // is asked for regardless of case, and taken only when that finds exactly one record, which is the rule a cache
            // lookup keeps: codes that differ only by case are different records, so several answers select none of them.
            if (Caseless(renderer, field!, value) is not { } loose)
            {
                outcomes.Add($"{find.Field} '{value}' {answer.Describe()}");
                tries.Add(new RenderMissTry(find.Field, find.Column, value));
                continue;
            }

            var looseQuestion = new SearchQuestion(kind, find.Field, value, loose.Text);
            if (!renderer.Search.TryAnswer(looseQuestion, out var looseAnswer))
            {
                searched.Ask(looseQuestion);
                return null;
            }

            searched.Use(looseQuestion, looseAnswer);
            switch (looseAnswer.Outcome)
            {
                case SearchOutcome.Found:
                    return WithVersionSeparator(looseAnswer.Id!);
                case SearchOutcome.NotFound:
                    outcomes.Add($"{find.Field} '{value}' found no record, exactly or once case is ignored");
                    tries.Add(new RenderMissTry(find.Field, find.Column, value));
                    continue;
                case SearchOutcome.Refused:
                    holds.Add(
                        $"{path}: searching {kind} for {find.Field} '{value}' found no record exactly, and once case is ignored it {looseAnswer.Describe()}; a reference taken without an answer would deliver a wrong document, so the record is held.");
                    return null;
                default:
                    holds.Add(
                        $"{path}: searching {kind} for {find.Field} '{value}' found no record exactly, and once case is ignored it {looseAnswer.Describe()}; a reference picked from several would deliver a wrong document, so the record is held. Make the incoming value exact with a replace modifier.");
                    return null;
            }
        }

        var searchedKind = renderer.Searches.Searches.TryGetValue(name, out var declared) ? declared.Declaration.Kind : name;
        if (outcomes.Count == 0)
        {
            if (entry.Required || searched.NotesMisses)
            {
                var nothing = $"{path}: {EmptiedText(emptied, entry)}, so there is nothing to search for";
                var blank = emptied.Where(e => e.Held is null).Select(e => e.Column).Distinct().ToList();
                if (blank.Count > 0)
                {
                    searched.Miss(new RenderMiss { Kind = RenderMissKind.Empty, Reason = nothing, Columns = blank, Types = [name], EntityType = searchedKind });
                }

                if (entry.Required)
                {
                    holds.Add($"{nothing}, and the entry is required");
                }
            }

            return null;
        }

        var reason = $"{path}: no {name} on the platform ({searchedKind}) matches: {string.Join("; ", outcomes)}";
        if (unaskable)
        {
            holds.Add(reason + ". A value that cannot be searched for proves nothing about whether the record exists, so the record is held whatever the entry's required flag says");
            return null;
        }

        return Missing(
            entry,
            new RenderMiss
            {
                Kind = RenderMissKind.NotFound,
                Reason = reason,
                Columns = tries.Select(t => t.Column).OfType<DatasetColumn>().Distinct().ToList(),
                Types = [name],
                EntityType = searchedKind,
                Tries = tries,
            },
            holds,
            searched);
    }

    /// <summary>
    /// The query that asks for <paramref name="value"/> regardless of case, or null when there is none to ask: the system
    /// properties the render is pinned to do not say the partition's indexer keeps a lowercased copy of text, the property
    /// is not text, or the value cannot be asked for that way. Only a system property known to be on changes how a lookup
    /// is written, so a partition whose settings were never read is asked exact questions alone.
    /// </summary>
    private static OsduQuery? Caseless(MappingRenderer renderer, OsduField field, string value)
    {
        if (!renderer.Context.KeywordLower || field.Index != OsduFieldIndex.Text)
        {
            return null;
        }

        try
        {
            return OsduQuery.Equal(field, value, caseInsensitive: true);
        }
        catch (OsduQueryException)
        {
            // The exact question was asked and answered; the only value the lowercased copy refuses beyond it is a
            // spelling of the text null, which that copy holds for every record with no value.
            return null;
        }
    }
}
