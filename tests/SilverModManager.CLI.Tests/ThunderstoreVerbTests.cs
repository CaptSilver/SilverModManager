using SilverModManager.Abstractions.Loadouts.Synchronizers;
using System.IO.Compression;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Extensions;
using SilverModManager.Games.TestFramework;
using NexusMods.Hashing.xxHash3;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.Networking.Thunderstore;
using SilverModManager.Networking.Thunderstore.Tests.Fixtures;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.ProxyConsole;
using SilverModManager.Sdk.Settings;
using SilverModManager.Sdk.Thunderstore;
using SilverModManager.DataModel.Undo;
using SilverModManager.SingleProcess;
using SilverModManager.StandardGameLocators.TestHelpers;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;

namespace SilverModManager.CLI.Tests;

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

    private async Task SetDisabled(Loadout.ReadOnly loadout, string package, bool disabled)
    {
        var link = LinkedItems(loadout).Single(item => item.LibraryItem.TryGetAsLibraryFile(out var file)
            && file.TryGetAsThunderstoreLibraryFile(out var thunderstore)
            && $"{thunderstore.Namespace}-{thunderstore.Name}" == package);
        using var tx = Connection.BeginTransaction();
        if (disabled) tx.Add(link.Id, LoadoutItem.Disabled, Null.Instance);
        else tx.Retract(link.Id, LoadoutItem.Disabled, Null.Instance);
        await tx.Commit();
    }

    private bool IsEnabled(Loadout.ReadOnly loadout, string package)
        => LinkedItems(loadout).Single(item => item.LibraryItem.TryGetAsLibraryFile(out var file)
            && file.TryGetAsThunderstoreLibraryFile(out var thunderstore)
            && $"{thunderstore.Namespace}-{thunderstore.Name}" == package).AsLoadoutItem().IsEnabled();

    [Fact]
    public async Task Install_Json()
    {
        var loadout = await CreateLoadout();

        var (code, output) = await Run("thunderstore", "install", "--loadout", loadout.Name, "--package", "Zehs-REPOLib", "--json");

        code.Should().Be(0, output);
        var rows = System.Text.Json.Nodes.JsonNode.Parse(output)!.AsArray();
        rows.Select(r => (string?)r!["name"]).Should().Equal("BepInEx-BepInExPack-5.4.2305", "Zehs-REPOLib-4.2.0");
        rows.Should().OnlyContain(r => (string?)r!["outcome"] == "installed" && (bool)r["downloaded"]!);
        ((bool)rows[0]!["dependency"]!).Should().BeTrue();
        ((bool)rows[1]!["dependency"]!).Should().BeFalse();
    }

    [Fact]
    public async Task Install_ThenRevert_RemovesBothPackages()
    {
        var loadout = await CreateLoadout();
        var undo = ServiceProvider.GetRequiredService<UndoService>();
        var revisionsBefore = undo.RevisionsFor(loadout.Id).Count;

        var (code, output) = await Run("thunderstore", "install", "--loadout", loadout.Name, "--package", "Zehs-REPOLib");
        code.Should().Be(0, output);
        LinkedItems(loadout).Should().HaveCount(2);
        var restorePoint = undo.RevisionsFor(loadout.Id).Count - 1;
        restorePoint.Should().Be(revisionsBefore, "the install writes one restore point");

        var (exit, revertOutput) = await Run("loadout", "revert", "-l", loadout.LoadoutId.ToString(), "-r", restorePoint.ToString());

        exit.Should().Be(0, revertOutput);
        LinkedItems(loadout).Should().BeEmpty("the restore point was taken before either package was linked");
    }

    [Fact]
    public async Task InstallEnablesADisabledDependencyInsteadOfSkippingIt()
    {
        var loadout = await CreateLoadout();
        await Run("thunderstore", "install", "--loadout", loadout.Name, "--package", "Zehs-REPOLib");
        await SetDisabled(loadout, "BepInEx-BepInExPack", true);

        var (code, output) = await Run("thunderstore", "install", "--loadout", loadout.Name, "--package", "Zehs-REPOLib");

        code.Should().Be(0, output);
        output.Should().Contain("[enabled] BepInEx-BepInExPack-5.4.2305").And.Contain("[already in loadout] Zehs-REPOLib-4.2.0");
        output.Should().NotContain("Nothing new");
        LinkedItems(loadout).Should().HaveCount(2);
        IsEnabled(loadout, "BepInEx-BepInExPack").Should().BeTrue();
    }

    [Fact]
    public async Task InstallEnablesADisabledRootPackage()
    {
        var loadout = await CreateLoadout();
        await Run("thunderstore", "install", "--loadout", loadout.Name, "--package", "Zehs-REPOLib");
        await SetDisabled(loadout, "Zehs-REPOLib", true);

        var (code, output) = await Run("thunderstore", "install", "--loadout", loadout.Name, "--package", "Zehs-REPOLib");

        code.Should().Be(0, output);
        output.Should().Contain("[enabled] Zehs-REPOLib-4.2.0");
        IsEnabled(loadout, "Zehs-REPOLib").Should().BeTrue();
    }

    [Fact]
    public async Task TheSharedInstallStepEnablesALinkedDisabledItemWithoutInstallingAgain()
    {
        var loadout = await CreateLoadout();
        await Run("thunderstore", "install", "--loadout", loadout.Name, "--package", "Zehs-REPOLib");
        await SetDisabled(loadout, "Zehs-REPOLib", true);
        var item = LinkedItems(loadout).Single(link => !link.AsLoadoutItem().IsEnabled()).LibraryItem;

        var first = await LoadoutPackages.InstallOrEnable(Connection, new CollectionOwnership([]), item, loadout.LoadoutId, () => throw new InvalidOperationException("must not install"));
        var second = await LoadoutPackages.InstallOrEnable(Connection, new CollectionOwnership([]), item, loadout.LoadoutId, () => throw new InvalidOperationException("must not install"));

        first.Should().Be(LoadoutPackageOutcome.Enabled);
        second.Should().Be(LoadoutPackageOutcome.AlreadyEnabled);
    }

    [Fact]
    public async Task InstallLeavesADisabledCollectionAloneAndSaysWhyTheDependencyIsNotInTheGame()
    {
        var loadout = await CreateLoadout();
        await Run("thunderstore", "install", "--loadout", loadout.Name, "--package", "Zehs-REPOLib");
        LoadoutItem.Parent.TryGetValue(LinkedItems(loadout).First().AsLoadoutItem(), out var collectionId).Should().BeTrue();
        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(collectionId, LoadoutItem.Disabled, Null.Instance);
            await tx.Commit();
        }

        var (code, output) = await Run("thunderstore", "install", "--loadout", loadout.Name, "--package", "Zehs-REPOLib");

        code.Should().Be(1, output);
        output.Should().Contain("is disabled").And.NotContain("[enabled]");
        LoadoutItem.Load(Connection.Db, collectionId).IsDisabled.Should().BeTrue();
        LinkedItems(loadout).Should().HaveCount(2);
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

    [Fact]
    public async Task Search_Json_ListsEveryMatchWithTheFieldsTheTableShows()
    {
        var loadout = await CreateLoadout();

        var (code, output) = await Run("thunderstore", "search", "--loadout", loadout.Name, "--query", "repolib", "--json");

        code.Should().Be(0, output);
        var rows = System.Text.Json.Nodes.JsonNode.Parse(output)!.AsArray();
        var row = rows.Should().ContainSingle().Subject!.AsObject();
        ((string?)row["package"]).Should().Be("Zehs-REPOLib");
        ((string?)row["latest"]).Should().Be("4.2.0");
        row["rating"].Should().NotBeNull();
        ((bool)row["deprecated"]!).Should().BeFalse();
    }

    [Fact]
    public async Task Search_Json_WithNoMatchIsAnEmptyArray()
    {
        var loadout = await CreateLoadout();

        var (code, output) = await Run("thunderstore", "search", "--loadout", loadout.Name, "--query", "no-such-package-anywhere", "--json");

        code.Should().Be(0, output);
        System.Text.Json.Nodes.JsonNode.Parse(output)!.AsArray().Should().BeEmpty();
    }

    [Fact]
    public async Task Updates_Json_CarriesThePackageAndBothVersions()
    {
        var loadout = await CreateLoadout();
        await AddLibraryPackage("4.0.0");

        var (code, output) = await Run("thunderstore", "updates", "--loadout", loadout.Name, "--json");

        code.Should().Be(0, output);
        var row = System.Text.Json.Nodes.JsonNode.Parse(output)!.AsArray().Should().ContainSingle().Subject!.AsObject();
        ((string?)row["package"]).Should().Contain("Zehs-REPOLib");
        ((string?)row["installed"]).Should().Contain("4.0.0");
        ((string?)row["newest"]).Should().Contain("4.2.0");
    }

    [Fact]
    public async Task Updates_Json_WhenCurrentIsAnEmptyArray()
    {
        var loadout = await CreateLoadout();
        await AddLibraryPackage("4.2.0");

        var (code, output) = await Run("thunderstore", "updates", "--loadout", loadout.Name, "--json");

        code.Should().Be(0, output);
        System.Text.Json.Nodes.JsonNode.Parse(output)!.AsArray().Should().BeEmpty();
    }
}
