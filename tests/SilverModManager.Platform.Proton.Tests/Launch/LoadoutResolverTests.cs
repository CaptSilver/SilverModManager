using System.Collections.Immutable;
using System.Runtime.InteropServices;
using FluentAssertions;
using NexusMods.Abstractions.Games;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Platform.Proton.Launch;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using NexusMods.Sdk.NexusModsApi;
using NSubstitute;
using Loadout = NexusMods.Sdk.Loadouts.Loadout;

namespace NexusMods.Platform.Proton.Tests.Launch;

public class LoadoutResolverTests(IConnection connection)
{
    private sealed record Seeded(GameInstallation Installation, EntityId MetadataId);

    private readonly string _unique = Guid.NewGuid().ToString("N")[..8];
    private readonly List<Seeded> _installations = [];
    private readonly Dictionary<string, string?> _env = [];

    private string Name(string name) => $"{name} {_unique}";

    /// <summary>
    /// Adds an installation (store id, game name) with the given loadouts (name, kind) to the database. The
    /// short names are generated, because tests that need to read them seed them explicitly.
    /// </summary>
    private async Task<Seeded> Install(string appId, string game, params (string Name, LoadoutKind Kind)[] loadouts)
    {
        var gameData = Substitute.For<IGameData>();
        gameData.DisplayName.Returns(game);
        var folder = FileSystem.Shared.FromUnsanitizedFullPath($"/tmp/silvermod-resolver-{_unique}-{appId}");
        var result = new GameLocatorResult
        {
            Game = gameData,
            Path = folder,
            Store = GameStore.Steam,
            StoreIdentifier = appId,
            LocatorIds = ImmutableArray<LocatorId>.Empty,
            Locator = Substitute.For<IGameLocator>(),
            Platform = OSPlatform.Windows,
        };
        var installation = new GameInstallation(result, GameLocations.Create(ImmutableDictionary<LocationId, AbsolutePath>.Empty.Add(LocationId.Game, folder)));

        using var tx = connection.BeginTransaction();
        var metadata = new GameInstallMetadata.New(tx)
        {
            Path = folder.GetFullPath(),
            Name = game,
            Store = GameStore.Steam,
            GameId = NexusModsGameId.From(1),
        };
        var index = 0;
        foreach (var (name, kind) in loadouts)
        {
            _ = new Loadout.New(tx)
            {
                Name = name,
                ShortName = ((char)('A' + index++)).ToString(),
                InstallationId = metadata,
                LoadoutKind = kind,
                Revision = 0,
                GameVersion = VanityVersion.From("Unknown"),
            };
        }
        var committed = await tx.Commit();

        var seeded = new Seeded(installation, committed.Remap(metadata).Id);
        _installations.Add(seeded);
        return seeded;
    }

    private LoadoutResolver Resolver()
    {
        var registry = Substitute.For<IGameRegistry>();
        registry.LocateGameInstallations().Returns(_ => _installations.Select(i => i.Installation).ToImmutableArray());
        registry.TryGetMetadata(Arg.Any<GameInstallation>(), out Arg.Any<GameInstallMetadata.ReadOnly>())
            .Returns(call =>
            {
                var installation = call.ArgAt<GameInstallation>(0);
                var match = _installations.FirstOrDefault(i => ReferenceEquals(i.Installation, installation));
                call[1] = match is null ? default : GameInstallMetadata.Load(connection.Db, match.MetadataId);
                return match is not null;
            });

        var environment = Substitute.For<IEnvironmentReader>();
        environment.Get(Arg.Any<string>()).Returns(call => _env.GetValueOrDefault(call.ArgAt<string>(0)));
        return new LoadoutResolver(connection, registry, environment);
    }

    private static LaunchArgs Args(string name, string? game = null, params string[] command)
        => new(name, game, command.Length > 0 ? command : ["game.exe"]);

