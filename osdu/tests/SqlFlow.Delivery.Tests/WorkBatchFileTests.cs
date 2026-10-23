using SqlFlow.Delivery.Storage;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The work batch files the intake writes and the drains read back (osdu/docs/reference/concepts/submissions.md, Work
/// batches): a batch is one JSON-lines file, written line by line as records render, and read either whole, in order,
/// or one document at a time by the reference the ledger holds. A batch of any size is never held in memory, which is
/// what these two reads prove: the sequential pass yields exactly what was written, and the range read answers one line
/// by its offset.
/// </summary>
public class WorkBatchFileTests
{
    [Fact]
    public async Task Work_batches_round_trip_sequentially_and_by_range()
    {
        var root = Samples.NewTempDirectory();
        var stores = Samples.Stores();
        var submission = Guid.NewGuid();
        var items = Enumerable.Range(0, 50)
            .Select(i => new WorkItem(Guid.NewGuid(), $"dev:x:{i}", $"{{\"id\":\"dev:x:{i}\",\"data\":{{\"n\":{i},\"name\":\"næme {i}\"}}}}"))
            .ToList();

        var refs = new List<DocumentRef>();
        await using (var writer = await WorkBatchWriter.OpenAsync(stores, root, submission, 7))
        {
            foreach (var item in items)
            {
                refs.Add(await writer.WriteAsync(item));
            }

            Assert.Equal(50, writer.Count);
            Assert.Equal(7, writer.Batch);
        }

        Assert.True(File.Exists(WorkBatchFile.PathFor(root, submission, 7)));
        Assert.Equal(7, refs[3].Batch);
        Assert.Equal(0, refs[0].Offset);
        Assert.Equal(refs[3], DocumentRef.Parse(refs[3].ToString()));
        Assert.True(DocumentRef.TryParse("1:2:3", out var parsed));
        Assert.Equal(new DocumentRef(1, 2, 3), parsed);
        Assert.False(DocumentRef.TryParse("nope", out _));

        var all = new List<(WorkItem Item, DocumentRef Reference)>();
        await foreach (var entry in WorkBatchFile.ReadAllAsync(stores, root, submission, 7))
        {
            all.Add(entry);
        }

        Assert.Equal(50, all.Count);
        Assert.Equal(refs, all.Select(e => e.Reference).ToList());
        Assert.Equal(items[17].Key, all[17].Item.Key);
        Assert.Equal(items[17].Document, all[17].Item.Document);

        // The range read is what a drain uses: one document, by the reference the record carries, without touching the rest.
        var one = await WorkBatchFile.ReadOneAsync(stores, root, submission, refs[17]);
        Assert.Equal(items[17].Key, one.Key);
        Assert.Equal(items[17].TargetId, one.TargetId);
        Assert.Equal(items[17].Document, one.Document);

        var last = await WorkBatchFile.ReadOneAsync(stores, root, submission, refs[49]);
        Assert.Equal(items[49].Document, last.Document);
    }

    [Fact]
    public async Task A_document_that_is_not_there_to_read_is_missing_whatever_took_it()
    {
        var root = Samples.NewTempDirectory();
        var stores = Samples.Stores();
        var submission = Guid.NewGuid();
        var refs = new List<DocumentRef>();
        await using (var writer = await WorkBatchWriter.OpenAsync(stores, root, submission, 1))
        {
            refs.Add(await writer.WriteAsync(new WorkItem(Guid.NewGuid(), "dev:x:1", "{\"id\":\"dev:x:1\",\"data\":{\"n\":1}}")));
            refs.Add(await writer.WriteAsync(new WorkItem(Guid.NewGuid(), "dev:x:2", "{\"id\":\"dev:x:2\",\"data\":{\"n\":2}}")));
        }

        var path = WorkBatchFile.PathFor(root, submission, 1);

        // Rewritten: the bytes where the document was are not a work item any more.
        await File.WriteAllTextAsync(path, new string('x', (int)new FileInfo(path).Length));
        var rewritten = await Assert.ThrowsAsync<WorkDocumentMissingException>(() => WorkBatchFile.ReadOneAsync(stores, root, submission, refs[0]));
        Assert.Contains("not a work item any more", rewritten.Message, StringComparison.Ordinal);

        // Truncated: the file ends before the document.
        await File.WriteAllTextAsync(path, "{}\n");
        var truncated = await Assert.ThrowsAsync<WorkDocumentMissingException>(() => WorkBatchFile.ReadOneAsync(stores, root, submission, refs[1]));
        Assert.Contains("ended before document", truncated.Message, StringComparison.Ordinal);

        // Pruned: the file is gone, and so is the folder of the submission's batches.
        File.Delete(path);
        var gone = await Assert.ThrowsAsync<WorkDocumentMissingException>(() => WorkBatchFile.ReadOneAsync(stores, root, submission, refs[0]));
        Assert.Contains("is gone", gone.Message, StringComparison.Ordinal);
        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        await Assert.ThrowsAsync<WorkDocumentMissingException>(() => WorkBatchFile.ReadOneAsync(stores, root, submission, refs[0]));
    }

    /// <summary>
    /// A drain reads its batch whole. A batch that is not there (pruned, or written on a node this one does not reach) and a
    /// line that is not a work item any more are missing, which the drain turns into records to plan again, after the items
    /// before them are read.
    /// </summary>
    [Fact]
    public async Task A_batch_that_is_not_there_to_read_whole_is_missing_after_the_items_before_it()
    {
        var root = Samples.NewTempDirectory();
        var stores = Samples.Stores();
        var submission = Guid.NewGuid();

        var gone = await Assert.ThrowsAsync<WorkDocumentMissingException>(() => ReadAllAsync(stores, root, submission, 3));
        Assert.Contains("the batch file is gone", gone.Message, StringComparison.Ordinal);

        var first = new WorkItem(Guid.NewGuid(), "dev:x:1", "{\"id\":\"dev:x:1\",\"data\":{\"n\":1}}");
        await using (var writer = await WorkBatchWriter.OpenAsync(stores, root, submission, 3))
        {
            await writer.WriteAsync(first);
        }

        var path = WorkBatchFile.PathFor(root, submission, 3);
        await File.AppendAllTextAsync(path, "\nnot a work item\n");
        var read = new List<Guid>();
        var rewritten = await Assert.ThrowsAsync<WorkDocumentMissingException>(async () =>
        {
            await foreach (var (item, _) in WorkBatchFile.ReadAllAsync(stores, root, submission, 3))
            {
                read.Add(item.Key);
            }
        });

        Assert.Equal([first.Key], read);
        Assert.Contains("is not a work item any more", rewritten.Message, StringComparison.Ordinal);
    }

    private static async Task<int> ReadAllAsync(FileStoreRegistry stores, string root, Guid submission, int batch)
    {
        var count = 0;
        await foreach (var _ in WorkBatchFile.ReadAllAsync(stores, root, submission, batch))
        {
            count++;
        }

        return count;
    }
}
