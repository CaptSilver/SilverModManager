using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using NexusMods.Sdk.NexusModsApi;
using Loadout = NexusMods.Sdk.Loadouts.Loadout;
using NexusMods.Sdk.ProxyConsole;
using NSubstitute;

namespace NexusMods.CLI.Tests;

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
            GameId = gameId,
        };
        NewLoadout(tx, metadata, "Short Deleted", shortName, LoadoutKind.Deleted);
        var live = NewLoadout(tx, metadata, "Short Live", shortName, LoadoutKind.Default);
        var result = await tx.Commit();

        var parser = ParserWithGame(connection, "parser-test-game", gameId);

        parser.TryParse($"parser-test-game/{shortName}", out var parsed, out _).Should().BeTrue();
        parsed.LoadoutId.Should().Be(result.Remap(live).LoadoutId);
    }

    /// <summary>
    /// The test host registers no games, so the short-name form needs a game parser that knows one. The parser
    /// class is internal, hence building it from the registered instance's type.
    /// </summary>
    private IOptionParser<Loadout.ReadOnly> ParserWithGame(IConnection connection, string gameName, NexusModsGameId gameId)
    {
        var game = Substitute.For<IGame>();
        game.NexusModsGameId.Returns(gameId);

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
