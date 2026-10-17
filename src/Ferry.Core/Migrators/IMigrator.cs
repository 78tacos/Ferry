using Ferry.Core.Engine;
using Ferry.Core.Model;
using Ferry.Core.Packaging;

namespace Ferry.Core.Migrators;

/// <summary>
/// One kind of thing Ferry can move (files, Wi-Fi networks, printers…). A migrator finds items on the old PC,
/// writes the selected ones into the package, and applies the package entries it owns on the new PC.
/// Entry names it writes must start with "&lt;Id&gt;/".
/// </summary>
public interface IMigrator
{
    string Id { get; }
    MigrationCategory Category { get; }

    /// <summary>Lower runs first, for both capture and restore. Settings before apps before files.</summary>
    int Order { get; }

    Task ScanAsync(ScanContext context, CancellationToken ct);

    Task CaptureAsync(CaptureContext context, IReadOnlyList<InventoryItem> items, CancellationToken ct);

    /// <summary>Called for each entry of a selected item, in package order, as it arrives.</summary>
    Task RestoreEntryAsync(RestoreContext context, InventoryItem item, PackageEntry entry, CancellationToken ct);

    /// <summary>Called once after the whole package has been read, for work that needs everything (or runs long, like installs).</summary>
    Task CompleteRestoreAsync(RestoreContext context, IReadOnlyList<InventoryItem> items, CancellationToken ct);
}

public abstract class MigratorBase : IMigrator
{
    public abstract string Id { get; }
    public abstract MigrationCategory Category { get; }
    public abstract int Order { get; }

    public abstract Task ScanAsync(ScanContext context, CancellationToken ct);
    public abstract Task CaptureAsync(CaptureContext context, IReadOnlyList<InventoryItem> items, CancellationToken ct);
    public abstract Task RestoreEntryAsync(RestoreContext context, InventoryItem item, PackageEntry entry, CancellationToken ct);

    public virtual Task CompleteRestoreAsync(RestoreContext context, IReadOnlyList<InventoryItem> items, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Builds a new item owned by this migrator with an id of "&lt;Id&gt;:&lt;key&gt;".</summary>
    protected InventoryItem NewItem(string key, string group, string name, string? userSid = null) => new()
    {
        Id = $"{Id}:{key}",
        MigratorId = Id,
        Category = Category,
        Group = group,
        Name = name,
        UserSid = userSid,
    };

    /// <summary>"&lt;Id&gt;/&lt;part&gt;/&lt;part&gt;…" for entries this migrator writes.</summary>
    protected string Entry(params string[] parts) => EntryNames.Combine([Id, .. parts]);
}

public static class EntryNames
{
    /// <summary>Joins name parts with '/'. Relative paths inside a part keep their backslashes.</summary>
    public static string Combine(params string[] parts) => string.Join('/', parts.Where(p => p.Length > 0));

    /// <summary>The remainder of <paramref name="name"/> after "<paramref name="prefix"/>/", or null if it doesn't start with it.</summary>
    public static string? After(string name, string prefix) =>
        name.Length > prefix.Length + 1 && name.StartsWith(prefix, StringComparison.Ordinal) && name[prefix.Length] == '/'
            ? name[(prefix.Length + 1)..]
            : null;

    /// <summary>Splits off the first '/'-separated segment: "a/b/c" → ("a", "b/c").</summary>
    public static (string Head, string Tail) SplitFirst(string name)
    {
        var i = name.IndexOf('/');
        return i < 0 ? (name, "") : (name[..i], name[(i + 1)..]);
    }
}
