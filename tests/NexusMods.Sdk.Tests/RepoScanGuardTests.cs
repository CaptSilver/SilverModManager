using FluentAssertions;

namespace NexusMods.Sdk.Tests;

public class RepoScanGuardTests
{
    [Fact]
    public void Test_VendoredLibrarySourceIsNotScanned()
    {
        var root = RepoScanGuard.FindRepoRoot();
        var files = RepoScanGuard.EnumerateFiles(root, "src", "*.cs").ToArray();

        Directory.Exists(Path.Combine(root, "src", "NexusMods.MnemonicDB")).Should().BeTrue("the library is vendored as source");
        files.Should().NotContain(f => f.StartsWith("src/NexusMods.MnemonicDB/") || f.StartsWith("src/NexusMods.MnemonicDB.Abstractions/") || f.StartsWith("src/NexusMods.MnemonicDB.SourceGenerator/"),
            "the guards police first-party source, and the vendored library keeps upstream's names and style");
        files.Should().Contain("src/NexusMods.Sdk/ApplicationConstants.cs", "everything else under src is still scanned");
    }
}
