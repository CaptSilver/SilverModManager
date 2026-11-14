using System.Collections.Immutable;
using DynamicData.Kernel;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Paths;
using NSubstitute;
using R3;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Library.Catalog;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.CLI.Types.IpcHandlers;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.EventBus;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.NexusModsApi;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.CLI.Tests;

/// <summary>A game that only has a name and a GameBanana id.</summary>
internal sealed class BananaGameData(string displayName, int gameBananaId) : IGameData, IGameBananaGame
{
    public GameId GameId { get; } = GameId.From(displayName);
    public StoreIdentifiers StoreIdentifiers => new(GameId);
    public string DisplayName { get; } = displayName;
    public Optional<NexusModsGameId> NexusModsGameId => default;
    public IStreamFactory IconImage => throw new NotSupportedException();
    public IStreamFactory TileImage => throw new NotSupportedException();
    public ImmutableDictionary<LocationId, AbsolutePath> GetLocations(IFileSystem fileSystem, GameLocatorResult gameLocatorResult) => throw new NotSupportedException();
    public GamePath GetPrimaryFile(GameInstallation installation) => throw new NotSupportedException();

    public int GameBananaGameId { get; } = gameBananaId;
    public IReadOnlyList<GameBananaSection> GameBananaSections => [GameBananaSection.Mod];
}

/// <summary>
/// The GameBanana link handler's decisions, with a fake installer and a fixed list of managed games, so no test reaches
/// the network, the database or the protocol registrar.
/// </summary>
public class GameBananaIpcProtocolHandlerTests
{
    private const string Link = "silvermodmanager://mod/install/44334/583960";

    private readonly IEventBus _events = Substitute.For<IEventBus>();
    private readonly IGameBananaInstaller _installer = Substitute.For<IGameBananaInstaller>();

    private static GameInstallation Install(string name, int gameBananaId) => new(
        new GameLocatorResult
        {
            Game = new BananaGameData(name, gameBananaId),
            Path = default,
            Store = GameStore.Unknown,
            StoreIdentifier = name,
            LocatorIds = [],
            Locator = Substitute.For<IGameLocator>(),
        },
        default);

    private ListedManagedGames _managed = null!;

    private GameBananaIpcProtocolHandler HandlerFor(IReadOnlyList<GameInstallation> installations, params GameInstallation[] applied)
    {
        _managed = new ListedManagedGames(installations, applied);
        return new GameBananaIpcProtocolHandler(NullLogger<GameBananaIpcProtocolHandler>.Instance, _events, _installer, _managed);
    }

    private void ModIsListedUnder(int? gameBananaId)
        => _installer.GameOf(44334, Arg.Any<CancellationToken>()).Returns(new ValueTask<int?>(gameBananaId));

    private void InstallSucceeds()
        => _installer.Install(Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<LoadoutId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<CatalogInstallOutcome>(new CatalogInstallOutcome.Installed([
                new InstallOutcome("Mod", default, true, LoadoutPackageOutcome.Installed, false),
            ])));

    private IFailureReason TheFailure()
        => _events.ReceivedCalls().Select(c => c.GetArguments()[0]).OfType<CliMessages.ModDownloadFailed>().Single().Reason;

    private sealed class Refusal(string message) : UserFacingException(message)
    {
        public override string Title => "Refused";
    }

    [Fact]
    public void TheHandlerOwnsTheAppsOwnScheme()
    {
        HandlerFor([]).Protocol.Should().Be("silvermodmanager");
    }

    [Fact]
    public async Task AnInstallLinkNamesTheModAndFileAndInstallsThemIntoTheGamesLoadout()
    {
        var tf2 = Install("TF2", 297);
        var other = Install("Other", 1234);
        ModIsListedUnder(297);
        InstallSucceeds();

        await HandlerFor([other, tf2]).Handle(Link, CancellationToken.None);

        await _installer.Received(1).Install(44334, 583960, _managed.LoadoutIdOf(tf2), Arg.Any<CancellationToken>());
        _events.Received(1).Send(Arg.Any<CliMessages.ModDownloadStarted>());
        _events.Received(1).Send(Arg.Any<CliMessages.ModDownloadSucceeded>());
        _events.Received(1).Send(Arg.Is<CliMessages.ModInstalled>(m => m.LoadoutName == "TF2 loadout"));
        _events.DidNotReceive().Send(Arg.Any<CliMessages.ModDownloadFailed>());
    }

    [Theory]
    [InlineData("silvermodmanager://mod/install/44334")]
    [InlineData("silvermodmanager://mod/install/abc/1")]
    [InlineData("silvermodmanager://sound/install/1/2")]
    [InlineData("not a link")]
    public async Task AMalformedLinkIsRefusedNotCrashed(string link)
    {
        await HandlerFor([Install("TF2", 297)]).Handle(link, CancellationToken.None);

        TheFailure().Should().BeOfType<FailureReason.Refused>().Which.Message.Should().Contain(link);
        await _installer.DidNotReceiveWithAnyArgs().Install(default, default, default, default);
    }

