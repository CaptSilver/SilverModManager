using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.Games.TestFramework;
using SilverModManager.CLI;
using SilverModManager.Games.TestFramework.Helpers;
using SilverModManager.Networking.NexusWebApi;
using SilverModManager.Sdk.NexusModsApi;
using SilverModManager.Sdk.ProxyConsole;
using SilverModManager.StandardGameLocators.TestHelpers;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;
using SilverModManager.SingleProcess;

namespace SilverModManager.CLI.Tests;

/// <summary>What a verb test needs to run <c>silvermodmanager</c> commands through the real command line.</summary>
internal static class NexusVerbHost
{
    public static IServiceCollection AddVerbs(this IServiceCollection services) => services
        .AddSingleton<CommandLineConfigurator>()
        .AddDefaultParsers()
        .AddCLI()
        .ReplaceDesktopNotifier();

    /// <summary>Runs a command and reads back what it printed, a refusal's sentence included (it travels as a template argument).</summary>
    public static async Task<(int ExitCode, string Output)> Run(IServiceProvider provider, params string[] args)
    {
        var renderer = new LoggingRenderer();
        var exitCode = await provider.GetRequiredService<CommandLineConfigurator>().RunAsync(args, renderer, CancellationToken.None);
        return (exitCode, string.Join("\n", renderer.Logs.SelectMany(Flatten)));
    }

    private static IEnumerable<string> Flatten(IRenderable renderable) => renderable switch
    {
        Text text => [text.Arguments is { Length: > 0 } arguments ? string.Format(text.Template, [.. arguments]) : text.Template],
        Table table => table.Columns.Concat(table.Rows.SelectMany(row => row)).SelectMany(Flatten),
        _ => [],
    };
}

/// <summary>An adult-content setting that stays where the test put it.</summary>
internal sealed class FakeAdultContentSetting(bool show) : IAdultContentSetting
{
    public bool ShowAdultContent => show;

    public IObservable<bool> Changes => new Once(show);

    private sealed class Once(bool value) : IObservable<bool>
    {
        public IDisposable Subscribe(IObserver<bool> observer)
        {
            observer.OnNext(value);
            return new Nothing();
        }
    }

    private sealed class Nothing : IDisposable
    {
        public void Dispose() { }
    }
}

