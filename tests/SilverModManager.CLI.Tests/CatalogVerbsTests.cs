using SilverModManager.Abstractions.Games;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Library.Catalog;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.Thunderstore.Tests.Fixtures;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;

namespace SilverModManager.CLI.Tests;

/// <summary>
/// The <c>catalog</c> verbs over the Thunderstore catalog (answered from the same table the thunderstore verb tests use)
/// and over a catalog of the test's own, for the outcomes Thunderstore does not produce.
/// </summary>
[Collection("ConsoleOutput")]
public class CatalogVerbsTests(ITestOutputHelper helper) : AIsolatedGameTest<CatalogVerbsTests, ThunderstoreStubbedGame>(helper)
{
    private readonly RoutingHandler _routes = ThunderstoreVerbFixtures.BuildRoutes();

    private sealed record Handle : ICatalogHandle
    {
        public string Catalog => "Scripted";
    }

    private sealed class RefusalException() : UserFacingException("Scripted refusal")
    {
        public override string Title => "Nothing was installed";
    }

    private sealed class ScriptedCatalog : IModCatalog
    {
        public string DisplayName => "Scripted";
        public IReadOnlyList<CatalogSort> Sorts { get; } = [new("Best", "Best")];
        public CatalogInstallOutcome Outcome { get; set; } = new CatalogInstallOutcome.Refused(new RefusalException());
        public bool Serves(IGameData game) => true;
        public string InstallLabel(IGameData game) => "Install";

        private static CatalogCard Card(string id) => new(id, "Scripted " + id, "someone", "", null, null, "1.0", 5, null, false, null, new Handle());

        public Task<CatalogPage> Search(IGameData game, CatalogQuery query, CancellationToken cancellationToken)
            => Task.FromResult(new CatalogPage([Card("a"), Card("b")], "2", 3, false));

        public Task<CatalogCard?> Find(IGameData game, string id, CancellationToken cancellationToken)
            => Task.FromResult(id == "a" ? Card("a") : null);

        public Task<CatalogInstallOutcome> Install(CatalogCard card, LoadoutId loadout, CancellationToken cancellationToken) => Task.FromResult(Outcome);
    }

    private readonly ScriptedCatalog _scripted = new();

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        base.AddServices(services)
            .AddGame<ThunderstoreStubbedGame>()
            .AddUniversalGameLocator<ThunderstoreStubbedGame>(Version.Parse("0.0.0"))
            .AddVerbHost(_routes)
            .AddSingleton<IModCatalog>(_scripted);
        return services;
    }

    private Task<(int ExitCode, string Output)> Run(params string[] args) => NexusVerbHost.Run(ServiceProvider, args);

    [Fact]
    public async Task SearchPrintsOneLinePerCard()
    {
        var loadout = await CreateLoadout();

        var (code, output) = await Run("catalog", "search", "--loadout", loadout.Name, "--source", "thunderstore", "--query", "REPO");

        code.Should().Be(0, output);
        output.Should().Contain("Zehs-REPOLib").And.Contain("REPOLib");
        output.Should().NotContain("BepInExPack", "the query narrows the list");
    }

    [Fact]
    public async Task SearchTellsHowToAskForTheNextPage()
    {
        var loadout = await CreateLoadout();

        var (code, output) = await Run("catalog", "search", "-l", loadout.Name, "-s", "Scripted");

        code.Should().Be(0, output);
        output.Should().Contain("--cursor 2");
    }

    [Fact]
    public async Task SearchJsonPrintsTheCardsThenTheCursor()
    {
        var loadout = await CreateLoadout();

        var (code, output) = await Run("catalog", "search", "-l", loadout.Name, "-s", "Scripted", "--json");

        code.Should().Be(0, output);
        // The cards are one JSON array, the cursor an object on the last line.
        var tail = output.LastIndexOf("\n{", StringComparison.Ordinal) + 1;
        System.Text.Json.Nodes.JsonNode.Parse(output[..tail])!.AsArray().Select(row => (string?)row!["id"]).Should().Equal("a", "b");
        System.Text.Json.Nodes.JsonNode.Parse(output[tail..])!["nextCursor"]!.GetValue<string>().Should().Be("2");
    }

    [Fact]
    public async Task InstallAddsThePackageAndItsDependencies()
    {
        var loadout = await CreateLoadout();

        var (code, output) = await Run("catalog", "install", "-l", loadout.Name, "-s", "Thunderstore", "--id", "Zehs-REPOLib");

        code.Should().Be(0, output);
        output.Should().Contain("[installed] BepInEx-BepInExPack-5.4.2305").And.Contain("[installed] Zehs-REPOLib-4.2.0");
    }

    [Fact]
    public async Task InstallExitsTwoOnARefusal()
    {
        var loadout = await CreateLoadout();

        var (code, output) = await Run("catalog", "install", "-l", loadout.Name, "-s", "Scripted", "--id", "a");

        code.Should().Be(2, output);
        output.Should().Contain("Scripted refusal");
    }

    [Fact]
    public async Task InstallExitsTwoWhenTheSourceSendsTheUserToItsWebsite()
    {
        var loadout = await CreateLoadout();
        _scripted.Outcome = new CatalogInstallOutcome.OpenedOnWebsite(new Uri("https://example.test/download"));

        var (code, output) = await Run("catalog", "install", "-l", loadout.Name, "-s", "Scripted", "--id", "a");

        code.Should().Be(2, output);
        output.Should().Contain("https://example.test/download");
    }

    [Fact]
    public async Task ASubscribedOutcomeExitsZeroWithItsNote()
    {
        var loadout = await CreateLoadout();
        _scripted.Outcome = new CatalogInstallOutcome.Subscribed("The game downloads it on the next start.");

        var (code, output) = await Run("catalog", "install", "-l", loadout.Name, "-s", "Scripted", "--id", "a");

        code.Should().Be(0, output);
        output.Should().Contain("The game downloads it on the next start.");
    }

    [Fact]
    public async Task AnIdTheSourceDoesNotKnowIsRefusedByName()
    {
        var loadout = await CreateLoadout();

        var (code, output) = await Run("catalog", "install", "-l", loadout.Name, "-s", "Scripted", "--id", "zzz");

        code.Should().Be(1, output);
        output.Should().Contain("zzz");
    }

    [Fact]
    public async Task AnUnknownSourceNamesTheOnesThatServeTheGame()
    {
        var loadout = await CreateLoadout();

        var (code, output) = await Run("catalog", "search", "-l", loadout.Name, "-s", "Nowhere");

        code.Should().Be(1, output);
        output.Should().Contain("Nowhere").And.Contain("Thunderstore").And.Contain("Scripted");
    }

    [Fact]
    public async Task AnUnknownSortNamesTheOnesTheSourceHas()
    {
        var loadout = await CreateLoadout();

        var (code, output) = await Run("catalog", "search", "-l", loadout.Name, "-s", "Scripted", "--sort", "Sideways");

        code.Should().Be(1, output);
        output.Should().Contain("Sideways").And.Contain("Best");
    }
}
