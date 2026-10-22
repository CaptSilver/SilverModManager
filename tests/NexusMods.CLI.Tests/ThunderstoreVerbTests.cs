using System.IO.Compression;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Library;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Games.TestFramework;
using NexusMods.Hashing.xxHash3;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Networking.Thunderstore;
using NexusMods.Networking.Thunderstore.Tests.Fixtures;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Hashes;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Loadouts;
using NexusMods.Sdk.ProxyConsole;
using NexusMods.Sdk.Settings;
using NexusMods.Sdk.Thunderstore;
using NexusMods.SingleProcess;
using NexusMods.StandardGameLocators.TestHelpers;
using NexusMods.StandardGameLocators.TestHelpers.StubbedGames;

namespace NexusMods.CLI.Tests;

/// <summary>
/// The <c>thunderstore</c> verbs against a package index and package downloads that an HTTP client answers from a
/// table, so nothing reaches the network.
/// </summary>
[Collection("ConsoleOutput")]
public class ThunderstoreVerbTests(ITestOutputHelper helper) : AIsolatedGameTest<ThunderstoreVerbTests, ThunderstoreStubbedGame>(helper)
{
    private readonly RoutingHandler _routes = ThunderstoreVerbFixtures.BuildRoutes();

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        base.AddServices(services)
            .AddGame<ThunderstoreStubbedGame>()
            .AddUniversalGameLocator<ThunderstoreStubbedGame>(Version.Parse("0.0.0"))
            .AddVerbHost(_routes);
        return services;
    }

    private Task<(int ExitCode, string Output)> Run(params string[] args) => ThunderstoreVerbFixtures.Run(ServiceProvider, args);

    private IEnumerable<LibraryLinkedLoadoutItem.ReadOnly> LinkedItems(Loadout.ReadOnly loadout)
        => LibraryLinkedLoadoutItem.All(Connection.Db).Where(item => item.AsLoadoutItem().LoadoutId == loadout.LoadoutId);

    private Task<LibraryFile.ReadOnly> AddLibraryPackage(string version) => Connection.AddThunderstoreLibraryPackage(version);

    [Fact]
    public async Task InstallAddsTheLoaderPackAndThePackageAndSaysSoOnlyOnce()
    {
        var loadout = await CreateLoadout();

        var (code, output) = await Run("thunderstore", "install", "--loadout", loadout.Name, "--package", "Zehs-REPOLib");

        code.Should().Be(0, output);
        LinkedItems(loadout).Should().HaveCount(2);
        output.Should().Contain("BepInEx-BepInExPack-5.4.2305").And.Contain("Zehs-REPOLib-4.2.0");
        output.IndexOf("BepInEx-BepInExPack-5.4.2305", StringComparison.Ordinal)
            .Should().BeLessThan(output.IndexOf("Zehs-REPOLib-4.2.0", StringComparison.Ordinal), "the loader pack goes in first");
        output.Should().Contain("[downloaded]").And.Contain("[installed]");

        var (again, againOutput) = await Run("thunderstore", "install", "--loadout", loadout.Name, "--package", "Zehs-REPOLib");

        again.Should().Be(0, againOutput);
        LinkedItems(loadout).Should().HaveCount(2);
        againOutput.Should().Contain("[already in loadout] Zehs-REPOLib-4.2.0").And.NotContain("[installed]").And.NotContain("[downloaded]");
        againOutput.Should().Contain("Nothing new");
    }

    [Fact]
    public async Task InstallOfAnUnknownPackageFailsNamingIt()
    {
        var loadout = await CreateLoadout();

        var (code, output) = await Run("thunderstore", "install", "--loadout", loadout.Name, "--package", "Nobody-Nothing");

        code.Should().Be(1);
        output.Should().Contain("Nobody-Nothing");
        LinkedItems(loadout).Should().BeEmpty();
    }

    [Fact]
    public async Task InstallWithAVersionTakesThatVersionFromTheCatalog()
    {
        var loadout = await CreateLoadout();

        var (code, output) = await Run("thunderstore", "install", "--loadout", loadout.Name, "--package", "BepInEx-BepInExPack-5.4.2305");

        code.Should().Be(0, output);
        output.Should().Contain("[installed] BepInEx-BepInExPack-5.4.2305");
    }

    [Fact]
    public async Task SearchPrintsTheMatchingPackageFromTheCachedIndexWithoutAnotherRequest()
    {
        var loadout = await CreateLoadout();
        await ServiceProvider.GetRequiredService<ThunderstoreIndexCache>().Refresh("repo", CancellationToken.None);
        _routes.Requests.Clear();

        var (code, output) = await Run("thunderstore", "search", "--loadout", loadout.Name, "--query", "repolib");

        code.Should().Be(0, output);
        output.Should().Contain("Zehs-REPOLib").And.Contain("4.2.0");
        output.Should().NotContain("BepInExPack");
        _routes.Requests.Should().BeEmpty("a fresh cache is not refreshed");
    }

    [Fact]
    public async Task SearchRefreshesAnEmptyCacheFirst()
    {
        var loadout = await CreateLoadout();

        var (code, output) = await Run("thunderstore", "search", "--loadout", loadout.Name, "--query", "repolib");

        code.Should().Be(0, output);
        output.Should().Contain("Zehs-REPOLib");
        _routes.Requests.Should().Contain(new Uri(ThunderstoreVerbFixtures.IndexUrl));
    }

    [Fact]
    public async Task RefreshFetchesTheIndexOfTheGamesCommunity()
    {
        var loadout = await CreateLoadout();

        var (code, output) = await Run("thunderstore", "refresh", "--loadout", loadout.Name);

        code.Should().Be(0, output);
        _routes.Requests.Should().Contain(new Uri(ThunderstoreVerbFixtures.IndexUrl));
        ServiceProvider.GetRequiredService<ThunderstoreIndexCache>().IsStale("repo").Should().BeFalse();
    }

    [Fact]
    public async Task UpdatesListsALibraryPackageWithANewerVersionInTheCatalog()
    {
        var loadout = await CreateLoadout();
        await AddLibraryPackage("4.0.0");

        var (code, output) = await Run("thunderstore", "updates", "--loadout", loadout.Name);

        code.Should().Be(0, output);
        output.Should().Contain("Zehs-REPOLib").And.Contain("4.0.0").And.Contain("4.2.0");
    }

    [Fact]
    public async Task UpdatesSaysSoWhenEverythingIsCurrent()
    {
        var loadout = await CreateLoadout();
        await AddLibraryPackage("4.2.0");

        var (code, output) = await Run("thunderstore", "updates", "--loadout", loadout.Name);

        code.Should().Be(0, output);
        output.Should().Contain("up to date");
    }
}
