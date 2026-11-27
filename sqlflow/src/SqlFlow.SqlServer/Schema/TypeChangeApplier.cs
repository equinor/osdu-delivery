using System.Globalization;
using Microsoft.Data.SqlClient;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Model;

namespace SqlFlow.SqlServer.Schema;

/// <summary>The opt-ins a flow gives schema sync for changing the type of a column that already exists.</summary>
/// <param name="AllowTableRewrite">A widening that rewrites the table may run (<c>schema.allowTableRewrite</c>).</param>
/// <param name="ConvertTypes">A checked conversion may run (<c>schema.convertTypes</c>).</param>
public sealed record TypeChangePolicy(bool AllowTableRewrite, bool ConvertTypes)
{
    public bool Permits(ColumnTypeChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        return change.RequiresOptIn switch
        {
            null => true,
            ColumnTypeChange.AllowTableRewrite => AllowTableRewrite,
            ColumnTypeChange.ConvertTypes => ConvertTypes,
            _ => false,
        };
    }
}

/// <summary>
/// Carries out and reports the type changes schema sync planned for one target: finishes or rolls back a safe rewrite an
/// earlier run left, applies the shadow rewrites the flow opted into (after checking a checked conversion's data), and
/// describes every change, applied or not, as a <see cref="TypeChangeReport"/>. The in-place part (one ALTER COLUMN in
/// metadata) runs with the rest of schema sync's DDL in <see cref="SchemaSyncService.EvolveAsync"/>. <see cref="PlanAsync"/>
/// says what a run would do about each change, through the same decisions and wording, and applies nothing.
/// </summary>
public static class TypeChangeApplier
{
    /// <summary>Finishes the rewrites an earlier run left on the table when this run still wants exactly that change and
    /// is allowed it, and rolls the others back. Returns what was done.</summary>
    public static async Task<IReadOnlyList<TypeChangeReport>> ResumeAsync(
        SchemaSyncService schemaSync, string connectionString, RelationalObject target, IReadOnlyList<SqlColumn> desired,
        IReadOnlySet<string> keyColumns, TypeChangePolicy policy, RetypeOptions options, Action<string, string> trace, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(schemaSync);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(trace);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        var pending = await ColumnRetyper.FindPendingAsync(connection, target, ct).ConfigureAwait(false);
        if (pending.Count == 0)
        {
            return [];
        }

        var context = new RetypeContext(connection, target, options, trace);
        var plan = await schemaSync.PlanAsync(connection, target, desired, keyColumns, ct).ConfigureAwait(false);
        var dependencies = await TableDependencyReader.ReadAsync(connection, target, ct).ConfigureAwait(false);
        var reports = new List<TypeChangeReport>();
        foreach (var left in pending)
        {
            var change = Resumable(plan, left);
            if (change is null || !policy.Permits(change))
            {
                await ColumnRetyper.RollBackAsync(context, left, ct).ConfigureAwait(false);
                reports.Add(RolledBack(target, plan, left, change, planned: false));
                continue;
            }

            if (!left.TriggerIntact)
            {
                // Writes may have reached the column without reaching the shadow: start over from a clean table.
                await ColumnRetyper.RollBackAsync(context, left, ct).ConfigureAwait(false);
                reports.Add(await ApplyOneAsync(context, target, change, dependencies, resume: false, ct).ConfigureAwait(false));
                continue;
            }

            var report = await ApplyOneAsync(context, target, change, dependencies, resume: true, ct).ConfigureAwait(false);
            reports.Add(report.Status == TypeChangeStatus.Applied
                ? report with
                {
                    Status = TypeChangeStatus.Resumed,
                    Message = $"An earlier run's conversion of {target.QualifiedName}.[{change.Column}] to {change.ToType.Render()} was finished. " + report.Message,
                }
                : report);
        }

        return reports;
    }

