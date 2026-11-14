using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Paths;
using NSubstitute;
using R3;
using SilverModManager.Library.ModIo;
using SilverModManager.Library.Pickup;
using SilverModManager.Networking.ModIo;
using SilverModManager.Sdk.Library;
using Xunit;

namespace SilverModManager.Library.Tests.ModIo;

public class ModIoPickupsTests : IDisposable
{
    private readonly AbsolutePath _downloads = FileSystem.Shared.FromUnsanitizedFullPath(Directory.CreateTempSubdirectory("modio-pickup-").FullName);

    public void Dispose() => Directory.Delete(_downloads.ToString(), recursive: true);

    private sealed class OptedIn(string folder) : IPickupSettings
    {
        public bool PickUpFromDownloads => true;
        public string DownloadsFolder => folder;
        public Observable<Unit> Changes { get; } = new Subject<Unit>();
    }

    private static ModIoFile File() => new(88001, 4101, 0, 2048, new ModIoFileHash("180f264ed68955adf16924b9a7c29fd6"), "wider-camera.zip", "1.2", null, 1, 0);

    [Fact]
    public void ArmingStartsTheDownloadsPickupWatching()
    {
        // Until something follows the expectations, the pickup watches nothing, whatever is armed.
        var expected = new ExpectedDownloads();
        using var watcher = new DownloadsPickup(new OptedIn(_downloads.ToString()), expected, FileSystem.Shared, NullLogger<DownloadsPickup>.Instance);
        var provider = new ServiceCollection().AddSingleton(watcher).BuildServiceProvider();
        var pickups = new ModIoPickups(provider, expected, NullLogger<ModIoPickups>.Instance);

        using var armed = pickups.Arm(6715, 4101, File(), 0, Substitute.For<IAcquisitionContext>());

        watcher.IsWatching.Should().BeTrue();
        watcher.WatchedFolder.Should().Be(_downloads);
    }

    [Fact]
    public void AHostWithoutTheDownloadsPickupSaysNothingWillCollectTheFile()
    {
        var expected = new ExpectedDownloads();
        var logger = new RecordingLogger();
        var pickups = new ModIoPickups(new ServiceCollection().BuildServiceProvider(), expected, logger);

        using var armed = pickups.Arm(6715, 4101, File(), 0, Substitute.For<IAcquisitionContext>());

        logger.Lines.Should().ContainSingle(line => line.Level == LogLevel.Warning).Which.Text.Should().Contain("Downloads");
    }

    private sealed class RecordingLogger : ILogger<ModIoPickups>
    {
        public List<(LogLevel Level, string Text)> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add((logLevel, formatter(state, exception)));
    }
}
