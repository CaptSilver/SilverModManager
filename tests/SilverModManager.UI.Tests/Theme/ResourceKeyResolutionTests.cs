using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Threading;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Sdk;
using SilverModManager.UI.Tests.Framework;

namespace SilverModManager.UI.Tests.Theme;

/// <summary>
/// Views name theme resources by key. A recolour must only change what a key resolves to, so every key a view
/// references still has to resolve through the running application.
/// </summary>
public partial class ResourceKeyResolutionTests
{
    private static readonly string[] ViewFolders =
    [
        "src/SilverModManager.App.UI",
        "src/SilverModManager.Games.FOMOD.UI",
        "src/SilverModManager.Games.AdvancedInstaller.UI",
    ];

    [GeneratedRegex("""\{(?:StaticResource|DynamicResource)\s+(?:ResourceKey=)?(?<key>[^\s}]+)\s*\}""")]
    private static partial Regex Reference();

    [GeneratedRegex("x:Key=\"(?<key>[^\"]+)\"")]
    private static partial Regex Definition();

    public ResourceKeyResolutionTests(IServiceProvider provider)
    {
        // The test host builds the Application on first use.
        provider.GetRequiredService<AvaloniaApp>();
    }

    [Fact]
    public async Task Test_EveryResourceKeyAViewReferencesResolvesThroughTheApplication()
    {
        var root = FindRoot();
        var files = ViewFolders
            .SelectMany(folder => Directory.EnumerateFiles(Path.Combine(root, folder), "*.axaml", SearchOption.AllDirectories))
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "obj" or "bin"))
            .ToArray();
        files.Should().NotBeEmpty();

        // A key a file defines itself (a local Resources block, a template) is that file's own business.
        var unresolved = new SortedSet<string>(StringComparer.Ordinal);
        var referenced = 0;
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            var local = Definition().Matches(text).Select(match => match.Groups["key"].Value).ToHashSet();
            foreach (var key in Reference().Matches(text).Select(match => match.Groups["key"].Value).Distinct().Where(key => !local.Contains(key)))
            {
                referenced++;
                var found = await Dispatcher.UIThread.InvokeAsync(() =>
                    Application.Current!.TryGetResource(key, Application.Current.ActualThemeVariant, out _));
                if (!found) unresolved.Add($"{key} ({Path.GetRelativePath(root, file)})");
            }
        }

        referenced.Should().BeGreaterThan(100, "the scan must actually find the views' references");
        string.Join(Environment.NewLine, unresolved).Should().BeEmpty();
    }

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, FirstPartyNames.SolutionFile))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException($"Could not find {FirstPartyNames.SolutionFile} above the test binaries");
    }
}
