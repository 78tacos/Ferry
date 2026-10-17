using System.IO.Enumeration;

namespace Ferry.Core.Infrastructure;

public readonly record struct WalkEntry(
    string FullPath,
    string RelativePath,
    bool IsDirectory,
    long Length,
    FileAttributes Attributes,
    DateTime CreationTimeUtc,
    DateTime LastWriteTimeUtc);

public sealed class WalkStats
{
    public long Bytes;
    public int Files;
    public int Directories;
    public int CloudOnlySkipped;
    public int LinksSkipped;
}

/// <summary>
/// Walks a folder tree for migration. Never follows junctions or symbolic links (Windows profiles are full of
/// legacy junctions that loop), and skips cloud-only placeholder files so OneDrive does not start downloading.
/// </summary>
public static class FileWalker
{
    // Attributes Windows sets on placeholders whose data lives in the cloud.
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;

    public static bool IsCloudOnly(FileAttributes attributes) =>
        (attributes & (RecallOnDataAccess | RecallOnOpen | FileAttributes.Offline)) != 0;

    /// <summary>Enumerates files and directories under <paramref name="root"/>, parents before children.</summary>
    public static IEnumerable<WalkEntry> Walk(string root, ExclusionRules? rules, WalkStats? stats = null,
        CancellationToken ct = default, long? maxFileSize = null)
    {
        if (!Directory.Exists(root))
            yield break;

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
        };
        var rootLength = root.TrimEnd('\\').Length + 1;

        var enumerable = new FileSystemEnumerable<WalkEntry>(root,
            (ref FileSystemEntry e) => new WalkEntry(
                e.ToFullPath(),
                RelativeOf(ref e, rootLength),
                e.IsDirectory,
                e.IsDirectory ? 0 : e.Length,
                e.Attributes,
                e.CreationTimeUtc.UtcDateTime,
                e.LastWriteTimeUtc.UtcDateTime),
            options)
        {
            ShouldRecursePredicate = (ref FileSystemEntry e) =>
            {
                if (rules?.IsExcluded(RelativeOf(ref e, rootLength)) == true)
                    return false;
                return (e.Attributes & FileAttributes.ReparsePoint) == 0 || !IsLink(e.ToFullPath(), directory: true);
            },
            ShouldIncludePredicate = (ref FileSystemEntry e) =>
            {
                if (rules?.IsExcluded(RelativeOf(ref e, rootLength)) == true)
                    return false;
                if ((e.Attributes & FileAttributes.ReparsePoint) != 0 && IsLink(e.ToFullPath(), e.IsDirectory))
                {
                    if (stats is not null)
                        Interlocked.Increment(ref stats.LinksSkipped);
                    return false;
                }
                if (!e.IsDirectory && IsCloudOnly(e.Attributes))
                {
                    if (stats is not null)
                        Interlocked.Increment(ref stats.CloudOnlySkipped);
                    return false;
                }
                if (!e.IsDirectory && maxFileSize is { } max && e.Length > max)
                    return false;
                return true;
            },
        };

        foreach (var entry in enumerable)
        {
            ct.ThrowIfCancellationRequested();
            if (stats is not null)
            {
                if (entry.IsDirectory)
                {
                    stats.Directories++;
                }
                else
                {
                    stats.Files++;
                    stats.Bytes += entry.Length;
                }
            }
            yield return entry;
        }
    }

    /// <summary>Total size and file count of what <see cref="Walk"/> would return.</summary>
    public static WalkStats Measure(string root, ExclusionRules? rules, CancellationToken ct = default)
    {
        var stats = new WalkStats();
        foreach (var _ in Walk(root, rules, stats, ct))
        {
        }
        return stats;
    }

    private static string RelativeOf(ref FileSystemEntry e, int rootLength)
    {
        var dir = e.Directory;
        var name = e.FileName;
        // Entries directly in the root (including drive roots like C:\, whose Directory keeps its backslash).
        if (dir.Length <= rootLength)
            return name.ToString();
        return string.Concat(dir[rootLength..], "\\", name);
    }

    /// <summary>True for symbolic links and junctions; false for other reparse points such as OneDrive placeholders.</summary>
    private static bool IsLink(string path, bool directory)
    {
        try
        {
            FileSystemInfo info = directory ? new DirectoryInfo(path) : new FileInfo(path);
            return info.LinkTarget is not null;
        }
        catch
        {
            return true;
        }
    }
}
