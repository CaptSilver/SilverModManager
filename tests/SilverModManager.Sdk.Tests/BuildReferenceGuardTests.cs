using System.Text.RegularExpressions;
using FluentAssertions;

namespace NexusMods.Sdk.Tests;

/// <summary>
/// Scans over build files for names that nothing checks at compile time: an
/// InternalsVisibleTo for an assembly that no longer exists compiles fine and silently grants
/// nothing, and a conditional-compilation symbol spelled differently in the file that defines it
/// and the file that tests it turns the feature off without a diagnostic.
/// </summary>
public partial class BuildReferenceGuardTests
{
    private const string LegacyPrefix = "NEXUSMODS" + "_";
    private const string SystemExtractorSymbol = "SMM_USE_SYSTEM_EXTRACTOR";
    private const string ExtractorTargetsFile = "src/NexusMods.Backend/build/NexusMods.FileExtractor.targets";
    private const string ExtractorSourceFile = "src/NexusMods.Backend/FileExtractor/Extractors/SevenZipExtractor.cs";
    private const string ContributingFile = "docs/developers/Contributing.md";

    [GeneratedRegex("""<InternalsVisibleTo\s+Include="([^"]+)""")]
    private static partial Regex InternalsVisibleToItemRegex();

    [GeneratedRegex("""\[assembly:\s*InternalsVisibleTo\("([^"]+)"\)""")]
    private static partial Regex InternalsVisibleToAttributeRegex();

    [Fact]
    public void Test_EveryInternalsVisibleToNamesAProjectThatExists()
    {
        var root = RepoScanGuard.FindRepoRoot();

        var projects = RepoScanGuard.EnumerateFiles(root, ".", "*.csproj")
            .Select(Path.GetFileNameWithoutExtension)
            .ToHashSet(StringComparer.Ordinal);
        projects.Should().NotBeEmpty("the scan must find the project files, or it proves nothing");

        // Projects grant access either with an MSBuild item or with an assembly attribute in source.
        var sources = RepoScanGuard.EnumerateFiles(root, ".", "*.csproj", "*.props", "*.targets").Select(file => (file, InternalsVisibleToItemRegex()))
            .Concat(RepoScanGuard.EnumerateFiles(root, ".", "*.cs").Select(file => (file, InternalsVisibleToAttributeRegex())))
            .ToArray();
        var dangling = new List<string>();
        var checkedNames = 0;
        foreach (var (file, regex) in sources)
        {
            foreach (Match match in regex.Matches(File.ReadAllText(Path.Combine(root, file))))
            {
                checkedNames++;
                var name = match.Groups[1].Value;
                if (!projects.Contains(name)) dangling.Add($"{file}: {name}");
            }
        }

        checkedNames.Should().BeGreaterThan(10, "the scan must find the InternalsVisibleTo items, or it proves nothing");
        string.Join(Environment.NewLine, dangling).Should().BeEmpty("an InternalsVisibleTo for an assembly that is not built grants nothing; remove it or fix the name");
    }

    [Fact]
    public void Test_TheSystemExtractorSymbolIsSpelledTheSameWhereItIsDefinedTestedAndDocumented()
    {
        var root = RepoScanGuard.FindRepoRoot();

        foreach (var file in new[] { ExtractorTargetsFile, ExtractorSourceFile, ContributingFile, "Directory.Build.props" })
        {
            File.ReadAllText(Path.Combine(root, file)).Should().Contain(SystemExtractorSymbol, $"{file} names the symbol");
        }
    }

    [Fact]
    public void Test_NoLegacyBrandedCompilationSymbolRemains()
    {
        var root = RepoScanGuard.FindRepoRoot();
        var files = RepoScanGuard.EnumerateFiles(root, ".", "*.cs", "*.csproj", "*.props", "*.targets", "*.md", "*.sh", "*.yml", "*.yaml");
        var found = RepoScanGuard.FindLines(root, files, line => line.Contains(LegacyPrefix, StringComparison.Ordinal));

        RepoScanGuard.AssertMatchesAllowlist(found, [], "compile-time symbols and variables carry the SMM_ prefix");
    }
}