    /// <summary>
    /// Applies the shadow rewrites of <paramref name="timing"/> the flow opted into, each after its data check, and
    /// reports every shadow rewrite of that timing (applied, pending an opt-in, or refused by the check).
    /// </summary>
    public static async Task<IReadOnlyList<TypeChangeReport>> ApplyAsync(
        SchemaSyncService schemaSync, string connectionString, RelationalObject target, IReadOnlyList<SqlColumn> desired,
        IReadOnlySet<string> keyColumns, TypeChangeTiming timing, TypeChangePolicy policy, RetypeOptions options,
        Action<string, string> trace, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(schemaSync);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(policy);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        var plan = await schemaSync.PlanAsync(connection, target, desired, keyColumns, ct).ConfigureAwait(false);
        var shadow = plan.TypeChanges.Where(c => c.Mechanism == TypeChangeMechanism.ShadowRewrite && c.Timing == timing).ToList();
        if (shadow.Count == 0)
        {
            return [];
        }

        var context = new RetypeContext(connection, target, options, trace);
        var dependencies = await TableDependencyReader.ReadAsync(connection, target, ct).ConfigureAwait(false);
        var reports = new List<TypeChangeReport>();
        foreach (var change in shadow)
        {
            if (!policy.Permits(change))
            {
                reports.Add(NotPermitted(change, target, dependencies.RowCount));
                continue;
            }

            if (change.Tier == TypeChangeTier.CheckedConversion)
            {
                var check = await ColumnRetyper.CheckAsync(context, change, ct).ConfigureAwait(false);
                if (check.Failing > 0)
                {
                    reports.Add(Refused(change, target, check, planned: false));
                    continue;
                }
            }

            reports.Add(await ApplyOneAsync(context, target, change, dependencies, resume: false, ct).ConfigureAwait(false));
            dependencies = await TableDependencyReader.ReadAsync(connection, target, ct).ConfigureAwait(false);
        }

        return reports;
    }