    [Fact]
    public async Task ResolvesTheOnlyLoadoutWithThatName()
    {
        var seeded = await Install("100", "Alpha", (Name("Main"), LoadoutKind.Default), (Name("Other"), LoadoutKind.Default));

        var resolution = Resolver().Resolve(Args(Name("Main")));

        resolution.IsResolved.Should().BeTrue(resolution.Error);
        resolution.ExitCode.Should().Be(0);
        resolution.Loadout!.Value.Name.Should().Be(Name("Main"));
        resolution.Installation.Should().BeSameAs(seeded.Installation);
    }

    [Fact]
    public async Task MatchesTrimmedAndIgnoringCase()
    {
        await Install("100", "Alpha", (Name("Main"), LoadoutKind.Default));

        var resolution = Resolver().Resolve(Args($"  {Name("main").ToUpperInvariant()}\t"));

        resolution.IsResolved.Should().BeTrue(resolution.Error);
    }

    [Theory]
    [InlineData(LoadoutKind.Deleted)]
    [InlineData(LoadoutKind.VanillaState)]
    public async Task NeverMatchesALoadoutTheUserCannotSee(LoadoutKind kind)
    {
        await Install("100", "Alpha", (Name("Hidden"), kind), (Name("Visible"), LoadoutKind.Default));

        var resolution = Resolver().Resolve(Args(Name("Hidden")));

        resolution.IsResolved.Should().BeFalse();
        resolution.ExitCode.Should().Be(125);
        resolution.Error.Should().Contain(Name("Visible"));
        // The hidden name appears once, as the name that was asked for, and is not offered as an alternative.
        resolution.Error!.Split(Name("Hidden")).Should().HaveCount(2);
    }

    [Fact]
    public async Task HiddenLoadoutDoesNotMakeAVisibleNameAmbiguous()
    {
        await Install("100", "Alpha", (Name("Twin"), LoadoutKind.Deleted), (Name("Twin"), LoadoutKind.Default));

        Resolver().Resolve(Args(Name("Twin"))).IsResolved.Should().BeTrue();
    }

    [Fact]
    public async Task SteamAppIdFromTheEnvironmentScopesTheSearch()
    {
        var first = await Install("100", "Alpha", (Name("Main"), LoadoutKind.Default));
        var second = await Install("200", "Beta", (Name("Main"), LoadoutKind.Default));
        _env["SteamAppId"] = "200";

        var resolution = Resolver().Resolve(Args(Name("Main")));

        resolution.IsResolved.Should().BeTrue(resolution.Error);
        resolution.Installation.Should().BeSameAs(second.Installation).And.NotBeSameAs(first.Installation);
    }

    [Fact]
    public async Task AppIdInTheCommandIsTheSecondSource()
    {
        await Install("100", "Alpha", (Name("Main"), LoadoutKind.Default));
        var second = await Install("200", "Beta", (Name("Main"), LoadoutKind.Default));
        string[] command = ["/usr/bin/reaper", "SteamLaunch", "AppId=200", "--", "proton", "waitforexitandrun", "game.exe"];

        var resolution = Resolver().Resolve(Args(Name("Main"), null, command));

        resolution.IsResolved.Should().BeTrue(resolution.Error);
        resolution.Installation.Should().BeSameAs(second.Installation);
    }

    [Fact]
    public async Task EnvironmentBeatsTheCommandAndTheCommandBeatsTheGameFlag()
    {
        var first = await Install("100", "Alpha", (Name("Main"), LoadoutKind.Default));
        var second = await Install("200", "Beta", (Name("Main"), LoadoutKind.Default));
        var third = await Install("300", "Gamma", (Name("Main"), LoadoutKind.Default));
        string[] command = ["reaper", "SteamLaunch", "AppId=200", "--", "game.exe"];

        _env["SteamAppId"] = "100";
        Resolver().Resolve(Args(Name("Main"), "300", command)).Installation.Should().BeSameAs(first.Installation);

        _env.Clear();
        Resolver().Resolve(Args(Name("Main"), "300", command)).Installation.Should().BeSameAs(second.Installation);

        Resolver().Resolve(Args(Name("Main"), "300")).Installation.Should().BeSameAs(third.Installation);
    }

