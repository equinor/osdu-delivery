using SqlFlow.Core.Runs;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// A run value as a document writes it: one value (<c>logSource: COMPOSITE</c>), or a sequence of them for a parameter
/// given several (<c>logSource: [COMPOSITE, WIRELINE]</c>). Each value is the scalar's text exactly as written, so
/// <c>007</c> stays <c>007</c> and <c>true</c> stays <c>true</c>, as it did when such a value was read as a string.
/// </summary>
internal sealed class RunValueYaml
{
    public RunValueYaml(IReadOnlyList<string> items, bool isList)
    {
        Items = items;
        IsList = isList;
    }

    /// <summary>The values, in the order written; one for a scalar.</summary>
    public IReadOnlyList<string> Items { get; }

    /// <summary>Whether the document wrote a sequence.</summary>
    public bool IsList { get; }

    /// <summary>The run value: the scalar's text, or the text of the list (<see cref="RunValueList"/>).</summary>
    public string Text => IsList ? RunValueList.Of(Items) : Items[0];
}

/// <summary>Reads <see cref="RunValueYaml"/>: a scalar, or a sequence of scalars; an empty scalar or <c>~</c> is no value.</summary>
internal sealed class RunValueYamlConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) => type == typeof(RunValueYaml);

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        ArgumentNullException.ThrowIfNull(parser);
        if (parser.TryConsume<Scalar>(out var scalar))
        {
            return IsNull(scalar) ? null : new RunValueYaml([scalar.Value], isList: false);
        }

        if (parser.TryConsume<SequenceStart>(out var start))
        {
            var items = new List<string>();
            while (!parser.TryConsume<SequenceEnd>(out _))
            {
                if (!parser.TryConsume<Scalar>(out var item) || IsNull(item))
                {
                    throw new YamlException(start.Start, parser.Current?.End ?? start.End, "A list of values holds values only: each entry is one scalar.");
                }

                items.Add(item.Value);
            }

            return new RunValueYaml(items, isList: true);
        }

        var current = parser.Current;
        throw new YamlException(current?.Start ?? Mark.Empty, current?.End ?? Mark.Empty, "A value is one scalar, or a list of them.");
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
        => throw new NotSupportedException("Run values are read from documents, never written to one.");

    private static bool IsNull(Scalar scalar)
        => scalar.IsPlainImplicit && scalar.Value is "" or "~" or "null" or "Null" or "NULL";
}
