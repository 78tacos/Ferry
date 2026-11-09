namespace Ferry.Core.Infrastructure;

/// <summary>
/// Child-directory listing shared by the file and settings scans. One rule, so a change to how links are
/// skipped does not have to be copied into each migrator.
/// </summary>
internal static class DirectoryListing
{
    /// <summary>
    /// Child directories of <paramref name="path"/>. Empty when the path is missing or cannot be read.
    /// Symbolic links and junctions are omitted; a directory whose link target cannot be read is omitted too.
    /// </summary>
    public static IReadOnlyList<string> SafeDirectories(string path)
    {
        try
        {
            if (!Directory.Exists(path))
                return [];
            return Directory.EnumerateDirectories(path).Where(d => !IsLink(d)).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// True for a symbolic link or junction (<see cref="FileSystemInfo.LinkTarget"/>).
    /// An unreadable directory is treated as a link so the scan skips it instead of failing.
    /// </summary>
    private static bool IsLink(string dir)
    {
        try
        {
            return new DirectoryInfo(dir).LinkTarget is not null;
        }
        catch
        {
            return true;
        }
    }
}
