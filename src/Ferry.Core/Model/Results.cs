using System.Text.Json.Serialization;

namespace Ferry.Core.Model;

[JsonConverter(typeof(JsonStringEnumConverter<ItemStatus>))]
public enum ItemStatus { Pending, Succeeded, Warnings, Failed, Skipped }

[JsonConverter(typeof(JsonStringEnumConverter<MessageLevel>))]
public enum MessageLevel { Info, Warning, Error }

public sealed record ItemMessage(MessageLevel Level, string Text);

public sealed class ItemResult
{
    public string ItemId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Group { get; set; } = "";
    public MigrationCategory Category { get; set; }
    public ItemStatus Status { get; set; } = ItemStatus.Pending;
    public int FilesDone { get; set; }
    public int FilesSkipped { get; set; }
    public int FilesFailed { get; set; }
    public long BytesDone { get; set; }
    public List<ItemMessage> Messages { get; set; } = [];

    /// <summary>Settles the status from what was recorded: errors win over warnings, nothing recorded is success.</summary>
    public void Finish()
    {
        if (Status is ItemStatus.Skipped or ItemStatus.Failed)
            return;
        if (Messages.Any(m => m.Level == MessageLevel.Error) && FilesDone == 0 && BytesDone == 0)
            Status = ItemStatus.Failed;
        else if (Messages.Any(m => m.Level != MessageLevel.Info) || FilesFailed > 0)
            Status = ItemStatus.Warnings;
        else
            Status = ItemStatus.Succeeded;
    }
}

[JsonConverter(typeof(JsonStringEnumConverter<OperationKind>))]
public enum OperationKind { Scan, Capture, Restore, Undo }

/// <summary>Outcome of one capture, restore or undo. Rendered to HTML by <see cref="Reporting.HtmlReport"/>.</summary>
public sealed class MigrationReport
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public OperationKind Kind { get; set; }
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
    public DateTime FinishedUtc { get; set; }
    public MachineInfo? Source { get; set; }
    public MachineInfo? Target { get; set; }
    public bool DryRun { get; set; }
    public bool Cancelled { get; set; }
    public string? FatalError { get; set; }

    /// <summary>Where the migration file was written or read, for file transfers.</summary>
    public string? PackagePath { get; set; }

    /// <summary>The undo id on the target, when a restore recorded changes.</summary>
    public string? UndoId { get; set; }
    public List<ItemResult> Items { get; set; } = [];

    /// <summary>Things a person still needs to do by hand (reinstall an app, re-open a PST, set default apps).</summary>
    public List<string> ManualSteps { get; set; } = [];

    /// <summary>Programs that were on the old PC but weren't reinstalled or copied ("Name (version, publisher)").</summary>
    public List<string> ProgramsNotMoved { get; set; } = [];
    public List<string> Notes { get; set; } = [];
    public string? LogPath { get; set; }

    public long TotalBytes => Items.Sum(i => i.BytesDone);
    public int TotalFiles => Items.Sum(i => i.FilesDone);

    /// <summary>Files left alone, mostly because an identical copy was already there.</summary>
    public int TotalSkipped => Items.Sum(i => i.FilesSkipped);
    public bool HasFailures => FatalError is not null || Items.Any(i => i.Status == ItemStatus.Failed);
    public bool HasWarnings => Items.Any(i => i.Status == ItemStatus.Warnings);
}

/// <summary>Progress snapshot raised during long operations. Byte totals are uncompressed content bytes.</summary>
public sealed record MigrationProgress
{
    public string Phase { get; init; } = "";
    public string? CurrentItem { get; init; }
    public string? CurrentDetail { get; init; }
    public long BytesDone { get; init; }
    public long BytesTotal { get; init; }
    public int ItemsDone { get; init; }
    public int ItemsTotal { get; init; }

    public double? Fraction => BytesTotal > 0 ? Math.Clamp((double)BytesDone / BytesTotal, 0, 1) : null;
}
