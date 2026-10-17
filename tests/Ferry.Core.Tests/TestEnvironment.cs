using System.Runtime.CompilerServices;

namespace Ferry.Core.Tests;

/// <summary>Points Ferry's data folder (logs, undo, reports) at a temp folder for the whole test run.</summary>
internal static class TestEnvironment
{
    public static string DataRoot { get; } = Path.Combine(Path.GetTempPath(), "ferry-tests", Guid.NewGuid().ToString("N"));

    [ModuleInitializer]
    internal static void Initialize() => Environment.SetEnvironmentVariable("FERRY_DATA_ROOT", DataRoot);

    /// <summary>A fresh empty folder under the test data root.</summary>
    public static string NewFolder(string name)
    {
        var dir = Path.Combine(DataRoot, $"{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }
}
