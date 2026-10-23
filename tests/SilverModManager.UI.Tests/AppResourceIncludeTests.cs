using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Markup.Xaml;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Threading;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Sdk;
using SilverModManager.UI.Tests.Framework;

namespace SilverModManager.UI.Tests;

/// <summary>
/// App.axaml pulls in the page resource dictionaries and the theme by <c>avares://</c> URI, whose host is an
/// assembly name. A wrong host compiles and then fails when the application loads, which is the first thing
/// the user sees. The test host already owns the one Application there can be, so each include is loaded
/// into it the way App.axaml does.
/// </summary>
public partial class AppResourceIncludeTests
{
    [GeneratedRegex("""<(?<kind>ResourceInclude|StyleInclude)\s+Source="(?<uri>avares://[^"]+)"\s*/>""")]
    private static partial Regex Include();

    private readonly string _appXaml;
    private readonly (string Kind, Uri Uri)[] _includes;

    public AppResourceIncludeTests(IServiceProvider provider)
    {
        // The test host builds the Application on first use.
        provider.GetRequiredService<AvaloniaApp>();

        _appXaml = File.ReadAllText(Path.Combine(FindAppProjectDirectory(), "App.axaml"));
        _includes = Include().Matches(_appXaml)
            .Select(match => (match.Groups["kind"].Value, new Uri(match.Groups["uri"].Value)))
            .ToArray();
    }

    [Fact]
    public void Test_AppXamlHasBothKindsOfInclude()
    {
        _includes.Select(include => include.Kind).Should().Contain("ResourceInclude").And.Contain("StyleInclude");
        // An include written in a shape the pattern misses would otherwise go unchecked.
        _includes.Should().HaveCount(Regex.Count(_appXaml, "avares://"), "every avares URI in App.axaml is an include this test loads");
    }

    [Fact]
    public async Task Test_EveryIncludeInAppXamlLoads()
    {
        foreach (var (kind, uri) in _includes)
        {
            var loaded = await Dispatcher.UIThread.InvokeAsync(() => AvaloniaXamlLoader.Load(uri));
            loaded.Should().NotBeNull($"{uri} must load");
            if (kind == "ResourceInclude") loaded.Should().BeAssignableTo<IResourceProvider>($"{uri} is a resource include");
            else loaded.Should().BeAssignableTo<IStyle>($"{uri} is a style include");
        }
    }

    [Fact]
    public async Task Test_AWrongHostFailsToLoad()
    {
        var (_, uri) = _includes.First();
        var wrong = new Uri(uri.ToString().Replace(uri.Host, "No.Such.Assembly"));

        var act = async () => await Dispatcher.UIThread.InvokeAsync(() => AvaloniaXamlLoader.Load(wrong));

        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task Test_ThemeKeyResolvesThroughTheApplication()
    {
        var found = await Dispatcher.UIThread.InvokeAsync(() =>
            Application.Current!.TryGetResource("SurfaceBaseBrush", Application.Current.ActualThemeVariant, out var brush) ? brush : null);

        found.Should().NotBeNull("the theme include supplies SurfaceBaseBrush");
    }

    [Fact]
    public async Task Test_PageResourceKeyResolvesThroughTheApplication()
    {
        var found = await Dispatcher.UIThread.InvokeAsync(() =>
            Application.Current!.TryGetResource("SharedImageTemplate", Application.Current.ActualThemeVariant, out var template) ? template : null);

        found.Should().NotBeNull("the first resource include supplies SharedImageTemplate");
    }

    private static string FindAppProjectDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, FirstPartyNames.SolutionFile))) dir = dir.Parent;
        var root = dir?.FullName ?? throw new InvalidOperationException($"Could not find {FirstPartyNames.SolutionFile} above the test binaries");

        var appXaml = Directory.EnumerateFiles(Path.Combine(root, "src"), "App.axaml", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "obj" or "bin"))
            .Single();
        return Path.GetDirectoryName(appXaml)!;
    }
}
