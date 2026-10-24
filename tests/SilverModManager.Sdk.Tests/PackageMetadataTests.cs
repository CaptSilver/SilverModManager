using System.Text.RegularExpressions;
using FluentAssertions;

namespace SilverModManager.Sdk.Tests;

/// <summary>
/// NuGet.Build.props is imported by every packable project, so the author it names ends up in
/// every package. The fork names its own author there; attribution to upstream lives in NOTICE.
/// </summary>
public partial class PackageMetadataTests
{
    [GeneratedRegex(@"<Authors>(?<names>[^<]*)</Authors>")]
    private static partial Regex AuthorsElement();

    [Fact]
    public void Test_PackageAuthorsNamesTheFork()
    {
        var props = File.ReadAllText(Path.Combine(RepoScanGuard.FindRepoRoot(), "NuGet.Build.props"));

        var authors = AuthorsElement().Matches(props);

        authors.Should().ContainSingle("NuGet.Build.props sets the package author once");
        var names = authors[0].Groups["names"].Value.Trim();
        names.Should().NotBeEmpty();
        names.Should().NotContain("Nexus Mods", "packages built from this repo are the fork's, not upstream's");
    }
}