    /// <summary>Describes the changes schema sync did not hand to the safe rewrite: the in-place ones (applied, or
    /// pending <c>schema.allowTableRewrite</c> when they rewrite) and the tier 3 ones, with the merging keys of a key
    /// column change counted. <paramref name="appliedInPlace"/> names the columns the DDL batch altered.</summary>
    public static async Task<IReadOnlyList<TypeChangeReport>> DescribeAsync(
        string connectionString, RelationalObject target, EvolutionPlan plan, IReadOnlySet<string> appliedInPlace,
        RetypeOptions options, Action<string, string> trace, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(appliedInPlace);

        var described = plan.TypeChanges.Where(c => c.Mechanism != TypeChangeMechanism.ShadowRewrite).ToList();
        if (described.Count == 0)
        {
            return [];
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        var context = new RetypeContext(connection, target, options, trace);
        var rows = (await TableDependencyReader.ReadAsync(connection, target, ct).ConfigureAwait(false)).RowCount;
        var reports = new List<TypeChangeReport>();
        foreach (var change in described)
        {
            if (change.Mechanism is TypeChangeMechanism.InPlace or TypeChangeMechanism.InPlaceRewrite)
            {
                reports.Add(appliedInPlace.Contains(change.Column)
                    ? TypeChangeReports.From(change, TypeChangeReport.TargetLayer, target.QualifiedName, TypeChangeStatus.Applied,
                        InPlace(change, target, planned: false), rows)
                    : NotPermitted(change, target, rows));
                continue;
            }

            reports.Add(await TierThreeAsync(context, target, change, rows, ct).ConfigureAwait(false));
        }

        return reports;
    }

    /// <summary>
    /// What a run with <paramref name="policy"/> would do about every type change of <paramref name="plan"/> and every
    /// rewrite an earlier run left on the table, reported in the order a run reports them (the unfinished rewrites, the
    /// in-place and tier 3 changes, then the safe rewrites before and after the load), applying nothing. Only the reads a
    /// run makes before it decides run over <paramref name="connection"/>: the unfinished rewrites, the table's facts, each
    /// checked conversion's data check (<see cref="ColumnRetyper.CheckAsync"/>) and a key change's merging keys. A change
    /// the run would apply is <see cref="TypeChangeStatus.Planned"/>, an unfinished rewrite it would roll back
    /// <see cref="TypeChangeStatus.RolledBack"/>; every other change carries the status and message the run gives it.
    /// <paramref name="appliedInPlace"/> names the columns the run's DDL batch would alter
    /// (<see cref="SchemaSyncService.Permitted"/>). The plan must be of an existing table.
    /// </summary>
    public static async Task<IReadOnlyList<TypeChangeReport>> PlanAsync(
        SqlConnection connection, RelationalObject target, EvolutionPlan plan, IReadOnlySet<string> appliedInPlace,
        TypeChangePolicy policy, RetypeOptions options, Action<string, string> trace, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(appliedInPlace);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(trace);
        if (plan.CreateTable)
        {
            throw new ArgumentException($"{target.QualifiedName} does not exist yet: a run creates it, so it has no type change to plan.", nameof(plan));
        }

        var context = new RetypeContext(connection, target, options, trace);
        var rows = (await TableDependencyReader.ReadAsync(connection, target, ct).ConfigureAwait(false)).RowCount;
        var reports = new List<TypeChangeReport>();

        // As ResumeAsync, first: a rewrite an earlier run left is finished when the run still wants that change and may make
        // it, and rolled back otherwise. A finished one is the change itself, so it is not planned a second time below.
        var finished = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var left in await ColumnRetyper.FindPendingAsync(connection, target, ct).ConfigureAwait(false))
        {
            var change = Resumable(plan, left);
            if (change is null || !policy.Permits(change))
            {
                reports.Add(RolledBack(target, plan, left, change, planned: true));
                continue;
            }

            finished.Add(change.Column);
            reports.Add(PlannedResume(target, change, left, rows));
        }

        // As DescribeAsync: the in-place changes the DDL batch would carry (or leave pending an opt-in), and the tier 3 ones.
        foreach (var change in plan.TypeChanges.Where(c => c.Mechanism != TypeChangeMechanism.ShadowRewrite && !finished.Contains(c.Column)))
        {
            if (change.Mechanism is TypeChangeMechanism.InPlace or TypeChangeMechanism.InPlaceRewrite)
            {
                reports.Add(appliedInPlace.Contains(change.Column)
                    ? TypeChangeReports.From(change, TypeChangeReport.TargetLayer, target.QualifiedName, TypeChangeStatus.Planned,
                        InPlace(change, target, planned: true), rows)
                    : NotPermitted(change, target, rows));
                continue;
            }

            reports.Add(await TierThreeAsync(context, target, change, rows, ct).ConfigureAwait(false));
        }

        // As ApplyAsync, before the load and then after it: each safe rewrite the flow allows, a checked conversion only once
        // every stored value is shown to convert.
        foreach (var timing in new[] { TypeChangeTiming.BeforeLoad, TypeChangeTiming.AfterLoad })
        {
            foreach (var change in plan.TypeChanges.Where(c =>
                         c.Mechanism == TypeChangeMechanism.ShadowRewrite && c.Timing == timing && !finished.Contains(c.Column)))
            {
                if (!policy.Permits(change))
                {
                    reports.Add(NotPermitted(change, target, rows));
                    continue;
                }

                StoredValueCheck? check = null;
                if (change.Tier == TypeChangeTier.CheckedConversion)
                {
                    check = await ColumnRetyper.CheckAsync(context, change, ct).ConfigureAwait(false);
                    if (check.Failing > 0)
                    {
                        reports.Add(Refused(change, target, check, planned: true));
                        continue;
                    }
                }

                reports.Add(PlannedRewrite(change, target, rows, check));
            }
        }

        return reports;
    }

