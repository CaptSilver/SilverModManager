using System.Runtime.InteropServices;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NexusMods.Backend.Games.Locators;
using NexusMods.Paths;
using NexusMods.Sdk.Games;

namespace NexusMods.Backend.Tests.Games;

public class UserInstallLocatorTests
{
    private readonly InMemoryFileSystem _fs = new(OSInformation.FakeUnix);
    private readonly RecordingLogger<UserInstallLocator> _logger = new();
    private readonly IGameData _alpha = new FakeGameData("alpha");
    private readonly IGameData _beta = new FakeGameData("beta");

    private sealed class FixedStore(params UserGameInstall[] installs) : IUserGameInstallStore
    {
        public IReadOnlyList<UserGameInstall> GetAll() => installs;
        public void Add(UserGameInstall install) => throw new NotSupportedException();
    }

    private IEnumerable<GameLocatorResult> Locate(params UserGameInstall[] installs)
        => new UserInstallLocator(_logger, new FixedStore(installs), _fs, [_alpha, _beta]).Locate().ToArray();

    private string Folder(string path)
    {
        _fs.FromUnsanitizedFullPath(path).CreateDirectory();
        return path;
    }

    [Fact]
    public void EachRecordYieldsAResultForItsGame()
    {
        var results = Locate(
            new UserGameInstall(GameId.From("alpha"), Folder("/games/alpha"), OSPlatform.Linux),
            new UserGameInstall(GameId.From("beta"), Folder("/games/beta"), OSPlatform.Linux)).ToArray();

        results.Select(result => result.Game).Should().Equal(_alpha, _beta);
        results.Should().OnlyContain(result => result.Store == GameStore.ManuallyAdded && result.LocatorIds.IsEmpty);
        results[0].StoreIdentifier.Should().Be("/games/alpha");
        results[0].Path.Should().Be(_fs.FromUnsanitizedFullPath("/games/alpha"));
    }

    [Fact]
    public void PlatformComesFromTheRecord()
    {
        var results = Locate(
            new UserGameInstall(GameId.From("alpha"), Folder("/games/linux"), OSPlatform.Linux),
            new UserGameInstall(GameId.From("alpha"), Folder("/games/windows"), OSPlatform.Windows)).ToArray();

        results.Select(result => result.Platform).Should().Equal(OSPlatform.Linux, OSPlatform.Windows);
    }

    [Fact]
    public void ARecordedPrefix_BecomesAPlainWineLayer()
    {
        var result = Locate(new UserGameInstall(GameId.From("alpha"), Folder("/games/alpha"), OSPlatform.Windows,
            Prefix: "/prefixes/alpha", Wine: "/opt/wine/bin/wine")).Single();

        result.Compatibility.Should().Be(new CompatibilityLayer(
            CompatibilityLayerKind.PlainWine,
            _fs.FromUnsanitizedFullPath("/prefixes/alpha"),
            _fs.FromUnsanitizedFullPath("/opt/wine/bin/wine")));
        result.WinePrefixPath.Should().Be(_fs.FromUnsanitizedFullPath("/prefixes/alpha"));
    }

    [Fact]
    public void NoRecordedPrefix_MeansNoCompatibilityLayer()
    {
        var result = Locate(new UserGameInstall(GameId.From("alpha"), Folder("/games/alpha"), OSPlatform.Windows)).Single();

        result.Compatibility.Should().BeNull();
        result.WinePrefixPath.Should().BeNull();
    }

    [Fact]
    public void UnknownGame_IsSkippedWithAWarningAndTheRestStillLocate()
    {
        var results = Locate(
            new UserGameInstall(GameId.From("gone"), Folder("/games/gone"), OSPlatform.Linux),
            new UserGameInstall(GameId.From("alpha"), Folder("/games/alpha"), OSPlatform.Linux)).ToArray();

        results.Should().ContainSingle().Which.Game.Should().BeSameAs(_alpha);
        _logger.Count(LogLevel.Warning).Should().BeGreaterThan(0);
    }

    [Fact]
    public void MissingFolder_IsSkippedWithAWarning()
    {
        var results = Locate(new UserGameInstall(GameId.From("alpha"), "/unplugged/drive/alpha", OSPlatform.Linux));

        results.Should().BeEmpty();
        _logger.Count(LogLevel.Warning).Should().BeGreaterThan(0);
    }
}
