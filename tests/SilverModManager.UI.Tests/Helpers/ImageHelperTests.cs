using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using SilverModManager.App.UI.Helpers;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.IO;

namespace SilverModManager.UI.Tests.Helpers;

/// <summary>
/// A game's icon or tile that can't be read is decoration gone missing: the card shows without it, and the log entry is
/// a warning, because the app shows an Error-level entry as a "Something went wrong" dialog.
/// </summary>
public class ImageHelperTests
{
    private static IGameData GameWhoseImagesCannotBeRead()
    {
        var unreadable = Substitute.For<IStreamFactory>();
        unreadable.GetStreamAsync().Returns<ValueTask<Stream>>(_ => throw new IOException("image missing"));
        var game = Substitute.For<IGameData>();
        game.DisplayName.Returns("Test Game");
        game.IconImage.Returns(unreadable);
        game.TileImage.Returns(unreadable);
        return game;
    }

    [Fact]
    public async Task AnUnreadableIconGivesNoImageAndAWarning()
    {
        var logger = new RecordingLogger();

        var image = await ImageHelper.LoadGameIconAsync(GameWhoseImagesCannotBeRead(), 64, logger);

        image.Should().BeNull();
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("Test Game"));
        logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task AnUnreadableTileGivesNoImageAndAWarning()
    {
        var logger = new RecordingLogger();

        var image = await ImageHelper.LoadGameTileAsync(GameWhoseImagesCannotBeRead(), 64, logger);

        image.Should().BeNull();
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("Test Game"));
        logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task NoGameGivesNoImageAndNoLogEntry()
    {
        var logger = new RecordingLogger();

        (await ImageHelper.LoadGameIconAsync(null, 64, logger)).Should().BeNull();
        (await ImageHelper.LoadGameTileAsync(null, 64, logger)).Should().BeNull();
        logger.Entries.Should().BeEmpty();
    }

    internal sealed class RecordingLogger : ILogger
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Enqueue((logLevel, formatter(state, exception)));
    }
}
