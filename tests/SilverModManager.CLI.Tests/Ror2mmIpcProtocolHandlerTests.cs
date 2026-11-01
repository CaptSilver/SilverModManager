using System.Collections.Immutable;
using DynamicData.Kernel;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.CLI;
using SilverModManager.CLI.Types.IpcHandlers;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.Thunderstore;
using SilverModManager.Sdk.EventBus;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.NexusModsApi;
using SilverModManager.Sdk.Thunderstore;

namespace SilverModManager.CLI.Tests;

/// <summary>A game that only has a name and a Thunderstore community.</summary>
internal sealed class CommunityGameData(string displayName, string community) : IGameData, IThunderstoreGame
{
    public GameId GameId { get; } = GameId.From(displayName);
    public StoreIdentifiers StoreIdentifiers => new(GameId);
    public string DisplayName { get; } = displayName;
    public Optional<NexusModsGameId> NexusModsGameId => default;
    public IStreamFactory IconImage => throw new NotSupportedException();
    public IStreamFactory TileImage => throw new NotSupportedException();
    public ImmutableDictionary<LocationId, AbsolutePath> GetLocations(IFileSystem fileSystem, GameLocatorResult gameLocatorResult) => throw new NotSupportedException();
    public GamePath GetPrimaryFile(GameInstallation installation) => throw new NotSupportedException();

    public string ThunderstoreCommunity { get; } = community;
    public string ThunderstoreLoaderPackage => "BepInEx-BepInExPack";
    public IReadOnlyList<string> ThunderstoreLoaderAliases => [];
}

/// <summary>
/// <see cref="ManagedGames"/> over a fixed list of installations: each is managed, and the ones in
/// <c>applied</c> have an applied loadout. Every loadout is named after its game.
/// </summary>
internal sealed class ListedManagedGames(IReadOnlyList<GameInstallation> installations, params GameInstallation[] applied)
    : ManagedGames(Substitute.For<IGameRegistry>(), Substitute.For<IConnection>(), Substitute.For<ISynchronizerService>())
{
    private static ulong _nextLoadout = 100;

    public override IReadOnlyList<ManagedGame> Candidates(Func<IGameData, bool> isGame)
        => installations.Where(i => isGame(i.Game)).Select(i => new ManagedGame(i, applied.Contains(i))).ToList();

    public override ManagedLoadout LoadoutOf(GameInstallation installation)
        => new(LoadoutId.From(Interlocked.Increment(ref _nextLoadout)), $"{installation.Game.DisplayName} loadout");
}

/// <summary>
/// The ror2mm handler's decisions, with a fake installer and a fixed list of managed games, so no test reaches the
/// network, the database or the protocol registrar.
/// </summary>
public class Ror2mmIpcProtocolHandlerTests
{
    private const string Link = "ror2mm://v1/install/thunderstore.io/Zehs/REPOLib/4.3.0/";
    private static readonly ThunderstoreVersionRef RepoLib = new(new ThunderstorePackageRef("Zehs", "REPOLib"), new ThunderstoreVersionNumber(4, 3, 0));

    private readonly IEventBus _events = Substitute.For<IEventBus>();
    private readonly IThunderstoreInstaller _installer = Substitute.For<IThunderstoreInstaller>();

    private static GameInstallation Install(string name, string community) => new(
        new GameLocatorResult
        {
            Game = new CommunityGameData(name, community),
            Path = default,
            Store = GameStore.Unknown,
            StoreIdentifier = name,
            LocatorIds = [],
            Locator = Substitute.For<IGameLocator>(),
        },
        default);

    private Ror2mmIpcProtocolHandler HandlerFor(IReadOnlyList<GameInstallation> installations, params GameInstallation[] applied)
        => new(NullLogger<Ror2mmIpcProtocolHandler>.Instance, _events, _installer, new ListedManagedGames(installations, applied));

    private void PackageIsListedUnder(params string[] communities)
        => _installer.CommunitiesOf(RepoLib.Package, Arg.Any<CancellationToken>()).Returns(new ValueTask<IReadOnlyList<string>>(communities));