    /// <summary>A report's message without the manual steps a tier 3 message ends with, for a reader that lists
    /// <see cref="TypeChangeReport.ManualSteps"/> on their own.</summary>
    public static string WithoutManualSteps(TypeChangeReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var steps = ManualStepsSuffix(report.ManualSteps);
        return report.Status == TypeChangeStatus.Manual && report.Message.EndsWith(steps, StringComparison.Ordinal)
            ? report.Message[..^steps.Length]
            : report.Message;
    }

    private static async Task<TypeChangeReport> ApplyOneAsync(
        RetypeContext context, RelationalObject target, ColumnTypeChange change, TableDependencies dependencies, bool resume, CancellationToken ct)
    {
        try
        {
            await ColumnRetyper.ApplyAsync(context, change, dependencies, resume, ct).ConfigureAwait(false);
        }
        catch (SqlException ex) when (ex.Number is 1505 or 2601 or 2627)
        {
            // A unique index over the new type found two rows equal: the conversion merges values the index keeps apart.
            await RollBackAsync(context, change, ct).ConfigureAwait(false);
            return TypeChangeReports.From(change, TypeChangeReport.TargetLayer, target.QualifiedName, TypeChangeStatus.Refused,
                $"{change.Describe(target.QualifiedName)}: refused and rolled back, the table is as it was. A unique index over [{change.Column}] " +
                $"finds duplicate values once they are {change.ToType.Render()}: {SecretHygiene.RedactedMessage(ex)}",
                dependencies.RowCount);
        }

        var failedViews = await ColumnRetyper.RefreshViewsAsync(context, dependencies, ct).ConfigureAwait(false);
        var message = $"{change.Describe(target.QualifiedName)}: applied by the safe rewrite; {KeptByRewrite} were kept. {Capitalize(change.Reason)}. " +
                      TypeChangeReport.Downstream;
        if (failedViews.Count > 0)
        {
            message += " These views over the table could not be refreshed and need attention: " + string.Join("; ", failedViews) + ".";
        }

        return TypeChangeReports.From(change, TypeChangeReport.TargetLayer, target.QualifiedName, TypeChangeStatus.Applied, message, dependencies.RowCount);
    }

    private static async Task RollBackAsync(RetypeContext context, ColumnTypeChange change, CancellationToken ct)
    {
        var left = (await ColumnRetyper.FindPendingAsync(context.Connection, context.Table, ct).ConfigureAwait(false))
            .FirstOrDefault(p => string.Equals(p.Column, change.Column, StringComparison.OrdinalIgnoreCase));
        if (left is not null)
        {
            await ColumnRetyper.RollBackAsync(context, left, ct).ConfigureAwait(false);
        }
    }

    private static TypeChangeReport NotPermitted(ColumnTypeChange change, RelationalObject target, long rows)
    {
        if (change.IsNarrowing)
        {
            return TypeChangeReports.From(change, TypeChangeReport.TargetLayer, target.QualifiedName, TypeChangeStatus.KeptWider,
                $"{change.Describe(target.QualifiedName)}: kept wider, which holds every value of the source; set {change.RequiresOptIn} to narrow it " +
                "after a check that every stored value fits.", rows);
        }

        var what = change.RequiresOptIn == ColumnTypeChange.ConvertTypes
            ? "checks every stored value converts without loss, then converts the column by the safe rewrite"
            : "rewrites the column by the safe rewrite, which blocks readers only for the final swap";
        return TypeChangeReports.From(change, TypeChangeReport.TargetLayer, target.QualifiedName, TypeChangeStatus.Pending,
            $"{change.Describe(target.QualifiedName)}: pending, the table is untouched. {Capitalize(change.Reason)}. Set {change.RequiresOptIn}: true " +
            $"on the flow and the next run {what}.", rows);
    }

