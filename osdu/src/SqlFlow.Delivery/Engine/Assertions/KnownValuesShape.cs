using System.Globalization;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Search;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.Engine.Assertions;

/// <summary>
/// One step of a known value's path as the templates have it read: the entity type of the records it reads (null for the
/// test's own kind), how the search index holds the property it reads there, and for a step to the records naming the one
/// before, how the index holds the reference they name it by.
/// </summary>
/// <param name="Step">The step as the document wrote it.</param>
/// <param name="EntityType">The entity type of the records the step reads, or null for the records of the test's kind.</param>
/// <param name="Indexed">How the index holds <see cref="KnownStep.Field"/> in those records, or null when no search can compare it.</param>
/// <param name="ReferenceIndexed">For a step to the records naming the one before: how the index holds <see cref="KnownStep.Reference"/> in them.</param>
public sealed record KnownStepShape(KnownStep Step, string? EntityType, OsduField? Indexed, OsduField? ReferenceIndexed);

/// <summary>
/// One column of known values as the templates have it read: whether its path starts in the record or in the item of the
/// list a row is an item of (and where in the item), and each of its steps.
/// </summary>
/// <param name="Column">The column as the document wrote it.</param>
/// <param name="InItem">The path inside the item for a column starting under the list a row is an item of; null for a column of the record.</param>
/// <param name="Steps">Its steps, in order.</param>
public sealed record KnownColumnShape(KnownColumn Column, string? InItem, IReadOnlyList<KnownStepShape> Steps)
{
    /// <summary>True when the column starts in the item of the list a row is an item of, rather than in the record.</summary>
    public bool ReadsItem => InItem is not null;

    /// <summary>True when the column reads records other than the record it starts in: the ones it names, or the ones naming it.</summary>
    public bool LeavesRecord => Steps.Count > 1 || Steps[0].Step.Via != KnownStepVia.Record;

    /// <summary>The step that reads the values the column compares.</summary>
    public KnownStepShape Last => Steps[^1];
}

/// <summary>
/// How one row of known values reads the records of a kind (osdu/docs/reference/flow/assertion.md, Known values), as the
/// templates say: the list of objects the row is an item of (<c>data.Curves</c>, when a path it is located by is inside it),
/// and the steps of each of its full paths through the records they reach. The template check builds it to find what does
/// not fit, and the evaluator to read and search.
/// </summary>
/// <param name="Items">The list of objects the row is an item of, or null when the row is a record.</param>
/// <param name="LocateBy">The paths the row is located by, in the order the document lists them.</param>
/// <param name="Check">The paths of its proof values, in the order the document lists them.</param>
public sealed record KnownValuesShape(string? Items, IReadOnlyList<KnownColumnShape> LocateBy, IReadOnlyList<KnownColumnShape> Check)
{
    /// <summary>The most problems one assertion's values add, so a thousand wrong values say so in a few lines.</summary>
    private const int MaxValueProblems = 10;

    /// <summary>The comparisons whose operands are values of the property compared, which the template's type can check.</summary>
    private static readonly HashSet<ValueOperator> ComparesValues =
    [
        ValueOperator.EqualTo, ValueOperator.NotEqualTo, ValueOperator.In, ValueOperator.NotIn, ValueOperator.AtLeast, ValueOperator.AtMost,
        ValueOperator.GreaterThan, ValueOperator.LessThan, ValueOperator.Between,
    ];

    /// <summary>
    /// The entity types of the records the paths reach that <paramref name="referenced"/> does not hold a template of yet, as
    /// far as the templates there are tell: a step to the records naming one is of the type it names, and a step to the record
    /// a reference names is of the one type the reference's schema relationship names, which needs the template of the step
    /// before. Asked again once the templates it names are loaded, it names the next ones.
    /// </summary>
    public static IReadOnlyList<string> ReferencedTypes(KnownValuesAssertion assertion, OsduTemplate template, IReadOnlyDictionary<string, OsduTemplate> referenced)
    {
        ArgumentNullException.ThrowIfNull(assertion);
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(referenced);
        var wanted = new List<string>();
        var paths = assertion.Rows.SelectMany(r => r.Locate.Select(l => l.Column).Concat(r.Checks.Select(c => c.Column)))
            .Where(c => c.Steps.Count > 1 || c.Steps[0].Via != KnownStepVia.Record)
            .DistinctBy(c => c.Path, StringComparer.Ordinal);
        foreach (var column in paths)
        {
            OsduTemplate? current = template;
            TemplateVariable? read = null;
            foreach (var step in column.Steps)
            {
                var type = step.Via switch
                {
                    KnownStepVia.Children => step.EntityType,
                    KnownStepVia.Parent => read is null ? null : EntityTypeOf(read),
                    _ => null,
                };
                if (step.Via != KnownStepVia.Record)
                {
                    if (type is null)
                    {
                        break;
                    }

                    if (!referenced.TryGetValue(type, out current))
                    {
                        if (!wanted.Contains(type, StringComparer.OrdinalIgnoreCase))
                        {
                            wanted.Add(type);
                        }

                        break;
                    }
                }

                read = step.Field is null ? read : AssertionTemplates.Resolve(current, step.Field, string.Empty, string.Empty, new List<string>());
            }
        }

        return wanted;
    }

