using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Extensions;
using SilverModManager.Games.TestFramework;
using SilverModManager.Networking.Thunderstore.Tests.Fixtures;
using SilverModManager.DataModel.Undo;
using SilverModManager.Sdk.Thunderstore;
using SilverModManager.StandardGameLocators.TestHelpers;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;

namespace SilverModManager.CLI.Tests;

/// <summary>
/// The install path the verb, the Thunderstore page and the ror2mm link share, against a package index and package
/// downloads answered from a table.
/// </summary>
[Collection("ConsoleOutput")]
public class ThunderstoreInstallerTests(ITestOutputHelper helper) : AIsolatedGameTest<ThunderstoreInstallerTests, ThunderstoreStubbedGame>(helper)
{
    private static readonly ThunderstoreVersionRef RepoLib = new(new ThunderstorePackageRef("Zehs", "REPOLib"), new ThunderstoreVersionNumber(4, 2, 0));

    private readonly RoutingHandler _routes = ThunderstoreVerbFixtures.BuildRoutes();

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        base.AddServices(services)
            .AddGame<ThunderstoreStubbedGame>()
            .AddUniversalGameLocator<ThunderstoreStubbedGame>(Version.Parse("0.0.0"))
            .AddVerbHost(_routes);
        return services;
    }

    private IThunderstoreInstaller Installer => ServiceProvider.GetRequiredService<IThunderstoreInstaller>();

    [Fact]
    public async Task TryGetGameFindsTheThunderstoreSideOfTheLoadoutsGame()
    {
        var loadout = await CreateLoadout();

        var found = Installer.TryGetGame(loadout.LoadoutId, out var game, out var problem);

        found.Should().BeTrue(problem);
        game!.ThunderstoreCommunity.Should().NotBeNullOrEmpty();
        problem.Should().BeEmpty();
    }

    [Fact]
    public async Task InstallDownloadsAndInstallsTheLoaderPackBeforeThePackage()
    {
        var loadout = await CreateLoadout();

        var outcomes = await Installer.InstallAsync(RepoLib, loadout.LoadoutId, CancellationToken.None);

        outcomes.Select(o => o.Name).Should().Equal("BepInEx-BepInExPack-5.4.2305", "Zehs-REPOLib-4.2.0");
        outcomes.Should().OnlyContain(o => o.Downloaded && o.Outcome == LoadoutPackageOutcome.Installed);
        outcomes[^1].Item.Name.Should().Be("Zehs-REPOLib-4.2.0.zip");
    }

    [Fact]
    public async Task Install_WritesARestorePointAfterDownloads()
    {
        var loadout = await CreateLoadout();
        var undo = ServiceProvider.GetRequiredService<UndoService>();
        var before = undo.RevisionsFor(loadout.Id);

        await Installer.InstallAsync(RepoLib, loadout.LoadoutId, CancellationToken.None);

        var after = undo.RevisionsFor(loadout.Id);
        after.Count.Should().Be(before.Count + 1);
        after[^1].ModCount.Should().Be(0, "the restore point is written before the first package is linked");
        LibraryLinkedLoadoutItem.All(Connection.Db).Count(link => link.AsLoadoutItem().LoadoutId == loadout.LoadoutId).Should().Be(2);
    }

    [Fact]
    public async Task InstallingAgainDownloadsNothingAndChangesNothing()
    {
        var loadout = await CreateLoadout();
        await Installer.InstallAsync(RepoLib, loadout.LoadoutId, CancellationToken.None);

        var again = await Installer.InstallAsync(RepoLib, loadout.LoadoutId, CancellationToken.None);

        again.Should().NotBeEmpty();
        again.Should().OnlyContain(o => o.Downloaded == false && o.Outcome == LoadoutPackageOutcome.AlreadyEnabled);
    }

    [Fact]
    public async Task CommunitiesOfReadsTheCommunitiesThePackageIsListedUnder()
    {
        var communities = await Installer.CommunitiesOf(new ThunderstorePackageRef("BepInEx", "BepInExPack"), CancellationToken.None);

        communities.Should().Equal("riskofrain2", "repo");
    }

    [Fact]
    public async Task CommunitiesOfAPackageThunderstoreDoesNotKnowIsEmpty()
    {
        var communities = await Installer.CommunitiesOf(new ThunderstorePackageRef("Nobody", "Nothing"), CancellationToken.None);

        communities.Should().BeEmpty();
    }
}
