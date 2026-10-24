using System.Text.RegularExpressions;
using FluentAssertions;

namespace SilverModManager.Sdk.Tests;

/// <summary>
/// Reactive properties come from ReactiveUI.SourceGenerators, which only fills in partial
/// members of partial types. Fody is gone, so a leftover weaver file, package reference or
/// helpers import would either be dead weight or reintroduce a second ReactiveAttribute.
/// </summary>
public partial class ReactiveSourceGeneratorGuardTests
{
    // Spelled in pieces so this file does not match its own scans.
    private const string FodyPackage = "ReactiveUI." + "Fody";
    private const string FodyHelpersUsing = "using " + FodyPackage + ".Helpers";
    private const string ReactiveAttribute = "[Re" + "active]";
    private const string ThisFile = "tests/SilverModManager.Sdk.Tests/ReactiveSourceGeneratorGuardTests.cs";

    [GeneratedRegex(@"\bpartial\s+(class|record|struct)\b")]
    private static partial Regex PartialType();

    [Fact]
    public void Test_NoFodyWeaverFilesAreTracked()
    {
        var root = RepoScanGuard.FindRepoRoot();
        var files = RepoScanGuard.TrackedFiles(root, "**/FodyWeavers.xml", "**/FodyWeavers.xsd").Order().ToArray();
        string.Join(Environment.NewLine, files).Should().BeEmpty("there is no weaver to configure any more");
    }

    [Fact]
    public void Test_NoFodyPackageReferences()
    {
        var root = RepoScanGuard.FindRepoRoot();
        var files = RepoScanGuard.EnumerateFiles(root, ".", "*.csproj", "*.props", "*.targets");
        var found = RepoScanGuard.FindLines(root, files, line => line.Contains(FodyPackage, StringComparison.Ordinal));
        string.Join(Environment.NewLine, found.Select(x => $"{x.File}: {x.Line}")).Should().BeEmpty("reactive properties come from ReactiveUI.SourceGenerators");
    }

    [Fact]
    public void Test_NoFodyHelpersImports()
    {
        var root = RepoScanGuard.FindRepoRoot();
        var files = RepoScanGuard.EnumerateFiles(root, ".", "*.cs").Where(f => f != ThisFile);
        var found = RepoScanGuard.FindLines(root, files, line => line.StartsWith(FodyHelpersUsing, StringComparison.Ordinal));
        string.Join(Environment.NewLine, found.Select(x => $"{x.File}: {x.Line}")).Should().BeEmpty("import ReactiveUI.SourceGenerators instead");
    }

    [Fact]
    public void Test_EveryFileWithAReactiveAttributeDeclaresAPartialType()
    {
        var root = RepoScanGuard.FindRepoRoot();
        var files = RepoScanGuard.EnumerateFiles(root, ".", "*.cs").Where(f => f != ThisFile).ToArray();
        files.Should().NotBeEmpty("the scan must find the sources, or it proves nothing");

        // Approximate: a comment mentioning the attribute does not count, and one partial type
        // in the file is taken to cover the rest. The compiler reports the precise cases.
        var offenders = new List<string>();
        foreach (var file in files)
        {
            var text = File.ReadAllText(Path.Combine(root, file));
            if (!text.Contains(ReactiveAttribute, StringComparison.Ordinal)) continue;

            var usesAttribute = text.Split('\n').Any(line =>
            {
                var trimmed = line.TrimStart();
                return trimmed.Contains(ReactiveAttribute, StringComparison.Ordinal) && !trimmed.StartsWith("//", StringComparison.Ordinal);
            });
            if (usesAttribute && !PartialType().IsMatch(text)) offenders.Add(file);
        }

        string.Join(Environment.NewLine, offenders.Order()).Should().BeEmpty("the generator only completes partial members of partial types");
    }
}
