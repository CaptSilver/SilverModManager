using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.MnemonicDB.Abstractions;
using DynamicData.Kernel;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.NexusModsApi;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;
using SilverModManager.Sdk.ProxyConsole;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;
using NSubstitute;

namespace SilverModManager.CLI.Tests;

public class LoadoutParserTests(IServiceProvider provider)
{
    [Fact]
    public async Task NameLookup_IgnoresDeletedLoadoutWithTheSameName()
    {
        var connection = provider.GetRequiredService<IConnection>();
        var name = "Parser Twin " + Guid.NewGuid();

        using var tx = connection.BeginTransaction();
        var metadata = new GameInstallMetadata.New(tx)
        {
            Path = "/tmp/parser-tests-" + Guid.NewGuid(),
            Name = "Parser Test Game",
            Store = GameStore.Unknown,
            GameId = NexusModsGameId.From(1),
        };
        var live = NewLoadout(tx, metadata, name, "LIV", LoadoutKind.Default);
        NewLoadout(tx, metadata, name, "DEL", LoadoutKind.Deleted);
        var result = await tx.Commit();

        var parser = provider.GetRequiredService<IOptionParser<Loadout.ReadOnly>>();

        parser.TryParse(name, out var parsed, out _).Should().BeTrue();
        parsed.LoadoutId.Should().Be(result.Remap(live).LoadoutId);
    }

    [Fact]
    public async Task ShortNameLookup_IgnoresDeletedLoadoutWithTheSameShortName()
    {
        var connection = provider.GetRequiredService<IConnection>();
        var gameId = NexusModsGameId.From(uint.MaxValue - 1);
        var shortName = "S" + Guid.NewGuid().ToString("N")[..8];

        // The deleted twin goes in first, so a lookup that takes the first match by short name would pick it.
        using var tx = connection.BeginTransaction();
        var metadata = new GameInstallMetadata.New(tx)
        {
            Path = "/tmp/parser-tests-" + Guid.NewGuid(),
            Name = "Parser Test Game",
            Store = GameStore.Unknown,
            Game = ParserTestGame,
            GameId = gameId,
        };
        NewLoadout(tx, metadata, "Short Deleted", shortName, LoadoutKind.Deleted);
        var live = NewLoadout(tx, metadata, "Short Live", shortName, LoadoutKind.Default);
        var result = await tx.Commit();

        var parser = ParserWithGame(connection, "parser-test-game", ParserTestGame, gameId);

        parser.TryParse($"parser-test-game/{shortName}", out var parsed, out _).Should().BeTrue();
        parsed.LoadoutId.Should().Be(result.Remap(live).LoadoutId);
    }

    /// <summary>
    /// The test host registers no games, so the short-name form needs a game parser that knows one. The parser
    /// class is internal, hence building it from the registered instance's type.
    /// </summary>
    private IOptionParser<Loadout.ReadOnly> ParserWithGame(IConnection connection, string gameName, GameId id, Optional<NexusModsGameId> nexusId)
    {
        var game = new IdentifiedStubbedGame(id, nexusId);

        var gameParser = Substitute.For<IOptionParser<IGame>>();
        gameParser.TryParse(gameName, out Arg.Any<IGame>(), out Arg.Any<string>())
            .Returns(call =>
            {
                call[1] = game;
                call[2] = string.Empty;
                return true;
            });

        var parserType = provider.GetRequiredService<IOptionParser<Loadout.ReadOnly>>().GetType();
        return (IOptionParser<Loadout.ReadOnly>)ActivatorUtilities.CreateInstance(provider, parserType, connection, gameParser);
    }

    [Fact]
    public async Task ShortNameLookup_FindsTheLoadoutOfAGameWithoutANexusId()
    {
        var connection = provider.GetRequiredService<IConnection>();
        var shortName = "N" + Guid.NewGuid().ToString("N")[..8];
        var noNexusGame = GameId.From("ParserNoNexusGame-" + Guid.NewGuid());

        using var tx = connection.BeginTransaction();
        var metadata = new GameInstallMetadata.New(tx)
        {
            Path = "/tmp/parser-tests-" + Guid.NewGuid(),
            Name = "Parser Game Without Nexus Id",
            Store = GameStore.Unknown,
            Game = noNexusGame,
        };
        var live = NewLoadout(tx, metadata, "No Nexus Live", shortName, LoadoutKind.Default);
        var result = await tx.Commit();

        var parser = ParserWithGame(connection, "no-nexus-game", noNexusGame, Optional<NexusModsGameId>.None);

        parser.TryParse($"no-nexus-game/{shortName}", out var parsed, out _).Should().BeTrue();
        parsed.LoadoutId.Should().Be(result.Remap(live).LoadoutId);
    }