    [Fact]
    public async Task GameFlagAcceptsAnAppIdOrANameIgnoringCase()
    {
        var first = await Install("100", "Alpha Game", (Name("Main"), LoadoutKind.Default));
        var second = await Install("200", "Beta Game", (Name("Main"), LoadoutKind.Default));

        Resolver().Resolve(Args(Name("Main"), "200")).Installation.Should().BeSameAs(second.Installation);
        Resolver().Resolve(Args(Name("Main"), " alpha GAME ")).Installation.Should().BeSameAs(first.Installation);
    }

    [Fact]
    public async Task UnknownGame_IsAnErrorNamingIt()
    {
        await Install("100", "Alpha", (Name("Main"), LoadoutKind.Default));

        var byFlag = Resolver().Resolve(Args(Name("Main"), "Nonexistent"));
        byFlag.IsResolved.Should().BeFalse();
        byFlag.ExitCode.Should().Be(125);
        byFlag.Error.Should().Contain("Nonexistent");

        _env["SteamAppId"] = "999";
        var byEnvironment = Resolver().Resolve(Args(Name("Main")));
        byEnvironment.IsResolved.Should().BeFalse();
        byEnvironment.Error.Should().Contain("999");
    }

    [Fact]
    public async Task ZeroMatches_ListsThatGamesNamesAndTellsWhereToFixTheName()
    {
        await Install("100", "Alpha", (Name("One"), LoadoutKind.Default), (Name("Two"), LoadoutKind.Default));
        await Install("200", "Beta", (Name("Elsewhere"), LoadoutKind.Default));
        _env["SteamAppId"] = "100";

        var resolution = Resolver().Resolve(Args(Name("Missing")));

        resolution.IsResolved.Should().BeFalse();
        resolution.ExitCode.Should().Be(125);
        resolution.Error.Should().Contain(Name("Missing"))
            .And.Contain("Alpha")
            .And.Contain(Name("One")).And.Contain(Name("Two"))
            .And.NotContain(Name("Elsewhere"))
            .And.Contain("Fix the name in Steam > Properties > Launch Options");
    }

    [Fact]
    public async Task ZeroMatchesWithoutAScope_ListsEveryGamesNames()
    {
        await Install("100", "Alpha", (Name("One"), LoadoutKind.Default));
        await Install("200", "Beta", (Name("Two"), LoadoutKind.Default));

        var resolution = Resolver().Resolve(Args(Name("Missing")));

        resolution.Error.Should().Contain($"Alpha: {Name("One")}").And.Contain($"Beta: {Name("Two")}");
    }

    [Fact]
    public async Task SeveralGames_ListGameAndNameAndPointAtTheGameFlag()
    {
        await Install("100", "Alpha", (Name("Main"), LoadoutKind.Default));
        await Install("200", "Beta", (Name("Main"), LoadoutKind.Default));

        var resolution = Resolver().Resolve(Args(Name("Main")));

        resolution.IsResolved.Should().BeFalse();
        resolution.ExitCode.Should().Be(125);
        resolution.Error.Should().Contain($"Alpha: {Name("Main")}").And.Contain($"Beta: {Name("Main")}")
            .And.Contain("--game");
    }

    [Fact]
    public async Task TwoInOneInstallation_NameTheShortNamesAndAskForARename()
    {
        await Install("100", "Alpha", (Name("Main"), LoadoutKind.Default), (Name("main"), LoadoutKind.Default));
        _env["SteamAppId"] = "100";

        var resolution = Resolver().Resolve(Args(Name("Main")));

        resolution.IsResolved.Should().BeFalse();
        resolution.ExitCode.Should().Be(125);
        resolution.Error.Should().Contain("short names A, B").And.Contain("rename one in SilverModManager");
    }

    [Fact]
    public async Task DuplicateInOneInstallation_IsReportedEvenWhenAnotherGameHasTheName()
    {
        await Install("100", "Alpha", (Name("Main"), LoadoutKind.Default), (Name("Main"), LoadoutKind.Default));
        await Install("200", "Beta", (Name("Main"), LoadoutKind.Default));

        Resolver().Resolve(Args(Name("Main"))).Error.Should().Contain("rename one in SilverModManager");
    }
}
