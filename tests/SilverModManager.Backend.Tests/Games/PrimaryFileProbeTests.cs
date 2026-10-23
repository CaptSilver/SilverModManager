using FluentAssertions;
using SilverModManager.Backend.Games.Locators;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using NSubstitute;

namespace SilverModManager.Backend.Tests.Games;

public class PrimaryFileProbeTests
{
    private readonly InMemoryFileSystem _fs = new(OSInformation.FakeUnix);
    private readonly RecordingLogger<PrimaryFileProbeTests> _logger = new();

    [Fact]
    public void AGameRelativePrimaryFileIsAnswered()
    {
        var game = new FakeGameData("alpha", primaryFile: "bin/Alpha.exe");

        var file = PrimaryFileProbe.Find(game, _fs, Substitute.For<IGameLocator>(), _logger);

        file.Should().Be((RelativePath)"bin/Alpha.exe");
        _logger.Levels.Should().BeEmpty();
    }

    [Fact]
    public void APrimaryFileInAnotherLocationIsNullWithAWarning()
    {
        var game = Substitute.For<IGameData>();
        game.GetPrimaryFile(Arg.Any<GameInstallation>()).Returns(new GamePath(LocationId.From("Elsewhere"), "Alpha.exe"));

        var file = PrimaryFileProbe.Find(game, _fs, Substitute.For<IGameLocator>(), _logger);

        file.Should().BeNull();
        _logger.Count(Microsoft.Extensions.Logging.LogLevel.Warning).Should().Be(1);
    }

    [Fact]
    public void AGameThatCannotSayWhatItLaunchesIsNullWithAWarning()
    {
        var game = new FakeGameData("alpha");

        var file = PrimaryFileProbe.Find(game, _fs, Substitute.For<IGameLocator>(), _logger);

        file.Should().BeNull();
        _logger.Count(Microsoft.Extensions.Logging.LogLevel.Warning).Should().Be(1);
    }
}