    private static TypeChangeReport Refused(ColumnTypeChange change, RelationalObject target, StoredValueCheck check, bool planned)
    {
        var count = check.Failing.ToString(CultureInfo.InvariantCulture);
        var afterLoad = change.Timing == TypeChangeTiming.AfterLoad;
        var next = afterLoad
            ? "Render them in the source as values of the new type (an expression in its transform, such as NULLIF(@ColName, 'NA') or a " +
              "CONVERT with the style the text is written in), then run this flow once with a full load: the load writes the source's values " +
              "into the column first, and the conversion then checks those. Or correct them in the table."
            : $"Declare a type in the source whose values fit (a longer {change.ToType.BaseType}), or correct them in the table.";

        // A plan checks the values the table holds now; a run checks a text column becoming typed only after its load has
        // written the source's values into the rows it read.
        var whenRun = planned && afterLoad
            ? " A run checks them once its load has written the source's values into the rows it reads, so it converts the column " +
              "when the load rewrites every one of these rows."
            : string.Empty;
        return TypeChangeReports.From(change, TypeChangeReport.TargetLayer, target.QualifiedName, TypeChangeStatus.Refused,
            $"{change.Describe(target.QualifiedName)}: refused, the table is untouched. {count} of {check.Rows.ToString(CultureInfo.InvariantCulture)} " +
            $"stored value(s) do not convert to {change.ToType.Render()} without loss (for example {DescribeSample(check.Sample)}).{whenRun} {next}",
            check.Rows, check.Failing, check.Sample);
    }

    // What the safe rewrite keeps, as a run's report and a plan both say it.
    private const string KeptByRewrite = "every row, its identity, system dates, indexes and statistics";

    // The change a rewrite an earlier run left belongs to: the same column, carried by the safe rewrite to the same type.
    // Null when the source no longer asks for that change.
    private static ColumnTypeChange? Resumable(EvolutionPlan plan, PendingRetype left)
        => plan.TypeChanges.FirstOrDefault(c =>
            string.Equals(c.Column, left.Column, StringComparison.OrdinalIgnoreCase)
            && c.Mechanism == TypeChangeMechanism.ShadowRewrite
            && string.Equals(c.ToType.Render(), left.ToType, StringComparison.OrdinalIgnoreCase));

    private static string Unfinished(RelationalObject target, string column, string toType)
        => $"An earlier run's conversion of {target.QualifiedName}.[{column}] to {toType} was left unfinished";

    // An unfinished rewrite a run rolled back, or (planned) would roll back: the source no longer asks for the change, or
    // the flow no longer allows it.
    private static TypeChangeReport RolledBack(
        RelationalObject target, EvolutionPlan plan, PendingRetype left, ColumnTypeChange? change, bool planned)
    {
        var reason = change is null
            ? $"the source no longer asks for [{left.Column}] as {left.ToType}"
            : $"the flow no longer sets {change.RequiresOptIn}";
        var outcome = planned
            ? $"; a run rolls it back first because {reason}, and the column keeps its type and every value."
            : $" and has been rolled back because {reason}; the column keeps its type and every value.";
        return new TypeChangeReport
        {
            Layer = TypeChangeReport.TargetLayer,
            Table = target.QualifiedName,
            Column = left.Column,
            FromType = plan.TypeChanges.FirstOrDefault(c => string.Equals(c.Column, left.Column, StringComparison.OrdinalIgnoreCase))?.FromType.Render() ?? string.Empty,
            ToType = left.ToType,
            Tier = change?.Tier ?? TypeChangeTier.CheckedConversion,
            Status = TypeChangeStatus.RolledBack,
            RewritesTable = true,
            Message = Unfinished(target, left.Column, left.ToType) + outcome,
        };
    }

