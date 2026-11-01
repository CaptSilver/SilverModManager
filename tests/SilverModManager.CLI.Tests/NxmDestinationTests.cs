using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.CLI;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.NexusModsApi;
using SilverModManager.StandardGameLocators.TestHelpers;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.CLI.Tests;

/// <summary>
/// The game an nxm download is recorded for. <see cref="StubbedGame"/> and <see cref="SiblingStubbedGame"/> share one
/// Nexus id the way Skyrim SE and Skyrim VR do, so the domain alone never says which of them the file is for.
/// </summary>
public class NxmDestinationTests(ITestOutputHelper helper) : AStubbedGameIsolatedTest<NxmDestinationTests>(helper)
{
    private static readonly GameDomain Domain = GameDomain.From("stubbed");

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        services.AddGame<SiblingStubbedGame>();
        return base.AddServices(services).AddUniversalGameLocator<SiblingStubbedGame>(Version.Parse("0.0.0"));
    }

    private GameInstallation InstallationOf(GameId game) => GameRegistry.LocateGameInstallations().First(install => install.Game.GameId == game);

    /// <summary>
    /// A handler for a link whose installs are both managed, so looking them up for loadouts finds a record for each,
    /// as in the app.
    /// </summary>
    private async Task<NxmHandlerHarness.Handler> CreateHandler(IExpectedNexusDownloads expectations, bool libraryHoldsFile)
    {
        await LoadoutManager.ManageInstallation(InstallationOf(StubbedGame.GameId));
        await LoadoutManager.ManageInstallation(InstallationOf(SiblingStubbedGame.GameId));
        return await new NxmHandlerHarness(ServiceProvider, Connection, Domain, StubbedGame.NexusModsGameId.Value)
            .CreateHandler(expectations, libraryHoldsFile);
    }

    private async Task SeedLoadout(GameId game)
    {
        var install = await LoadoutManager.ManageInstallation(InstallationOf(game));
        using var tx = Connection.BeginTransaction();
        _ = new Loadout.New(tx)
        {
            Name = Guid.NewGuid().ToString(),
            ShortName = "ZZZ",
            InstallationId = install.Id,
            LoadoutKind = LoadoutKind.Default,
            Revision = 0,
            GameVersion = VanityVersion.From("Unknown"),
        };
        await tx.Commit();
    }

    private async Task<EntityId> Download(NxmHandlerHarness.Handler handler)
    {
        await handler.Instance.Handle(handler.Link, CancellationToken.None);
        return handler.Events.ReceivedCalls()
            .Select(call => call.GetArguments()[0])
            .OfType<CliMessages.ModDownloadSucceeded>()
            .Single().LibraryItem.Id;
    }

    private IReadOnlySet<GameId> GamesOf(EntityId item) => LibraryDestinations.GamesOf(LibraryItem.Load(Connection.Db, item));

    private bool IsShared(EntityId item) => LibraryItem.Load(Connection.Db, item).Contains(LibraryItem.DestinationsShared);

    private bool IsUnknown(EntityId item) => LibraryItem.Load(Connection.Db, item).Contains(LibraryItem.DestinationUnknown);

    [Fact]
    public async Task ExpectedFile_RecordsTheModlistsGame_NotTheDomainsFirst()
    {
        await SeedLoadout(StubbedGame.GameId);
        await SeedLoadout(SiblingStubbedGame.GameId);
        var handler = await CreateHandler(new ExpectingDownloads(expected: true, SiblingStubbedGame.GameId), libraryHoldsFile: false);

        var item = await Download(handler);

        GamesOf(item).Should().BeEquivalentTo([SiblingStubbedGame.GameId]);
        IsShared(item).Should().BeFalse();
        IsUnknown(item).Should().BeFalse();
    }

    [Fact]
    public async Task ExpectedFile_WithNoLoadoutAnywhere_StillRecordsTheModlistsGame()
    {
        var handler = await CreateHandler(new ExpectingDownloads(expected: true, SiblingStubbedGame.GameId), libraryHoldsFile: false);

        var item = await Download(handler);

        GamesOf(item).Should().BeEquivalentTo([SiblingStubbedGame.GameId]);
    }

    [Fact]
    public async Task ExpectedFile_OfAPlanWithoutAGame_IsUnknown()
    {
        var handler = await CreateHandler(new ExpectingDownloads(expected: true), libraryHoldsFile: false);

        var item = await Download(handler);

        GamesOf(item).Should().BeEmpty();
        IsUnknown(item).Should().BeTrue();
    }

    [Fact]
    public async Task UnexpectedFile_WhenOneGameOfTheDomainHasALoadout_IsForThatGame()
    {
        await SeedLoadout(SiblingStubbedGame.GameId);
        var handler = await CreateHandler(new ExpectingDownloads(expected: false), libraryHoldsFile: false);

        var item = await Download(handler);

        GamesOf(item).Should().BeEquivalentTo([SiblingStubbedGame.GameId]);
        IsShared(item).Should().BeFalse();
    }

    [Fact]
    public async Task UnexpectedFile_WhenSeveralGamesOfTheDomainHaveLoadouts_IsForAllOfThemAndShared()
    {
        await SeedLoadout(StubbedGame.GameId);
        await SeedLoadout(SiblingStubbedGame.GameId);
        var handler = await CreateHandler(new ExpectingDownloads(expected: false), libraryHoldsFile: false);

        var item = await Download(handler);

        GamesOf(item).Should().BeEquivalentTo([StubbedGame.GameId, SiblingStubbedGame.GameId]);
        IsShared(item).Should().BeTrue("the link does not say which of the two the user meant");
    }

    [Fact]
    public async Task AlreadyDownloaded_AdoptsTheExistingFileForTheGame()
    {
        await SeedLoadout(SiblingStubbedGame.GameId);
        var handler = await CreateHandler(new ExpectingDownloads(expected: false), libraryHoldsFile: true);

        await handler.Instance.Handle(handler.Link, CancellationToken.None);

        handler.Events.Received(1).Send(Arg.Is<CliMessages.ModDownloadFailed>(m => m.Reason is FailureReason.AlreadyExists));
        GamesOf(handler.HeldItem!.Value).Should().BeEquivalentTo([SiblingStubbedGame.GameId]);
        IsUnknown(handler.HeldItem.Value).Should().BeFalse();
    }
}