    /// <summary>
    /// The shape of every row of <paramref name="assertion"/> in <paramref name="template"/>, with each place it does not
    /// fit added once to <paramref name="problems"/>: a path the kind (or a kind a path reaches) does not have, a row located
    /// in two lists, a reference that names no one type of record, a step to records naming one that the index cannot find
    /// them by, and values a property cannot hold.
    /// </summary>
    /// <param name="assertion">The known values.</param>
    /// <param name="template">The template of the test's kind.</param>
    /// <param name="referenced">The templates of the entity types the paths reach, by entity type.</param>
    /// <param name="at">How the problems name the assertion.</param>
    /// <param name="problems">Where the problems go.</param>
    public static IReadOnlyList<KnownValuesShape> Of(
        KnownValuesAssertion assertion, OsduTemplate template, IReadOnlyDictionary<string, OsduTemplate> referenced, string at, ICollection<string> problems)
    {
        ArgumentNullException.ThrowIfNull(assertion);
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(referenced);
        ArgumentNullException.ThrowIfNull(problems);
        var lists = template.Variables
            .Where(v => v.Shape == TemplateVariableShape.GroupList && v.Inside is null)
            .Select(v => v.Path.SchemaPath)
            .ToList();
        var found = new List<string>();
        var columns = new Dictionary<(string Items, string Path), (KnownColumnShape Shape, TemplateVariable? Read)>();
        var shapes = new List<KnownValuesShape>(assertion.Rows.Count);
        var valueProblems = 0;
        for (var r = 0; r < assertion.Rows.Count; r++)
        {
            var row = assertion.Rows[r];

            // The list the row is an item of: the one list the paths it is located by reach into, starting in the record.
            var reached = new List<IReadOnlyList<string>>();
            foreach (var column in row.Locate.Select(l => l.Column).Where(c => c.Steps[0].Via == KnownStepVia.Record))
            {
                var crossed = Crossed(column.Steps[0].Field!, lists);
                if (crossed.Count > 1)
                {
                    found.Add($"{at}: '{column.Path}' reaches into {string.Join(", then ", crossed)}, a list inside a list; a row is an item of one list, so locate it by the outer list's properties.");
                }

                if (crossed.Count > 0)
                {
                    reached.Add(crossed);
                }
            }

            var items = reached.Select(c => c[0]).Distinct(StringComparer.Ordinal).ToList();
            if (items.Count > 1)
            {
                found.Add($"{at}: a row is located by paths into {string.Join(" and ", items)}; a row is an item of one list, so locate it by the properties of one of them.");
            }

            var grain = items.Count == 1 ? items[0] : null;
            if (grain is not null && reached.Count == row.Locate.Count)
            {
                found.Add($"{at}: a row is located by paths inside {grain} alone, so nothing says which record its item is in; locate it by a value of the record as well, such as its name or its wellbore's.");
            }

            (KnownColumnShape Shape, TemplateVariable? Read) ShapeOf(KnownColumn column)
            {
                if (!columns.TryGetValue((grain ?? string.Empty, column.Path), out var known))
                {
                    known = Shape(column, grain, template, referenced, at, found);
                    columns[(grain ?? string.Empty, column.Path)] = known;
                }

                return known with { Shape = known.Shape with { Column = column } };
            }

            var locate = new List<KnownColumnShape>(row.Locate.Count);
            foreach (var l in row.Locate)
            {
                var (shape, read) = ShapeOf(l.Column);
                locate.Add(shape);
                if (read is not null && valueProblems < MaxValueProblems)
                {
                    valueProblems += CheckValue(read, shape, [l.Value], string.Create(CultureInfo.InvariantCulture, $"{at} rows[{r}] {l.Column.Path}"), " A row located by a value no record can hold is never found.", found);
                }
            }

            var check = new List<KnownColumnShape>(row.Checks.Count);
            foreach (var c in row.Checks)
            {
                var (shape, read) = ShapeOf(c.Column);
                check.Add(shape);
                if (read is not null && c.Column.Aggregate is null && ComparesValues.Contains(c.Condition.Operator) && valueProblems < MaxValueProblems)
                {
                    valueProblems += CheckValue(read, shape, c.Condition.Operands, string.Create(CultureInfo.InvariantCulture, $"{at} rows[{r}] {c.Column.Path}"), string.Empty, found);
                }
            }

            shapes.Add(new KnownValuesShape(grain, locate, check));
        }

        foreach (var problem in found.Distinct(StringComparer.Ordinal))
        {
            problems.Add(problem);
        }

        return shapes;
    }

