namespace Ferry.Core.Infrastructure;

/// <summary>
/// Creates Volume Shadow Copy snapshots on demand so files locked by running programs (an open Outlook
/// data file, a browser database, NTUSER.DAT) can still be read consistently. One snapshot per volume,
/// created the first time a locked file on it is hit, deleted on dispose. Needs admin rights.
/// </summary>
public sealed class ShadowCopyManager : IAsyncDisposable
{
    private readonly Dictionary<string, Task<Snapshot?>> snapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly object gate = new();

    private sealed record Snapshot(string Id, string Device);

    private sealed record CreatedShadow(string Id, string Device);

    public bool IsAvailable => Elevation.IsAdministrator;

    /// <summary>The path of <paramref name="path"/> inside a snapshot of its volume, or null if no snapshot can be made.</summary>
    public async Task<string?> TranslateAsync(string path, CancellationToken ct)
    {
        if (!IsAvailable)
            return null;
        var root = Path.GetPathRoot(path);
        if (root is null || root.Length != 3 || root[1] != ':')
            return null;

        Task<Snapshot?> task;
        lock (gate)
        {
            if (!snapshots.TryGetValue(root, out task!))
            {
                task = CreateAsync(root, ct);
                snapshots[root] = task;
            }
        }
        var snapshot = await task.ConfigureAwait(false);
        return snapshot is null ? null : snapshot.Device + path[2..];
    }

    private static async Task<Snapshot?> CreateAsync(string root, CancellationToken ct)
    {
        Log.Info($"Creating a shadow copy of {root} to read files that are in use");
        try
        {
            var script =
                $"$r=Invoke-CimMethod -ClassName Win32_ShadowCopy -MethodName Create -Arguments @{{Volume={PowerShellRunner.Quote(root)};Context='ClientAccessible'}};" +
                "if($r.ReturnValue -ne 0){throw \"Win32_ShadowCopy.Create returned $($r.ReturnValue)\"};" +
                "$s=Get-CimInstance Win32_ShadowCopy -Filter \"ID='$($r.ShadowID)'\";" +
                "[pscustomobject]@{Id=$r.ShadowID;Device=$s.DeviceObject}";
            var created = await PowerShellRunner.QueryAsync<CreatedShadow>(script, ct, TimeSpan.FromMinutes(3)).ConfigureAwait(false);
            var shadow = created.FirstOrDefault();
            if (shadow is null || string.IsNullOrEmpty(shadow.Device))
                return null;
            Log.Info($"Shadow copy {shadow.Id} of {root} is {shadow.Device}");
            return new Snapshot(shadow.Id, shadow.Device);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"Could not create a shadow copy of {root}: {ex.Message}. Files in use on it will be skipped.");
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<Task<Snapshot?>> tasks;
        lock (gate)
        {
            tasks = snapshots.Values.ToList();
            snapshots.Clear();
        }
        foreach (var task in tasks)
        {
            Snapshot? snapshot;
            try
            {
                snapshot = await task.ConfigureAwait(false);
            }
            catch
            {
                continue;
            }
            if (snapshot is null)
                continue;
            var result = await PowerShellRunner.RunAsync(
                $"Get-CimInstance Win32_ShadowCopy -Filter \"ID='{snapshot.Id}'\" | Remove-CimInstance", CancellationToken.None).ConfigureAwait(false);
            if (result.Succeeded)
                Log.Info($"Deleted shadow copy {snapshot.Id}");
            else
                Log.Warn($"Could not delete shadow copy {snapshot.Id}: {result.StdErr.Trim()}");
        }
    }
}