    // An unfinished rewrite a run would finish: from where it stopped, or from the start when its trigger is gone (writes
    // may then have reached the column without reaching the shadow).
    private static TypeChangeReport PlannedResume(RelationalObject target, ColumnTypeChange change, PendingRetype left, long rows)
    {
        var how = left.TriggerIntact
            ? "a run finishes it first"
            : "its trigger is missing or disabled, so writes may have passed it by: a run removes it first and converts the column again from the start";
        return TypeChangeReports.From(change, TypeChangeReport.TargetLayer, target.QualifiedName, TypeChangeStatus.Planned,
            $"{Unfinished(target, change.Column, change.ToType.Render())}; {how}, by the safe rewrite, which keeps {KeptByRewrite}. " +
            $"{Capitalize(change.Reason)}.", rows);
    }

    private static string InPlace(ColumnTypeChange change, RelationalObject target, bool planned)
        => $"{change.Describe(target.QualifiedName)}: {(planned ? "planned, a run applies it in place" : "applied in place")}. {Capitalize(change.Reason)}. " +
           TypeChangeReport.Downstream;

    // A safe rewrite a run would apply; a checked conversion's message says what its data check found.
    private static TypeChangeReport PlannedRewrite(ColumnTypeChange change, RelationalObject target, long rows, StoredValueCheck? check)
    {
        var afterLoad = change.Timing == TypeChangeTiming.AfterLoad;
        var message = $"{change.Describe(target.QualifiedName)}: planned, a run applies it by the safe rewrite " +
                      $"{(afterLoad ? "once the load has committed" : "before the load")}, which keeps {KeptByRewrite}. {Capitalize(change.Reason)}.";
        if (check is not null)
        {
            message += check.Rows == 0
                ? " The table holds no value to check."
                : $" Every one of the {check.Rows.ToString(CultureInfo.InvariantCulture)} stored value(s) converts to {change.ToType.Render()} without loss" +
                  (afterLoad ? "; the run checks them again once the load has written the source's values." : ".");
        }

        return TypeChangeReports.From(change, TypeChangeReport.TargetLayer, target.QualifiedName, TypeChangeStatus.Planned, message, rows);
    }

    // A tier 3 change: a narrowing the column keeps, or a change the engine refuses, with its manual steps and, for a key
    // column, the stored keys that would merge.
    private static async Task<TypeChangeReport> TierThreeAsync(
        RetypeContext context, RelationalObject target, ColumnTypeChange change, long rows, CancellationToken ct)
    {
        if (change.IsNarrowing)
        {
            return TypeChangeReports.From(change, TypeChangeReport.TargetLayer, target.QualifiedName, TypeChangeStatus.KeptWider,
                $"{change.Describe(target.QualifiedName)}: kept. {Capitalize(change.Reason)}.", rows);
        }

        var message = $"{change.Describe(target.QualifiedName)}: refused, the engine does not apply a tier 3 change. {Capitalize(change.Reason)}.";
        if (change.IsKey && TypeChangeClassifier.IsCheckedPair(change.FromType, change.SourceType))
        {
            var merges = await ColumnRetyper.KeyMergesAsync(context, change, ct).ConfigureAwait(false);
            message += merges.MergedKeys == 0
                ? $" No two stored keys would become one as {change.SourceType.Render()}."
                : $" {merges.MergedKeys.ToString(CultureInfo.InvariantCulture)} converted key(s) would each stand for several stored keys " +
                  $"(for example {DescribeSample(merges.Sample)}).";
        }

        return TypeChangeReports.From(change, TypeChangeReport.TargetLayer, target.QualifiedName, TypeChangeStatus.Manual,
            message + ManualStepsSuffix(change.ManualSteps), rows);
    }

    private static string ManualStepsSuffix(IReadOnlyList<string> steps) => " Manual steps: " + string.Join(" ", steps);

    internal static string DescribeSample(IReadOnlyList<TypeChangeValueSample> sample)
        => sample.Count == 0
            ? "none sampled"
            : string.Join(", ", sample.Select(s => $"'{s.Value}' in {s.Rows.ToString(CultureInfo.InvariantCulture)} row(s)"));

    private static string Capitalize(string text)
        => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