    /// <summary>
    /// One column's shape: each step resolved in the template of the records it reads (the test's kind, the type a reference
    /// names, the type of the records naming one), and the rows' values checked against what the last step reads.
    /// </summary>
    private static (KnownColumnShape Shape, TemplateVariable? Read) Shape(
        KnownColumn column, string? grain, OsduTemplate template, IReadOnlyDictionary<string, OsduTemplate> referenced, string at, ICollection<string> problems)
    {
        var first = column.Steps[0];
        var inItem = first.Via == KnownStepVia.Record && grain is not null && first.Field!.StartsWith(grain + ".", StringComparison.Ordinal)
            ? first.Field[(grain.Length + 1)..]
            : null;
        var steps = new List<KnownStepShape>(column.Steps.Count);
        OsduTemplate? current = template;
        string? currentType = Documents.OsduKind.EntityType(template.Kind);
        string? stepType = null;
        TemplateVariable? read = null;
        var broken = false;
        foreach (var step in column.Steps)
        {
            OsduField? referenceIndexed = null;
            if (!broken)
            {
                switch (step.Via)
                {
                    case KnownStepVia.Parent:
                        var through = read is null ? null : NamedType(read, column, current!, at, problems);
                        if (through is null)
                        {
                            broken = true;
                            break;
                        }

                        current = Template(through, column, referenced, at, problems);
                        currentType = through;
                        stepType = through;
                        broken = current is null;
                        break;
                    case KnownStepVia.Children:
                        var type = step.EntityType!;
                        current = Template(type, column, referenced, at, problems);
                        if (current is null)
                        {
                            broken = true;
                            break;
                        }

                        var where = $"{current.Kind} (template {current.Version})";
                        if (AssertionTemplates.Resolve(current, step.Reference!, at, where, problems) is { } reference)
                        {
                            if (reference.Relationships.Count > 0 && currentType is not null
                                && !reference.Relationships.Any(r => string.Equals(r, currentType, StringComparison.OrdinalIgnoreCase)))
                            {
                                problems.Add($"{at}: '{column.Path}' reaches the {type} records naming a {currentType} by {step.Reference}, which refers to {string.Join(" or ", reference.Relationships)} in {where}, never to {currentType}.");
                            }

                            referenceIndexed = Indexed(current, step.Reference!);
                            if (referenceIndexed is null)
                            {
                                problems.Add($"{at}: '{column.Path}' reaches the {type} records by {step.Reference}, which the search index does not hold as a value it can compare, so they cannot be found.");
                            }
                        }

                        currentType = type;
                        stepType = type;
                        break;
                }
            }

            if (broken || current is null)
            {
                read = null;
                steps.Add(new KnownStepShape(step, stepType, null, referenceIndexed));
                continue;
            }

            var where2 = $"{current.Kind} (template {current.Version})";
            read = step.Field is null ? null : AssertionTemplates.Resolve(current, step.Field, at, where2, problems);
            steps.Add(new KnownStepShape(step, stepType, step.Field is null ? null : Indexed(current, step.Field), referenceIndexed));
        }

        var shape = new KnownColumnShape(column, inItem, steps);
        if (broken || read is null)
        {
            return (shape, null);
        }

        if (read.Shape is TemplateVariableShape.Group or TemplateVariableShape.GroupList && column.Aggregate is null)
        {
            problems.Add($"{at}: '{column.Path}' ends at {Described(read)}, whose values are not single values a row's can be compared with; name a property inside it.");
            return (shape, null);
        }

        return (shape, read);
    }

