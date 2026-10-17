using System.Diagnostics;
using Ferry.Core.Infrastructure;
using Ferry.Core.Migrators;
using Ferry.Core.Model;
using Ferry.Core.Packaging;
using Ferry.Core.Rollback;

namespace Ferry.Core.Engine;

/// <summary>Runs scans, captures and restores across all migrators.</summary>
public sealed class MigrationEngine
{
    public MigrationEngine(IEnumerable<IMigrator>? migrators = null)
    {
        Migrators = (migrators ?? MigratorCatalog.CreateAll()).OrderBy(m => m.Order).ToList();
    }

    static MigrationEngine()
    {
        // File opens block a thread for as long as security software takes to scan the file. Reading and writing
        // many small files in parallel only helps if those threads exist up front; the pool otherwise adds them slowly.
        ThreadPool.GetMinThreads(out var workers, out var io);
        if (workers < 64)
            ThreadPool.SetMinThreads(64, io);
    }

    public IReadOnlyList<IMigrator> Migrators { get; }

    // ───────────────────────────── Scan (old PC) ─────────────────────────────

    public async Task<Inventory> ScanAsync(ScanOptions options, IProgress<MigrationProgress>? progress, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        using var awake = KeepAwake.Start("Ferry is scanning this PC for a migration");
        Log.Info("Scanning this PC");
        progress?.Report(new MigrationProgress { Phase = "Looking at this PC" });

        var machine = SystemInfo.Collect();
        var users = FilterUsers(UserProfiles.Enumerate(), options.Users);
        using var hives = new HiveCache(writable: false);
        foreach (var user in users)
        {
            var hive = hives.Get(user, out _)?.Root;
            foreach (var folder in KnownFolders.All)
                user.FolderPaths[folder.Key] = KnownFolders.Resolve(folder, user.ProfilePath, hive);
            user.OneDriveRoots = KnownFolders.OneDriveRoots(hive, user.ProfilePath);
        }

        var exclusions = new ExclusionRules(options.ExcludePatterns);
        var context = new ScanContext(options, machine, users, hives, exclusions,
            text => progress?.Report(new MigrationProgress { Phase = "Scanning", CurrentDetail = text }));
        if (!Elevation.IsAdministrator)
            context.Warn("Ferry is not running as administrator, so only your own account was scanned and Wi-Fi passwords, " +
                         "machine settings and other users' data can't be included. Restart Ferry as administrator to include them.");

        await Parallel.ForEachAsync(Migrators, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (migrator, token) =>
        {
            try
            {
                await migrator.ScanAsync(context, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Error($"Scan of {migrator.Id} failed", ex);
                context.Warn($"Part of the scan ({migrator.Id}) failed: {ex.Message}");
            }
        }).ConfigureAwait(false);

        var order = Migrators.Select((m, i) => (m.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var inventory = new Inventory
        {
            Machine = machine,
            Users = users,
            Warnings = context.Warnings.ToList(),
            Items = context.Items
                .OrderBy(i => i.Category)
                .ThenBy(i => order.GetValueOrDefault(i.MigratorId))
                .ThenBy(i => i.Group, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
        };
        Log.Info($"Scan found {inventory.Items.Count} items ({Format.Bytes(inventory.Items.Sum(i => i.SizeBytes))}) in {Format.Duration(clock.Elapsed)}");
        return inventory;
    }

    private static List<UserProfileInfo> FilterUsers(List<UserProfileInfo> all, List<string> wanted)
    {
        IEnumerable<UserProfileInfo> users = all;
        if (wanted.Count > 0)
        {
            users = all.Where(u => wanted.Any(w =>
                string.Equals(w, u.Sid, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(w, u.AccountName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(w, u.UserName, StringComparison.OrdinalIgnoreCase)));
        }
        else if (!Elevation.IsAdministrator)
        {
            // Without admin rights other profiles can't be read anyway.
            users = all.Where(u => u.Sid == Elevation.CurrentUserSid);
        }
        return users.ToList();
    }

    // ──────────────────────────── Capture (old PC) ───────────────────────────

    public async Task<MigrationReport> CaptureAsync(Inventory inventory, Selection selection, CaptureOptions options, PackageWriter writer,
        IProgress<MigrationProgress>? progress, CancellationToken ct)
    {
        using var awake = KeepAwake.Start("Ferry is copying this PC's files for a migration");
        var report = new MigrationReport { Kind = OperationKind.Capture, Source = inventory.Machine, LogPath = Log.FilePath };
        var selected = inventory.Items.Where(i => selection.Contains(i.Id)).ToList();
        var totalBytes = selected.Sum(i => Preflight.EffectiveSize(i, selection));
        Log.Info($"Capturing {selected.Count} items ({Format.Bytes(totalBytes)})");

        // The package's manifest lists only what is inside it, with the handling chosen for each item.
        var manifest = new Inventory
        {
            ScanId = inventory.ScanId,
            CreatedUtc = inventory.CreatedUtc,
            Machine = inventory.Machine,
            Users = inventory.Users.Where(u => selected.Any(i => i.UserSid == u.Sid)).ToList(),
            Warnings = inventory.Warnings,
            Items = selected.Select(i => WithMode(i, selection.ModeFor(i))).ToList(),
        };
        await writer.WriteJsonAsync(PackageFormat.ManifestEntry, "", manifest, ct: ct).ConfigureAwait(false);
        await writer.WriteJsonAsync(PackageFormat.CaptureInfoEntry, "", new CaptureInfo
        {
            IncludedItemIds = selected.Select(i => i.Id).ToList(),
            Modes = new Dictionary<string, string>(selection.Modes),
            EstimatedBytes = totalBytes,
        }, ct: ct).ConfigureAwait(false);

        var results = new ResultSet();
        foreach (var item in selected)
            results.For(item);

        string? currentItem = null, currentDetail = null;
        var itemsDone = 0;
        var reportClock = Stopwatch.StartNew();
        void Report(long bytes, bool force = false)
        {
            if (!force && reportClock.ElapsedMilliseconds < 200)
                return;
            reportClock.Restart();
            progress?.Report(new MigrationProgress
            {
                Phase = "Copying from this PC",
                CurrentItem = currentItem,
                CurrentDetail = currentDetail,
                BytesDone = bytes,
                BytesTotal = Math.Max(totalBytes, bytes),
                ItemsDone = itemsDone,
                ItemsTotal = selected.Count,
            });
        }
        writer.Progress += b => Report(b);

        using var hives = new HiveCache(writable: false);
        await using var shadow = options.UseShadowCopy && Elevation.IsAdministrator ? new ShadowCopyManager() : null;
        var context = new CaptureContext(writer, inventory, selection, options, hives,
            new ExclusionRules(options.ExcludePatterns), shadow, results, (item, detail) =>
            {
                currentItem = item ?? currentItem;
                currentDetail = detail;
                Report(writer.ContentBytes);
            });

        try
        {
            foreach (var migrator in Migrators)
            {
                var items = selected.Where(i => i.MigratorId == migrator.Id).ToList();
                if (items.Count == 0)
                    continue;
                ct.ThrowIfCancellationRequested();
                currentItem = items[0].Name;
                Report(writer.ContentBytes, force: true);
                try
                {
                    await migrator.CaptureAsync(context, items, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not DestinationException)
                {
                    Log.Error($"Capture of {migrator.Id} failed", ex);
                    foreach (var item in items)
                        results.Error(item, $"Could not be saved: {ex.Message}");
                }
                itemsDone += items.Count;
            }
            await writer.CompleteAsync(cancelled: false, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            report.Cancelled = true;
            Log.Warn("Capture cancelled");
        }
        catch (DestinationException ex)
        {
            report.FatalError = ex.Message;
            Log.Error("Capture stopped: destination failed", ex);
        }

        results.FillReport(report, selected);
        report.FinishedUtc = DateTime.UtcNow;
        Report(writer.ContentBytes, force: true);
        Log.Info($"Capture finished: {Format.Bytes(writer.ContentBytes)} in {writer.EntryCount} entries");
        return report;
    }

    private static InventoryItem WithMode(InventoryItem item, string? mode)
    {
        if (mode is null || mode == item.DefaultMode)
            return item;
        var copy = Json.FromBytes<InventoryItem>(Json.ToBytes(item));
        copy.DefaultMode = mode;
        return copy;
    }

    // ──────────────────────────── Restore (new PC) ───────────────────────────

    /// <summary>
    /// Applies a package. The reader must be positioned just after the manifest (see <see cref="PackageReader.ReadManifestAsync(CancellationToken)"/>).
    /// </summary>
    public async Task<MigrationReport> RestoreAsync(PackageReader reader, Inventory inventory, Selection selection, RestoreOptions options,
        IProgress<MigrationProgress>? progress, CancellationToken ct)
    {
        using var awake = KeepAwake.Start("Ferry is moving files and settings onto this PC");
        var report = new MigrationReport
        {
            Kind = OperationKind.Restore,
            Source = inventory.Machine,
            Target = SystemInfo.Collect(),
            DryRun = options.DryRun,
            LogPath = Log.FilePath,
        };
        var targetUsers = UserProfiles.Enumerate();
        var selected = inventory.Items.Where(i => selection.Contains(i.Id)).ToList();
        var items = selected.ToDictionary(i => i.Id, StringComparer.Ordinal);
        var totalBytes = selected.Sum(i => Preflight.EffectiveSize(i, selection));
        var migrators = Migrators.ToDictionary(m => m.Id, StringComparer.Ordinal);
        Log.Info($"Restoring {selected.Count} items from {inventory.Machine.ComputerName}{(options.DryRun ? " (dry run)" : "")}" +
                 $"{(options.SandboxRoot is null ? "" : $" into sandbox {options.SandboxRoot}")}");

        if (options.CreateRestorePoint && !options.DryRun && options.SandboxRoot is null)
            await CreateRestorePointAsync(report, progress, ct).ConfigureAwait(false);

        var mappedTargets = options.UserMap.Values.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(sid => targetUsers.FirstOrDefault(u => string.Equals(u.Sid, sid, StringComparison.OrdinalIgnoreCase))?.AccountName ?? sid);
        IChangeJournal journal = options.DryRun ? NullJournal.Instance : RollbackJournal.Create(inventory.Machine.ComputerName, mappedTargets);

        var results = new ResultSet();
        foreach (var item in selected)
            results.For(item);

        string? currentItem = null, currentDetail = null;
        var reportClock = Stopwatch.StartNew();
        void Report(long bytes, bool force = false, string phase = "Moving your stuff")
        {
            if (!force && reportClock.ElapsedMilliseconds < 200)
                return;
            reportClock.Restart();
            progress?.Report(new MigrationProgress
            {
                Phase = phase,
                CurrentItem = currentItem,
                CurrentDetail = currentDetail,
                BytesDone = bytes,
                BytesTotal = Math.Max(totalBytes, bytes),
            });
        }
        reader.Progress += b => Report(b);

        using var hives = new HiveCache(writable: true);
        var context = new RestoreContext(inventory, selection, options, report.Target, targetUsers, hives, journal, results, (item, detail) =>
        {
            currentItem = item ?? currentItem;
            currentDetail = detail;
            Report(reader.ContentBytes);
        });

        var unknownMigrators = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            while (await reader.ReadNextAsync(ct).ConfigureAwait(false) is { } entry)
            {
                if (entry.Name.StartsWith("manifest/", StringComparison.Ordinal) || !items.TryGetValue(entry.ItemId, out var item))
                    continue;
                if (results.For(item).Status is ItemStatus.Skipped or ItemStatus.Failed)
                    continue;
                var (migratorId, _) = EntryNames.SplitFirst(entry.Name);
                if (!migrators.TryGetValue(migratorId, out var migrator))
                {
                    if (unknownMigrators.Add(migratorId))
                        report.Notes.Add($"This version of Ferry doesn't know how to restore \"{migratorId}\" data; it was skipped. Update Ferry and run the migration again.");
                    continue;
                }
                if (currentItem != item.Name)
                {
                    currentItem = item.Name;
                    Report(reader.ContentBytes, force: true);
                }
                try
                {
                    await migrator.RestoreEntryAsync(context, item, entry, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not (OperationCanceledException or DiskFullException or InvalidDataException or PackageReadException))
                {
                    Log.Error($"Restoring {entry.Name} failed", ex);
                    results.Error(item, $"{EntryNames.SplitFirst(entry.Name).Tail}: {ex.Message}");
                }
            }
            // Files still being written in the background; a full disk there is reported here.
            await context.Files.DrainAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            report.Cancelled = true;
            Log.Warn("Restore cancelled");
        }
        catch (Exception ex)
        {
            // Disk full, a damaged package, the other PC going away, or anything unexpected: stop, but still
            // finish what arrived and close the undo journal properly.
            report.FatalError = ex.Message;
            Log.Error("Restore stopped", ex);
        }

        try
        {
            // After a stop, let in-flight background writes settle so the journal is complete.
            await context.Files.DrainAsync(throwOnFailure: false).ConfigureAwait(false);

            // Finishing steps (app installs, Wi-Fi, printers) still run for what arrived, unless the user cancelled.
            if (!report.Cancelled)
            {
                foreach (var migrator in Migrators)
                {
                    var owned = selected.Where(i => i.MigratorId == migrator.Id && results.For(i).Status is not ItemStatus.Skipped).ToList();
                    if (owned.Count == 0)
                        continue;
                    currentItem = owned[0].Name;
                    Report(reader.ContentBytes, force: true, phase: "Finishing up");
                    try
                    {
                        await migrator.CompleteRestoreAsync(context, owned, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        report.Cancelled = true;
                        break;
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"Finishing {migrator.Id} failed", ex);
                        foreach (var item in owned)
                            results.Error(item, $"Could not finish: {ex.Message}");
                    }
                }
            }
            results.FillReport(report, selected);
        }
        finally
        {
            if (journal is RollbackJournal rollback)
            {
                rollback.Complete(report.Cancelled ? UndoStatus.Cancelled : report.FatalError is not null ? UndoStatus.Failed : UndoStatus.Completed);
                var changes = rollback.ChangeCount;
                rollback.Dispose();
                if (changes > 0)
                    report.UndoId = rollback.Id;
                else
                    AppPaths.TryDeleteDirectory(rollback.Directory);
            }
        }
        if (!options.DryRun && report.Items.Any(i => i.Status is ItemStatus.Succeeded or ItemStatus.Warnings))
            report.Notes.Add("Sign out and back in (or restart) so Windows and your apps pick up the moved settings.");
        report.FinishedUtc = DateTime.UtcNow;
        Log.Info($"Restore finished: {report.TotalFiles} files, {Format.Bytes(report.TotalBytes)}");
        return report;
    }

    private static async Task CreateRestorePointAsync(MigrationReport report, IProgress<MigrationProgress>? progress, CancellationToken ct)
    {
        if (!Elevation.IsAdministrator)
            return;
        progress?.Report(new MigrationProgress { Phase = "Creating a System Restore point" });
        // Windows makes at most one restore point a day; when it skips one, Checkpoint-Computer only warns, so a
        // warning counts as failure rather than reporting a restore point that doesn't exist.
        var result = await PowerShellRunner.RunAsync(
            "Checkpoint-Computer -Description 'Before Ferry migration' -RestorePointType APPLICATION_INSTALL -WarningAction SilentlyContinue -WarningVariable w;" +
            "if ($w) { [Console]::Error.WriteLine(($w | Out-String).Trim()); exit 3 }",
            ct, TimeSpan.FromMinutes(5)).ConfigureAwait(false);
        if (result.Succeeded)
        {
            report.Notes.Add("A System Restore point named \"Before Ferry migration\" was created.");
        }
        else
        {
            Log.Warn($"Restore point not created: {result.StdErr.Trim()}");
            report.Notes.Add("No System Restore point was created (System Protection may be off, or Windows already made one in the last 24 hours). Ferry's own undo is still available.");
        }
    }
}
