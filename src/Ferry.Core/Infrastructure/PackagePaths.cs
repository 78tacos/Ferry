using System.Diagnostics.CodeAnalysis;

namespace Ferry.Core.Infrastructure;

/// <summary>
/// Joins a path taken from a migration package onto a folder the migrator chose.
/// Entry names are not authenticated — an unencrypted file is accepted as-is — so a rooted path or a ".." segment
/// must not be able to write outside that folder.
/// </summary>
public static class PackagePaths
{
    /// <summary>
    /// <paramref name="root"/> plus <paramref name="relative"/>. False when <paramref name="relative"/> is rooted,
    /// contains a ".." segment, or <see cref="Path.GetFullPath(string)"/> of the result leaves <paramref name="root"/>.
    /// </summary>
    public static bool TryCombineUnder(string root, string relative, [NotNullWhen(true)] out string? path)
    {
        path = null;
        if (string.IsNullOrEmpty(root) || relative is null || IsRooted(relative) || HasParentSegment(relative))
            return false;

        // Same string Path.Combine produced before this check, so a path this program captured is unchanged.
        var combined = Path.Combine(root, relative);
        try
        {
            if (!StaysUnder(Path.GetFullPath(root), Path.GetFullPath(ToPlatformSeparators(combined))))
                return false;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }

        path = combined;
        return true;
    }

    private static bool IsRooted(string path)
    {
        if (path.Length == 0)
            return false;
        // Path.Combine drops the destination when the relative part is rooted ("C:\...", "\Windows", "C:foo").
        if (path[0] is '\\' or '/')
            return true;
        if (path.Length >= 2 && path[1] == ':' && char.IsAsciiLetter(path[0]))
            return true;
        return Path.IsPathRooted(path);
    }

    /// <summary>Package paths use either separator. ".." anywhere in them walks out of the destination.</summary>
    private static bool HasParentSegment(string path)
    {
        var start = 0;
        for (var i = 0; i <= path.Length; i++)
        {
            if (i < path.Length && path[i] is not ('\\' or '/'))
                continue;
            if (i - start == 2 && path[start] == '.' && path[start + 1] == '.')
                return true;
            start = i + 1;
        }
        return false;
    }

    /// <summary>
    /// True when <paramref name="candidateFull"/> is <paramref name="rootFull"/> or a child of it.
    /// Trailing separators are ignored and the comparison is case-insensitive, so "C:\Dest\" contains "c:\dest\file"
    /// and "C:\Dest" does not contain "C:\Destination".
    /// </summary>
    private static bool StaysUnder(string rootFull, string candidateFull)
    {
        rootFull = Path.TrimEndingDirectorySeparator(rootFull);
        candidateFull = Path.TrimEndingDirectorySeparator(candidateFull);
        if (rootFull.Length == 0)
            return false;
        if (candidateFull.Equals(rootFull, StringComparison.OrdinalIgnoreCase))
            return true;
        var prefix = Path.EndsInDirectorySeparator(rootFull) ? rootFull : rootFull + Path.DirectorySeparatorChar;
        return candidateFull.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Entry names are Windows paths ('\'). The runtime separator is '/' when tests run elsewhere.</summary>
    private static string ToPlatformSeparators(string path) =>
        Path.DirectorySeparatorChar == '\\' ? path.Replace('/', '\\') : path.Replace('\\', '/');
}
