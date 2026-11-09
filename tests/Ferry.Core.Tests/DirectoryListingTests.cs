using Ferry.Core.Infrastructure;

namespace Ferry.Core.Tests;

/// <summary>
/// The shared listing is the stricter of the three copies it replaced: links are skipped, and a directory
/// that cannot be read comes back empty instead of failing the scan.
/// </summary>
public class DirectoryListingTests
{
    [Fact]
    public void ChildDirectoriesOmitLinksAndMissingPaths()
    {
        var root = TestEnvironment.NewFolder("listing");
        var real = Path.Combine(root, "Projects");
        Directory.CreateDirectory(real);
        var target = Path.Combine(root, "target");
        Directory.CreateDirectory(target);
        var link = Path.Combine(root, "Linked");
        Directory.CreateSymbolicLink(link, target);

        var names = DirectoryListing.SafeDirectories(root).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.Equal(["Projects", "target"], names);

        Assert.Empty(DirectoryListing.SafeDirectories(Path.Combine(root, "does-not-exist")));
    }
}
