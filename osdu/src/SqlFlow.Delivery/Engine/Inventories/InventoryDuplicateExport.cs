using System.Globalization;
using System.Text;
using SqlFlow.Delivery.Engine.Dimensions;
using SqlFlow.Delivery.Ledger;

namespace SqlFlow.Delivery.Engine.Inventories;

/// <summary>
/// Writes every copy of every group of a duplicate check (osdu/docs/reference/flow/inventory-duplicates.md) to a
/// stream as CSV with a header row (RFC 4180), a page of groups at a time in the check's order, the largest group first, so an
/// export of a check of millions never holds more than one page. A row is one copy: its group and how the group came out, the
/// copy kept, what OSDU serves of the copy and its finding, what the check read of it (its versions, how many values it holds,
/// how many records name it) and its role with why, and its values at each property of the key.
/// </summary>
/// <remarks>A cell a spreadsheet would run as a formula is written as text, as an inventory's export writes it.</remarks>
public static class InventoryDuplicateExport
{
    /// <summary>The groups one ledger read writes.</summary>
    public const int GroupsPage = 200;

    /// <summary>The copies of a group too large to list with its page that one ledger read writes.</summary>
    public const int CopiesPage = 1000;

    private const string LineEnd = "\r\n";

    private static readonly string[] Columns =
    [
        "group_number", "group_copies", "group_outcome", "group_reason", "kept_inventory_record_id", "chosen_by", "chosen_utc", "inventory_record_id", "id",
        "kind", "finding", "indexed_version", "stored_version", "storage", "attributes", "mentioned_by", "role", "reason", "create_user", "create_time",
        "modify_user", "modify_time",
    ];

    /// <summary>The file an export of a check is saved as: the flow, the partition, the inventory and the check's number.</summary>
    public static string FileName(InventoryState inventory, long checkId)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        var parts = new[] { inventory.FlowName, inventory.Partition, inventory.Name, "duplicates", checkId.ToString(CultureInfo.InvariantCulture) };
        return string.Join('-', parts.Select(Safe)) + ".csv";
    }

    /// <summary>
    /// Writes every copy of every group of check <paramref name="checkId"/> to <paramref name="output"/> as UTF-8, one column for each
    /// of the key's <paramref name="paths"/> at the end, its values joined by a semicolon and a space; returns how many rows it
    /// wrote. The stream is left open.
    /// </summary>
    public static async Task<long> WriteAsync(ILedger ledger, InventoryState inventory, long checkId, IReadOnlyList<string> paths, Stream output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(output);
        var writer = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 64 * 1024, leaveOpen: true);
        await using (writer.ConfigureAwait(false))
        {
            await writer.WriteAsync(DimensionExport.Csv([.. Columns, .. paths]) + LineEnd).ConfigureAwait(false);
            long rows = 0;
            int? after = null;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var page = await ledger.ListInventoryDuplicateGroupsAsync(inventory.Partition, checkId, null, after, GroupsPage, InventoryDuplicates.MaxCopiesPicked, ct).ConfigureAwait(false);
                foreach (var group in page)
                {
                    if (group.Group.Copies <= InventoryDuplicates.MaxCopiesPicked)
                    {
                        rows += await WriteCopiesAsync(writer, group.Group, group.Members, paths).ConfigureAwait(false);
                        continue;
                    }

                    // A group too large to list with its page is read on its own, a page of its copies at a time.
                    long? afterCopy = null;
                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();
                        var copies = await ledger.ListInventoryDuplicateMembersAsync(inventory.Partition, checkId, group.Group.GroupNumber, afterCopy, CopiesPage, ct).ConfigureAwait(false);
                        rows += await WriteCopiesAsync(writer, group.Group, copies, paths).ConfigureAwait(false);
                        if (copies.Count < CopiesPage)
                        {
                            break;
                        }

                        afterCopy = copies[^1].Record.InventoryRecordId;
                    }
                }

                if (page.Count < GroupsPage)
                {
                    await writer.FlushAsync(ct).ConfigureAwait(false);
                    return rows;
                }

                after = page[^1].Group.GroupNumber;
            }
        }
    }

    private static async Task<long> WriteCopiesAsync(TextWriter writer, InventoryDuplicateGroupState group, IReadOnlyList<InventoryDuplicateMemberState> copies, IReadOnlyList<string> paths)
    {
        foreach (var copy in copies)
        {
            var record = copy.Record;
            var held = InventorySelectionValues.Parse(copy.ValuesJson);
            var line = DimensionExport.Csv(
            [
                Number(group.GroupNumber), Number(group.Copies), group.Outcome, group.Reason, Number(group.KeptInventoryRecordId), group.ChosenBy, Instant(group.ChosenUtc),
                Number(record.InventoryRecordId), record.TargetId, record.Kind, record.Finding, Number(copy.IndexedVersion), Number(copy.StoredVersion), copy.StorageState,
                Number(copy.Attributes), Number(copy.MentionedBy), copy.Role, copy.Reason, record.CreateUser, Instant(record.CreateTime), record.ModifyUser,
                Instant(record.ModifyTime),
                .. paths.Select(p => held.TryGetValue(p, out var values) ? string.Join("; ", values) : null),
            ]);
            await writer.WriteAsync(line + LineEnd).ConfigureAwait(false);
        }

        return copies.Count;
    }

    private static string? Number(long? value) => value?.ToString(CultureInfo.InvariantCulture);

    private static string? Instant(DateTime? value) => value is { } at
        ? DateTime.SpecifyKind(at, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)
        : null;

    /// <summary>A part of a file name: letters, digits, dots, dashes and underscores, anything else a dash.</summary>
    private static string Safe(string part)
    {
        var safe = new StringBuilder(part.Length);
        foreach (var c in part)
        {
            safe.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '-');
        }

        return safe.Length == 0 ? "inventory" : safe.ToString();
    }
}