/// <summary>
/// <c>nexus search</c> against a catalog the test hands back, so nothing reaches Nexus Mods.
/// </summary>
[Collection("ConsoleOutput")]
public class NexusSearchVerbTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<NexusSearchVerbTests>(helper)
{
    private readonly INexusModsBrowser _browser = Substitute.For<INexusModsBrowser>();
    private bool _showAdult;
    private NexusModsSearchQuery? _captured;
    private NexusModsSearchPage _page = new([], 0, 0);

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        _browser.Search(Arg.Any<NexusModsSearchQuery>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            _captured = call.Arg<NexusModsSearchQuery>();
            return Task.FromResult(_page);
        });
        return base.AddServices(services)
            .AddVerbs()
            .AddSingleton(_browser)
            .AddSingleton<IAdultContentSetting>(_ => new FakeAdultContentSetting(_showAdult))
            .AddSingleton<IGameDomainToGameIdMappingCache>(new FixedGameDomains(GameDomain.From("cyberpunk2077"), NexusModsGameId.From(3333)));
    }

    private static NexusModsSearchHit Hit(uint id, string name, bool adult = false) => new(
        ModId.From(id), $"uid{id}", name, "summary", "someone", "1.2", 10 + (int)id, 100 + (int)id,
        new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), null, null, adult);

    private Task<(int ExitCode, string Output)> Run(string loadout, params string[] args)
        => NexusVerbHost.Run(ServiceProvider, ["nexus", "search", "--loadout", loadout, .. args]);

    [Fact]
    public async Task Search_PrintsAPage()
    {
        var loadout = await CreateLoadout();
        _page = new NexusModsSearchPage([Hit(7, "Alpha"), Hit(8, "Beta")], 0, 2);

        var (code, output) = await Run(loadout.Name, "--query", "a");

        code.Should().Be(0, output);
        output.Should().Contain("Mod id").And.Contain("Name").And.Contain("Author").And.Contain("Version")
            .And.Contain("Endorsements").And.Contain("Downloads").And.Contain("Updated");
        output.Should().Contain("Alpha").And.Contain("Beta").And.Contain("someone").And.Contain("1.2");
        _captured!.SearchText.Should().Be("a");
        _captured.Sort.Should().Be(NexusModsSort.Endorsements);
    }

    [Fact]
    public async Task Search_Json_CarriesTotalAndHasMore()
    {
        var loadout = await CreateLoadout();
        _page = new NexusModsSearchPage([Hit(7, "Alpha", adult: true)], 40, 90);

        var (code, output) = await Run(loadout.Name, "--page", "2", "--json");

        code.Should().Be(0, output);
        var lines = output.TrimEnd().Split('\n');
        var rows = JsonDocument.Parse(string.Join('\n', lines[..^1])).RootElement;
        rows[0].GetProperty("modId").GetUInt32().Should().Be(7);
        rows[0].GetProperty("adultContent").GetBoolean().Should().BeTrue();
        rows[0].GetProperty("url").GetString().Should().Contain("cyberpunk2077").And.EndWith("/7");
        var tail = JsonDocument.Parse(lines[^1]).RootElement;
        tail.GetProperty("offset").GetInt32().Should().Be(40);
        tail.GetProperty("totalCount").GetInt32().Should().Be(90);
        tail.GetProperty("hasMore").GetBoolean().Should().BeTrue();
        _captured!.Offset.Should().Be(NexusModsSearchQuery.DefaultPageSize);
    }

    [Fact]
    public async Task Search_FollowsTheAdultSetting()
    {
        var loadout = await CreateLoadout();
        _showAdult = true;

        var (code, output) = await Run(loadout.Name);

        code.Should().Be(0, output);
        _captured!.IncludeAdult.Should().BeTrue();
    }

    [Fact]
    public async Task Search_AdultFlagOverridesTheSetting()
    {
        var loadout = await CreateLoadout();
        _showAdult = false;

        var (code, output) = await Run(loadout.Name, "--adult");

        code.Should().Be(0, output);
        _captured!.IncludeAdult.Should().BeTrue();
    }

    [Fact]
    public async Task Search_AdultStaysOffWithoutTheSettingOrTheFlag()
    {
        var loadout = await CreateLoadout();

        await Run(loadout.Name);

        _captured!.IncludeAdult.Should().BeFalse();
    }

    [Fact]
    public async Task Search_UsesTheSharedPageSize()
    {
        var loadout = await CreateLoadout();

        await Run(loadout.Name);

        _captured!.Count.Should().Be(NexusModsSearchQuery.DefaultPageSize);
        _captured.Offset.Should().Be(0);
    }

    [Fact]
    public async Task Search_NexusError_SaysNexusDidNotAnswer()
    {
        var loadout = await CreateLoadout();
        _browser.Search(default!, default).ReturnsForAnyArgs(Task.FromException<NexusModsSearchPage>(new InvalidOperationException("bad gateway")));

        var (code, output) = await Run(loadout.Name);

        code.Should().Be(1, output);
        output.Should().Contain("Nexus Mods did not answer").And.Contain("bad gateway");
    }

    [Fact]
    public async Task Search_NexusTimesOut_SaysNexusDidNotAnswer()
    {
        var loadout = await CreateLoadout();
        // HttpClient reports its own timeout as a cancellation, though nobody cancelled the command.
        _browser.Search(default!, default).ReturnsForAnyArgs(Task.FromException<NexusModsSearchPage>(new TaskCanceledException("The request timed out")));

        var (code, output) = await Run(loadout.Name);

        code.Should().Be(1, output);
        output.Should().Contain("Nexus Mods did not answer").And.Contain("timed out").And.NotContain("   at ");
    }

    [Fact]
    public async Task Search_PastTheLastPage_SaysHowManyMatchesThereAre()
    {
        var loadout = await CreateLoadout();
        _page = new NexusModsSearchPage([], 80, 50);

        var (code, output) = await Run(loadout.Name, "--query", "tweaks", "--page", "3");

        code.Should().Be(0, output);
        output.Should().Contain("Page 3 is past the last match").And.Contain("50").And.NotContain("No mod matches");
    }

    [Fact]
    public async Task Search_RefusesAPageBelowOne()
    {
        var loadout = await CreateLoadout();

        var (code, output) = await Run(loadout.Name, "--page", "0");

        code.Should().Be(1, output);
        output.Should().Contain("page");
    }
}

/// <summary>The Nexus verbs on a loadout whose game Nexus Mods does not host.</summary>
[Collection("ConsoleOutput")]
public class NexusVerbsNoNexusGameTests(ITestOutputHelper helper) : AIsolatedGameTest<NexusVerbsNoNexusGameTests, ThunderstoreStubbedGame>(helper)
{
    private readonly INexusModsBrowser _browser = Substitute.For<INexusModsBrowser>();

    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services)
        .AddGame<ThunderstoreStubbedGame>()
        .AddUniversalGameLocator<ThunderstoreStubbedGame>(Version.Parse("0.0.0"))
        .AddVerbs()
        .AddSingleton(_browser)
        .AddSingleton<IAdultContentSetting>(new FakeAdultContentSetting(false));

    [Fact]
    public async Task Search_RefusesAGameNotOnNexus()
    {
        var loadout = await CreateLoadout();

        var (code, output) = await NexusVerbHost.Run(ServiceProvider, "nexus", "search", "--loadout", loadout.Name);

        code.Should().Be(1, output);
        output.Should().Contain($"{ThunderstoreStubbedGame.DisplayName} is not on Nexus Mods").And.NotContain("   at ");
        await _browser.DidNotReceiveWithAnyArgs().Search(default!, default);
    }

    [Fact]
    public async Task Files_RefusesAGameNotOnNexus()
    {
        var loadout = await CreateLoadout();

        var (code, output) = await NexusVerbHost.Run(ServiceProvider, "nexus", "files", "--loadout", loadout.Name, "--modId", "9");

        code.Should().Be(1, output);
        output.Should().Contain($"{ThunderstoreStubbedGame.DisplayName} is not on Nexus Mods").And.NotContain("   at ");
    }
}