    [Fact]
    public async Task AModGameBananaDoesNotKnowIsRefused()
    {
        ModIsListedUnder(null);

        await HandlerFor([Install("TF2", 297)]).Handle(Link, CancellationToken.None);

        TheFailure().Should().BeOfType<FailureReason.Refused>().Which.Message.Should().Contain("44334");
        await _installer.DidNotReceiveWithAnyArgs().Install(default, default, default, default);
    }

    [Fact]
    public async Task NoManagedGameForTheModsGameIsReported()
    {
        ModIsListedUnder(297);

        await HandlerFor([Install("Other", 1234)]).Handle(Link, CancellationToken.None);

        TheFailure().Should().BeOfType<FailureReason.GameNotManaged>();
        await _installer.DidNotReceiveWithAnyArgs().Install(default, default, default, default);
    }

    [Fact]
    public async Task SeveralInstallsOfTheGameAreAmbiguousUnlessOneIsInUse()
    {
        var first = Install("TF2 A", 297);
        var second = Install("TF2 B", 297);
        ModIsListedUnder(297);
        InstallSucceeds();

        await HandlerFor([first, second]).Handle(Link, CancellationToken.None);
        TheFailure().Should().BeOfType<FailureReason.Ambiguous>();
        await _installer.DidNotReceiveWithAnyArgs().Install(default, default, default, default);

        _events.ClearReceivedCalls();
        await HandlerFor([first, second], second).Handle(Link, CancellationToken.None);
        await _installer.Received(1).Install(44334, 583960, _managed.LoadoutIdOf(second), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ARefusedInstallIsAToastWithThePlainMessage()
    {
        ModIsListedUnder(297);
        _installer.Install(Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<LoadoutId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<CatalogInstallOutcome>(new CatalogInstallOutcome.Refused(new Refusal("GameBanana's scan did not clear `a.zip`."))));

        await HandlerFor([Install("TF2", 297)]).Handle(Link, CancellationToken.None);

        TheFailure().Should().BeOfType<FailureReason.Refused>().Which.Message.Should().Be("GameBanana's scan did not clear a.zip.");
        _events.DidNotReceive().Send(Arg.Any<CliMessages.ModInstalled>());
    }

    [Fact]
    public async Task AUserFacingFailureWhileLookingTheModUpIsAToast()
    {
        _installer.GameOf(44334, Arg.Any<CancellationToken>()).Returns<ValueTask<int?>>(_ => throw new Refusal("GameBanana is asking to be left alone."));

        await HandlerFor([Install("TF2", 297)]).Handle(Link, CancellationToken.None);

        TheFailure().Should().BeOfType<FailureReason.Refused>().Which.Message.Should().Contain("left alone");
    }

    [Fact]
    public async Task AnUnexpectedFailureIsReportedAndRethrown()
    {
        ModIsListedUnder(297);
        _installer.Install(Arg.Any<long>(), Arg.Any<long?>(), Arg.Any<LoadoutId>(), Arg.Any<CancellationToken>())
            .Returns<Task<CatalogInstallOutcome>>(_ => throw new InvalidOperationException("boom"));

        var act = async () => await HandlerFor([Install("TF2", 297)]).Handle(Link, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        TheFailure().Should().BeOfType<FailureReason.Unknown>();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheHandlerIsNotRegisteredUntilTheSettingIsOn(bool on)
    {
        var settings = Substitute.For<ISettingsManager>();
        settings.Get<GameBananaHandlerSettings>(Arg.Any<string?>()).Returns(new GameBananaHandlerSettings { RegisterAsDefaultHandler = on });

        HandlerFor([]).IsRegistrationWanted(settings).Should().Be(on);
        new GameBananaHandlerSettings().RegisterAsDefaultHandler.Should().BeFalse("registering takes the scheme from whatever else holds it");
    }

    [Fact]
    public void RegistrationIsReportedOnlyWhenTheSettingTurnsOn()
    {
        var changes = new Subject<GameBananaHandlerSettings>();
        var settings = Substitute.For<ISettingsManager>();
        settings.GetChanges<GameBananaHandlerSettings>(Arg.Any<string?>(), Arg.Any<bool>()).Returns(changes);
        var turnedOn = 0;
        using var _ = HandlerFor([]).RegistrationTurnedOn(settings).Subscribe(_ => turnedOn++);

        changes.OnNext(new GameBananaHandlerSettings { RegisterAsDefaultHandler = true });
        changes.OnNext(new GameBananaHandlerSettings { RegisterAsDefaultHandler = false });

        turnedOn.Should().Be(1);
    }
}