    /// <summary>The one entity type the reference <paramref name="variable"/> holds names, or null with a problem saying why there is none.</summary>
    private static string? NamedType(TemplateVariable variable, KnownColumn column, OsduTemplate template, string at, ICollection<string> problems)
    {
        var where = $"{template.Kind} (template {template.Version})";
        if (variable.Type is not ("string" or "any") && !(variable.Shape == TemplateVariableShape.ValueList && variable.ItemType is "string" or null))
        {
            problems.Add($"{at}: '{column.Path}' follows {variable.Path.SchemaPath}, which is {Described(variable)} in {where}, not a reference to a record.");
            return null;
        }

        if (EntityTypeOf(variable) is { } entityType)
        {
            return entityType;
        }

        problems.Add(variable.Relationships.Count == 0
            ? $"{at}: '{column.Path}' follows {variable.Path.SchemaPath}, which names no type of record it refers to in {where}, so the records it names cannot be read."
            : $"{at}: '{column.Path}' follows {variable.Path.SchemaPath}, which may refer to {string.Join(" or ", variable.Relationships)} in {where}; known values follow a reference to one type of record.");
        return null;
    }

    /// <summary>The template of an entity type a path reaches, or null with a problem saying no template folder the flow reads holds one.</summary>
    private static OsduTemplate? Template(string entityType, KnownColumn column, IReadOnlyDictionary<string, OsduTemplate> referenced, string at, ICollection<string> problems)
    {
        if (referenced.TryGetValue(entityType, out var template))
        {
            return template;
        }

        problems.Add($"{at}: no template folder on the flow's path holds a template of {entityType}, so '{column.Path}' cannot be checked against what those records hold. Put its file in one ('sqlflow template capture --kind osdu:wks:{entityType}:<version> --out <folder>/<file>.json' writes it, and the Templates page downloads a saved version), then run the test again.");
        return null;
    }

    /// <summary>The one entity type a reference's schema relationship names (<c>master-data--Wellbore</c>), or null when it names none or several.</summary>
    internal static string? EntityTypeOf(TemplateVariable variable)
    {
        var named = variable.Relationships.Where(r => r.Contains("--", StringComparison.Ordinal)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return named.Count == 1 && variable.Relationships.Count == 1 ? named[0] : null;
    }

    /// <summary>The lists of objects <paramref name="path"/> reaches into, outermost first.</summary>
    private static IReadOnlyList<string> Crossed(string path, IReadOnlyList<string> lists)
        => lists.Where(list => path.StartsWith(list + ".", StringComparison.Ordinal)).OrderBy(list => list.Length).ToList();

    /// <summary>How the search index holds <paramref name="path"/> in records of <paramref name="template"/>'s kind, or null when no search can compare it.</summary>
    private static OsduField? Indexed(OsduTemplate template, string path)
        => SearchFields.IsDataPath(path) ? SearchFields.ClassifyValue(template.Schema, path).Field : SearchFields.RecordProperty(path).Field;

    /// <summary>
    /// Whether every value a row compares a path's values with could be a value of the property the path ends at: of its
    /// type, its date format and its pattern. Returns how many problems it added.
    /// </summary>
    private static int CheckValue(TemplateVariable variable, KnownColumnShape shape, IReadOnlyList<ExpectedValue> operands, string at, string why, ICollection<string> problems)
    {
        var found = new List<string>();
        foreach (var operand in operands)
        {
            AssertionTemplates.CheckValue(variable, shape.Last.Step.Field!, operand, at, found);
        }

        foreach (var problem in found)
        {
            problems.Add(problem + why);
        }

        return found.Count;
    }

    private static string Described(TemplateVariable variable) => variable.Shape switch
    {
        TemplateVariableShape.Group => "an object",
        TemplateVariableShape.GroupList => "a list of objects",
        TemplateVariableShape.ValueList => $"a list of {variable.ItemType ?? "values"}",
        _ => $"a{("aeiou".Contains(variable.Type[0], StringComparison.Ordinal) ? "n" : string.Empty)} {variable.Type}",
    };
}
