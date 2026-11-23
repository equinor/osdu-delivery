using System.Collections.Concurrent;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Source;

namespace SqlFlow.Delivery.Engine.Worker;

/// <summary>
/// The records a run's workers could not send because the documents queued for them were not there to read
/// (osdu/docs/reference/concepts/submissions.md, Work queued where a run cannot read it): each dropped what it had queued
/// and asks to be planned again from its row. The workers of one run name them here, and the run reads them by key and
/// sends them before it ends, so a document out of a run's reach costs a render, not a wait for the flow's next run.
/// Safe for a run's workers to add to at once.
/// </summary>
public sealed class SetAsideRecords
{
    private readonly ConcurrentDictionary<DeliveryKey, string?> _records = new();

    /// <summary>How many records are named and not yet taken.</summary>
    public int Count => _records.Count;

    /// <summary>Names <paramref name="record"/>, with the key tuple the source reads its row by when the ledger holds one.</summary>
    public void Add(RecordState record)
    {
        ArgumentNullException.ThrowIfNull(record);
        _records[record.DeliveryKey] = record.SourceKeyJson;
    }

    /// <summary>
    /// Takes every record named so far and leaves none: the key tuples a pass reads their rows by, each once, and how many
    /// carry no tuple (records from before the ledger kept one), which no pass can read by key.
    /// </summary>
    public (IReadOnlyList<KeyTuple> Keys, int Keyless) Take()
    {
        var keys = new List<KeyTuple>();
        var seen = new HashSet<KeyTuple>();
        var keyless = 0;
        foreach (var key in _records.Keys.ToList())
        {
            if (!_records.TryRemove(key, out var json))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(json))
            {
                keyless++;
            }
            else
            {
                var tuple = KeyTuple.FromJson(json);
                if (seen.Add(tuple))
                {
                    keys.Add(tuple);
                }
            }
        }

        return (keys, keyless);
    }
}
