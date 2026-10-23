using FluentAssertions;

namespace SilverModManager.Sdk.Tests;

public class RepoScanGuardTests
{
    [Fact]
    public void Test_VendoredLibrarySourceIsNotScanned()
    {
        var root = RepoScanGuard.FindRepoRoot();
        var files = RepoScanGuard.EnumerateFiles(root, "src", "*.cs").ToArray();

        Directory.Exists(Path.Combine(root, "src", "SilverModManager.MnemonicDB")).Should().BeTrue("the library is vendored as source");
        files.Should().NotContain(f => f.StartsWith("src/SilverModManager.MnemonicDB/") || f.StartsWith("src/SilverModManager.MnemonicDB.Abstractions/") || f.StartsWith("src/SilverModManager.MnemonicDB.SourceGenerator/"),
            "the guards police first-party source, and the vendored library keeps upstream's code and style");
        files.Should().Contain("src/SilverModManager.Sdk/ApplicationConstants.cs", "everything else under src is still scanned");
    }
}
