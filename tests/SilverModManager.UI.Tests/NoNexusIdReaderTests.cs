using DynamicData.Kernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.App.UI.Helpers;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.NexusModsApi;
using NexusMods.StandardGameLocators.TestHelpers.StubbedGames;

namespace NexusMods.UI.Tests;

/// <summary>
/// Places in the UI that read a game's Nexus id have to cope with a game that has none: it offers no Nexus
/// links, owns no Nexus collections and is never the target of an nxm link.
/// </summary>
public class NoNexusIdReaderTests(IServiceProvider provider)
{
    private static readonly NexusModsGameId SharedNexusId = NexusModsGameId.From(4242);

    private static IdentifiedStubbedGame Game(string name, Optional<NexusModsGameId> nexusId) => new(GameId.From(name), nexusId);

    [Fact]
    public void IsOnNexusMods_IsFalseWithoutANexusId()
    {
        new NoNexusIdStubbedGame(provider).IsOnNexusMods().Should().BeFalse();
        Game("HasNexusId", SharedNexusId).IsOnNexusMods().Should().BeTrue();
    }

    [Fact]
    public void IsNexusGame_NeverMatchesWhenThereIsNoNexusId()
    {
        Optional<NexusModsGameId>.None.IsNexusGame(SharedNexusId).Should().BeFalse();
        Optional.Some(SharedNexusId).IsNexusGame(SharedNexusId).Should().BeTrue();
        Optional.Some(SharedNexusId).IsNexusGame(NexusModsGameId.From(1)).Should().BeFalse();
    }

    [Fact]
    public void CollectionsOf_IsEmptyWithoutANexusId()
    {
        var db = provider.GetRequiredService<IConnection>().Db;

        new NoNexusIdStubbedGame(provider).CollectionsOf(db).Should().BeEmpty();
    }

    [Fact]
    public void NxmRouting_PrefersTheGameThatHasALoadout()
    {
        var first = Game("NxmFirst", SharedNexusId);
        var second = Game("NxmSecond", SharedNexusId);
        var logger = new RecordingLogger();

        var picked = NexusModsGameScope.ResolveNxmGame([first, second], SharedNexusId, id => id == second.GameId, logger);

        picked.Value.GameId.Should().Be(second.GameId);
        logger.Messages.Should().BeEmpty("only one of the games has a loadout, so nothing was ambiguous");
    }

    [Fact]
    public void NxmRouting_TakesTheFirstGameAndNamesAllOfThemWhenSeveralHaveLoadouts()
    {
        var first = Game("NxmFirst", SharedNexusId);
        var second = Game("NxmSecond", SharedNexusId);
        var logger = new RecordingLogger();

        var picked = NexusModsGameScope.ResolveNxmGame([first, second], SharedNexusId, _ => true, logger);

        picked.Value.GameId.Should().Be(first.GameId);
        logger.Messages.Should().ContainSingle().Which.Should().Contain("NxmFirst").And.Contain("NxmSecond");
    }

    [Fact]
    public void NxmRouting_TakesTheFirstGameWhenNoneHasALoadout()
    {
        var first = Game("NxmFirst", SharedNexusId);
        var second = Game("NxmSecond", SharedNexusId);

        var picked = NexusModsGameScope.ResolveNxmGame([first, second], SharedNexusId, _ => false, new RecordingLogger());

        picked.Value.GameId.Should().Be(first.GameId);
    }

    [Fact]
    public void NxmRouting_IgnoresGamesWithoutANexusIdAndGamesWithAnotherOne()
    {
        var noId = new NoNexusIdStubbedGame(provider);
        var other = Game("NxmOther", NexusModsGameId.From(1));

        NexusModsGameScope.ResolveNxmGame([noId, other], SharedNexusId, _ => true, new RecordingLogger())
            .HasValue.Should().BeFalse();
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Information) Messages.Add(formatter(state, exception));
        }
    }
}
