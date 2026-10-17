namespace Ferry.Core.Infrastructure;

/// <summary>
/// Where Ferry keeps its own data. Machine-wide under ProgramData when elevated, so undo data
/// survives whichever admin account ran the migration; per-user under LocalAppData otherwise.
/// </summary>
public static class AppPaths
{
    private static readonly Lazy<string> DataRootLazy = new(ResolveDataRoot);

    public static string DataRoot => DataRootLazy.Value;
    public static string Logs => Ensure(Path.Combine(DataRoot, "Logs"));
    public static string Undo => Ensure(Path.Combine(DataRoot, "Undo"));
    public static string Reports => Ensure(Path.Combine(DataRoot, "Reports"));
    public static string Temp => Ensure(Path.Combine(DataRoot, "Temp"));

    /// <summary>A fresh empty directory under Temp; the caller deletes it.</summary>
    public static string NewTempDirectory(string prefix)
    {
        var dir = Path.Combine(Temp, $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void TryDeleteDirectory(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return;
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not delete temporary folder {path}: {ex.Message}");
        }
    }

    private static string ResolveDataRoot()
    {
        // Lets tests (and portable runs from a USB stick) keep Ferry's data somewhere else.
        if (Environment.GetEnvironmentVariable("FERRY_DATA_ROOT") is { Length: > 0 } overridden)
        {
            Directory.CreateDirectory(overridden);
            return overridden;
        }
        var machine = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Ferry");
        if (Elevation.IsAdministrator && CanWrite(machine))
            return machine;
        var user = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ferry");
        Directory.CreateDirectory(user);
        return user;
    }

    private static bool CanWrite(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, $".probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}
