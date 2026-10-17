using System.Collections.Concurrent;
using Ferry.Core.Infrastructure;
using Ferry.Core.Model;

namespace Ferry.Core.Engine;

/// <summary>Thread-safe per-item results for one capture or restore.</summary>
public sealed class ResultSet
{
    private const int MaxMessagesPerItem = 300;
    private readonly ConcurrentDictionary<string, ItemResult> results = new(StringComparer.Ordinal);
    private readonly List<string> manualSteps = [];
    private readonly List<string> programsNotMoved = [];
    private readonly List<string> notes = [];

    public ItemResult For(InventoryItem item) => results.GetOrAdd(item.Id, _ => new ItemResult
    {
        ItemId = item.Id,
        Name = item.Name,
        Group = item.Group,
        Category = item.Category,
    });

    public void Info(InventoryItem item, string text) => Add(item, MessageLevel.Info, text);

    public void Warn(InventoryItem item, string text)
    {
        Log.Warn($"[{item.Name}] {text}");
        Add(item, MessageLevel.Warning, text);
    }

    public void Error(InventoryItem item, string text)
    {
        Log.Error($"[{item.Name}] {text}");
        Add(item, MessageLevel.Error, text);
    }

    public void FileDone(InventoryItem item, long bytes)
    {
        var r = For(item);
        lock (r)
        {
            r.FilesDone++;
            r.BytesDone += bytes;
        }
    }

    public void FileSkipped(InventoryItem item, string? reason = null)
    {
        var r = For(item);
        lock (r)
            r.FilesSkipped++;
        if (reason is not null)
            Add(item, MessageLevel.Info, reason);
    }

    public void FileFailed(InventoryItem item, string path, string reason)
    {
        var r = For(item);
        lock (r)
            r.FilesFailed++;
        Log.Warn($"[{item.Name}] {path}: {reason}");
        Add(item, MessageLevel.Warning, $"{path}: {reason}");
    }

    public void MarkSkipped(InventoryItem item, string reason)
    {
        var r = For(item);
        lock (r)
        {
            r.Status = ItemStatus.Skipped;
            r.Messages.Add(new ItemMessage(MessageLevel.Info, reason));
        }
    }

    public void MarkFailed(InventoryItem item, string reason)
    {
        Log.Error($"[{item.Name}] {reason}");
        var r = For(item);
        lock (r)
        {
            r.Status = ItemStatus.Failed;
            r.Messages.Add(new ItemMessage(MessageLevel.Error, reason));
        }
    }

    public void ManualStep(string text)
    {
        lock (manualSteps)
        {
            if (!manualSteps.Contains(text))
                manualSteps.Add(text);
        }
    }

    public void ProgramNotMoved(string text)
    {
        lock (programsNotMoved)
        {
            if (!programsNotMoved.Contains(text))
                programsNotMoved.Add(text);
        }
    }

    public void Note(string text)
    {
        lock (notes)
        {
            if (!notes.Contains(text))
                notes.Add(text);
        }
    }

    private void Add(InventoryItem item, MessageLevel level, string text)
    {
        var r = For(item);
        lock (r)
        {
            if (r.Messages.Count < MaxMessagesPerItem)
                r.Messages.Add(new ItemMessage(level, text));
            else if (r.Messages.Count == MaxMessagesPerItem)
                r.Messages.Add(new ItemMessage(MessageLevel.Info, "Further messages for this item are in the log file."));
        }
    }

    /// <summary>Copies everything into the report, in inventory order.</summary>
    public void FillReport(MigrationReport report, IEnumerable<InventoryItem> order)
    {
        foreach (var item in order)
        {
            if (!results.TryGetValue(item.Id, out var r))
                continue;
            r.Finish();
            report.Items.Add(r);
        }
        lock (manualSteps)
            report.ManualSteps.AddRange(manualSteps);
        lock (programsNotMoved)
            report.ProgramsNotMoved.AddRange(programsNotMoved.Order(StringComparer.CurrentCultureIgnoreCase));
        lock (notes)
            report.Notes.AddRange(notes);
    }
}