    private void InstallSucceeds()
        => _installer.InstallAsync(Arg.Any<ThunderstoreVersionRef>(), Arg.Any<LoadoutId>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyList<ThunderstoreInstallOutcome>>([
                new ThunderstoreInstallOutcome(RepoLib, default, true, LoadoutPackageOutcome.Installed),
            ]));

    private Task InstallThrows(Exception e)
    {
        _installer.InstallAsync(Arg.Any<ThunderstoreVersionRef>(), Arg.Any<LoadoutId>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask<IReadOnlyList<ThunderstoreInstallOutcome>>>(_ => throw e);
        return Task.CompletedTask;
    }

    private IFailureReason TheFailure()
        => _events.ReceivedCalls().Select(c => c.GetArguments()[0]).OfType<CliMessages.ModDownloadFailed>().Single().Reason;

    [Fact]
    public void TheHandlerOwnsTheRor2mmScheme()
    {
        HandlerFor([]).Protocol.Should().Be("ror2mm");
    }

    [Fact]
    public async Task OneManagedGameForTheCommunityInstallsTheLinkedVersionIntoItsLoadout()
    {
        var repo = Install("REPO", "repo");
        var other = Install("Other", "valheim");
        PackageIsListedUnder("repo");
        InstallSucceeds();

        await HandlerFor([other, repo]).Handle(Link, CancellationToken.None);

        await _installer.Received(1).InstallAsync(RepoLib, Arg.Any<LoadoutId>(), Arg.Any<CancellationToken>());
        _events.Received(1).Send(Arg.Any<CliMessages.ModDownloadStarted>());
        _events.Received(1).Send(Arg.Any<CliMessages.ModDownloadSucceeded>());
        _events.Received(1).Send(Arg.Is<CliMessages.ModInstalled>(m => m.LoadoutName == "REPO loadout"));
        _events.DidNotReceive().Send(Arg.Any<CliMessages.ModDownloadFailed>());
    }

    [Fact]
    public async Task ALinkThatIsNotAnInstallLinkIsRefusedWithAMessageQuotingIt()
    {
        const string bad = "ror2mm://v2/install/thunderstore.io/Zehs/REPOLib/4.3.0/";

        await HandlerFor([Install("REPO", "repo")]).Handle(bad, CancellationToken.None);

        TheFailure().Should().BeOfType<FailureReason.Refused>().Which.Message.Should().Contain(bad);
        await _installer.DidNotReceiveWithAnyArgs().InstallAsync(default, default, default);
        _events.DidNotReceive().Send(Arg.Any<CliMessages.ModDownloadStarted>());
    }

    [Fact]
    public async Task TextThatIsNotAUriIsRefusedWithAMessageQuotingIt()
    {
        await HandlerFor([]).Handle("not a link", CancellationToken.None);

        TheFailure().Should().BeOfType<FailureReason.Refused>().Which.Message.Should().Contain("not a link");
    }

    [Fact]
    public async Task APackageThunderstoreDoesNotKnowIsRefused()
    {
        PackageIsListedUnder();

        await HandlerFor([Install("REPO", "repo")]).Handle(Link, CancellationToken.None);

        TheFailure().Should().BeOfType<FailureReason.Refused>().Which.Message.Should().Contain("Zehs-REPOLib");
        await _installer.DidNotReceiveWithAnyArgs().InstallAsync(default, default, default);
    }

    [Fact]
    public async Task NoManagedGameForTheCommunitiesIsReportedNamingThem()
    {
        PackageIsListedUnder("repo", "lethal-company");

        await HandlerFor([Install("Valheim", "valheim")]).Handle(Link, CancellationToken.None);

        TheFailure().Should().BeOfType<FailureReason.GameNotManaged>().Which.Game.Should().Be("repo, lethal-company");
        await _installer.DidNotReceiveWithAnyArgs().InstallAsync(default, default, default);
        _events.DidNotReceive().Send(Arg.Any<CliMessages.ModDownloadStarted>());
    }

    [Fact]
    public async Task APackageInTwoManagedGamesGoesToTheOneWithAnAppliedLoadout()
    {
        var repo = Install("REPO", "repo");
        var lethal = Install("Lethal Company", "lethal-company");
        PackageIsListedUnder("repo", "lethal-company");
        InstallSucceeds();

        await HandlerFor([repo, lethal], lethal).Handle(Link, CancellationToken.None);

        await _installer.Received(1).InstallAsync(RepoLib, Arg.Any<LoadoutId>(), Arg.Any<CancellationToken>());
        _events.Received(1).Send(Arg.Is<CliMessages.ModInstalled>(m => m.LoadoutName == "Lethal Company loadout"));
    }

    [Fact]
    public async Task APackageInTwoManagedGamesWithTwoAppliedLoadoutsIsAmbiguousAndInstallsNothing()
    {
        var repo = Install("REPO", "repo");
        var lethal = Install("Lethal Company", "lethal-company");
        PackageIsListedUnder("repo", "lethal-company");

        await HandlerFor([repo, lethal], repo, lethal).Handle(Link, CancellationToken.None);

        var ambiguous = TheFailure().Should().BeOfType<FailureReason.Ambiguous>().Subject;
        ambiguous.Package.Should().Be("Zehs-REPOLib");
        ambiguous.Games.Should().Equal("REPO", "Lethal Company");
        await _installer.DidNotReceiveWithAnyArgs().InstallAsync(default, default, default);
    }

    [Fact]
    public async Task APackageInTwoManagedGamesWithNoAppliedLoadoutIsAmbiguous()
    {
        PackageIsListedUnder("repo", "lethal-company");

        await HandlerFor([Install("REPO", "repo"), Install("Lethal Company", "lethal-company")]).Handle(Link, CancellationToken.None);

        TheFailure().Should().BeOfType<FailureReason.Ambiguous>();
        await _installer.DidNotReceiveWithAnyArgs().InstallAsync(default, default, default);
    }

    [Fact]
    public async Task TwoInstallationsOfTheSameGameWithOneAppliedLoadoutPickTheAppliedOne()
    {
        var first = Install("REPO", "repo");
        var second = Install("REPO", "repo");
        PackageIsListedUnder("repo");
        InstallSucceeds();

        await HandlerFor([first, second], second).Handle(Link, CancellationToken.None);

        await _installer.Received(1).InstallAsync(RepoLib, Arg.Any<LoadoutId>(), Arg.Any<CancellationToken>());
        _events.DidNotReceive().Send(Arg.Any<CliMessages.ModDownloadFailed>());
    }

    [Fact]
    public async Task ARefusalFromTheInstallerIsReportedWithItsMessage()
    {
        PackageIsListedUnder("repo");
        await InstallThrows(new DependencyResolutionException("Zehs-REPOLib needs a package that does not exist."));

        await HandlerFor([Install("REPO", "repo")]).Handle(Link, CancellationToken.None);

        TheFailure().Should().BeOfType<FailureReason.Refused>().Which.Message.Should().Be("Zehs-REPOLib needs a package that does not exist.");
        _events.DidNotReceive().Send(Arg.Any<CliMessages.ModInstalled>());
    }

    [Fact]
    public async Task ADisabledGroupRefusalIsReportedWithItsMessage()
    {
        PackageIsListedUnder("repo");
        var refusal = new ThunderstorePackageInDisabledGroupException("BepInEx-BepInExPack", "Packs");
        await InstallThrows(refusal);

        await HandlerFor([Install("REPO", "repo")]).Handle(Link, CancellationToken.None);

        TheFailure().Should().BeOfType<FailureReason.Refused>().Which.Message.Should().Be(refusal.Message);
    }

    [Fact]
    public async Task AnUnexpectedFailureIsReportedAsUnknownAndRethrown()
    {
        PackageIsListedUnder("repo");
        var crash = new InvalidOperationException("boom");
        await InstallThrows(crash);

        var act = () => HandlerFor([Install("REPO", "repo")]).Handle(Link, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(crash);
        TheFailure().Should().BeOfType<FailureReason.Unknown>().Which.Exception.Should().BeSameAs(crash);
    }
}