    [Fact]
    public async Task ShortNameLookup_DoesNotPickASiblingGameThatSharesTheNexusId()
    {
        var connection = provider.GetRequiredService<IConnection>();
        var shortName = "V" + Guid.NewGuid().ToString("N")[..8];
        var sharedNexusId = NexusModsGameId.From(uint.MaxValue - 2);
        var sibling = GameId.From("ParserSibling-" + Guid.NewGuid());
        var wanted = GameId.From("ParserWanted-" + Guid.NewGuid());

        // The sibling goes in first, so a lookup on the shared Nexus id would take its loadout.
        using var tx = connection.BeginTransaction();
        var siblingMetadata = new GameInstallMetadata.New(tx)
        {
            Path = "/tmp/parser-tests-" + Guid.NewGuid(),
            Name = "Sibling",
            Store = GameStore.Unknown,
            Game = sibling,
            GameId = sharedNexusId,
        };
        var wantedMetadata = new GameInstallMetadata.New(tx)
        {
            Path = "/tmp/parser-tests-" + Guid.NewGuid(),
            Name = "Wanted",
            Store = GameStore.Unknown,
            Game = wanted,
            GameId = sharedNexusId,
        };
        NewLoadout(tx, siblingMetadata, "Sibling Loadout", shortName, LoadoutKind.Default);
        var live = NewLoadout(tx, wantedMetadata, "Wanted Loadout", shortName, LoadoutKind.Default);
        var result = await tx.Commit();

        var parser = ParserWithGame(connection, "wanted-game", wanted, sharedNexusId);

        parser.TryParse($"wanted-game/{shortName}", out var parsed, out _).Should().BeTrue();
        parsed.LoadoutId.Should().Be(result.Remap(live).LoadoutId);
    }

    [Theory]
    [InlineData("999999")]
    [InlineData("No Such Loadout")]
    [InlineData("LoadoutId:zzzzzzzzzzzzzzz")]
    public void AnInputThatMatchesNoLoadout_ReturnsFalseWithAnErrorNamingIt(string input)
    {
        var parser = provider.GetRequiredService<IOptionParser<Loadout.ReadOnly>>();

        parser.TryParse(input, out _, out var error).Should().BeFalse();
        error.Should().Contain(input).And.Contain("loadouts list");
    }

    [Fact]
    public async Task ANameTwoLoadoutsShare_ReturnsFalseWithAnErrorSayingToUseTheId()
    {
        var connection = provider.GetRequiredService<IConnection>();
        var name = "Parser Pair " + Guid.NewGuid();

        using var tx = connection.BeginTransaction();
        var metadata = new GameInstallMetadata.New(tx)
        {
            Path = "/tmp/parser-tests-" + Guid.NewGuid(),
            Name = "Parser Test Game",
            Store = GameStore.Unknown,
            GameId = NexusModsGameId.From(1),
        };
        NewLoadout(tx, metadata, name, "PA1", LoadoutKind.Default);
        NewLoadout(tx, metadata, name, "PA2", LoadoutKind.Default);
        await tx.Commit();

        var parser = provider.GetRequiredService<IOptionParser<Loadout.ReadOnly>>();

        parser.TryParse(name, out _, out var error).Should().BeFalse();
        error.Should().Contain(name).And.Contain("id");
    }

    private static readonly GameId ParserTestGame = GameId.From("ParserTestGame");

    private static Loadout.New NewLoadout(ITransaction tx, GameInstallMetadata.New metadata, string name, string shortName, LoadoutKind kind) => new(tx)
    {
        Name = name,
        ShortName = shortName,
        InstallationId = metadata,
        LoadoutKind = kind,
        Revision = 0,
        GameVersion = VanityVersion.From("Unknown"),
    };
}
