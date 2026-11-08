using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Wabbajack.Diagnostics;
using SilverModManager.Wabbajack.Gallery;
using SilverModManager.Wabbajack.Tests.Gallery;
using static SilverModManager.Wabbajack.Tests.Gallery.GalleryLibraryFiles;

namespace SilverModManager.Wabbajack.Tests.Diagnostics;

public class UpdateAvailableTests(ITestOutputHelper helper) : UpdateCheckTestBase<UpdateAvailableTests>(helper)
{
    private static GalleryEntry Newer(string title, string machineUrl) => GalleryLibraryFiles.Entry(title, machineUrl: machineUrl) with { Version = "2.0.0.0", ArchiveCount = 3, ArchivesSize = 1_500_000_000UL };

    private sealed class RecordingDiagnostics : IDiagnosticManager
    {
        public List<LoadoutId> Refreshed { get; } = [];
        public HashSet<LoadoutId> Failing { get; } = [];
        public IObservable<Diagnostic[]> GetLoadoutDiagnostics(LoadoutId loadoutId) => throw new NotSupportedException();
        public Task RefreshAsync(LoadoutId loadoutId)
        {
            lock (Refreshed) Refreshed.Add(loadoutId);
            if (Failing.Contains(loadoutId)) throw new ObjectDisposedException("the diagnostic manager");
            return Task.CompletedTask;
        }
        public IObservable<(int NumSuggestions, int NumWarnings, int NumCritical)> CountDiagnostics(LoadoutId loadoutId) => throw new NotSupportedException();
        public void Dispose() { }
    }

    private async Task<Diagnostic[]> Diagnose(Loadout.ReadOnly stale)
    {
        var loadout = Loadout.Load(Connection.Db, stale.Id);
        var emitter = ServiceProvider.GetServices<IDiagnosticEmitter>().OfType<WabbajackUpdateEmitter>().Single();
        var result = new List<Diagnostic>();
        await foreach (var diagnostic in emitter.Diagnose(loadout, CancellationToken.None)) result.Add(diagnostic);
        return result.ToArray();
    }

    private static string Text(Diagnostic diagnostic)
    {
        var formatter = new PlainWriter();
        return diagnostic.FormatSummary(formatter) + "\n" + diagnostic.FormatDetails(formatter);
    }

    [Fact]
    public async Task TheEmitterReadsOnlyTheCache()
    {
        var loadout = await CreateLoadout();
        var title = $"Cached {Guid.NewGuid():N}";
        await AddInstalled(loadout, title);
        Gallery.Cached = Listing(Newer(title, "a-list"));
        Gallery.GetFailure = new InvalidOperationException("the emitter must not call Get");

        var diagnostics = await Diagnose(loadout);

        diagnostics.Should().ContainSingle();
        Gallery.GetCalls.Should().Be(0);
    }

    [Fact]
    public async Task TheEmitterYieldsOneSuggestionPerOutdatedGroupWithTheOpenPageAction()
    {
        var loadout = await CreateLoadout();
        var firstTitle = $"First {Guid.NewGuid():N}";
        var secondTitle = $"Second {Guid.NewGuid():N}";
        var first = await AddInstalled(loadout, firstTitle, machineUrl: "first");
        var second = await AddInstalled(loadout, secondTitle, machineUrl: "second");
        await AddInstalled(loadout, $"Current {Guid.NewGuid():N}", machineUrl: "current");
        Gallery.Cached = Listing(Newer(firstTitle, "first"), Newer(secondTitle, "second"), GalleryLibraryFiles.Entry("Current", machineUrl: "current") with { Version = "1.0.0.0" });

        var diagnostics = await Diagnose(loadout);

        diagnostics.Should().HaveCount(2);
        diagnostics.Should().OnlyContain(d => d.Severity == DiagnosticSeverity.Suggestion);
        var offered = diagnostics.Select(d => d.Actions.Should().ContainSingle().Subject).ToArray();
        offered.Should().OnlyContain(a => a.Id == DiagnosticActions.OpenWabbajackModlistId);
        var files = new[] { first, second }.Select(g => g.Modlist.Id.Value.ToString()).ToHashSet();
        offered.Select(a => a.Arguments[0]).ToHashSet().Should().BeEquivalentTo(files);
        offered.Should().OnlyContain(a => a.Arguments[1] == loadout.Id.Value.ToString());
        var text = Text(diagnostics.First(d => Text(d).Contains(firstTitle)));
        text.Should().Contain("1.0.0.0").And.Contain("2.0.0.0");
    }

    [Fact]
    public async Task AReuploadIsSaidToBeOne()
    {
        var loadout = await CreateLoadout();
        var title = $"Reupload {Guid.NewGuid():N}";
        var date = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        await AddInstalled(loadout, title, dateUpdated: date);
        Gallery.Cached = Listing(Newer(title, "a-list") with { DateUpdated = date });

        Text((await Diagnose(loadout)).Should().ContainSingle().Subject).Should().Contain("re-uploaded by the author");
    }

    [Fact]
    public async Task ACachedListingOlderThanTheCacheLifetimeIsSaidToBeAnOfflineCopy()
    {
        var loadout = await CreateLoadout();
        var title = $"Old {Guid.NewGuid():N}";
        await AddInstalled(loadout, title);
        var fetched = DateTimeOffset.UtcNow - WabbajackGalleryClient.CacheLifetime - TimeSpan.FromHours(1);
        Gallery.Cached = new GalleryListing([Newer(title, "a-list")], fetched, IsStale: false);

        Text((await Diagnose(loadout)).Should().ContainSingle().Subject).Should().Contain("offline copy");
    }

    [Fact]
    public async Task AFreshCachedListingIsNotSaidToBeAnOfflineCopy()
    {
        var loadout = await CreateLoadout();
        var title = $"Fresh {Guid.NewGuid():N}";
        await AddInstalled(loadout, title);
        Gallery.Cached = new GalleryListing([Newer(title, "a-list")], DateTimeOffset.UtcNow, IsStale: false);

        Text((await Diagnose(loadout)).Should().ContainSingle().Subject).Should().NotContain("offline copy");
    }

    [Fact]
    public async Task AFailingRefreshOfOneLoadoutDoesNotStopTheOthers()
    {
        var first = await CreateLoadout();
        var second = await CreateLoadout();
        await AddInstalled(first, $"One {Guid.NewGuid():N}");
        await AddInstalled(second, $"Two {Guid.NewGuid():N}");
        var diagnostics = new RecordingDiagnostics();
        diagnostics.Failing.Add(first.LoadoutId);
        diagnostics.Failing.Add(second.LoadoutId);
        using var runner = new GalleryRefreshDiagnosticsRunner(Gallery, Connection, diagnostics, NullLogger<GalleryRefreshDiagnosticsRunner>.Instance);
        await runner.StartAsync(CancellationToken.None);

        Gallery.RefreshedSubject.OnNext(Listing());

        await Eventually(() => diagnostics.Refreshed.Count >= 2);
        diagnostics.Refreshed.Should().Contain([first.LoadoutId, second.LoadoutId]);
        await runner.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task NoRefreshIsRequestedAfterTheRunnerStops()
    {
        var loadout = await CreateLoadout();
        await AddInstalled(loadout, $"Stopped {Guid.NewGuid():N}");
        var diagnostics = new RecordingDiagnostics();
        using var runner = new GalleryRefreshDiagnosticsRunner(Gallery, Connection, diagnostics, NullLogger<GalleryRefreshDiagnosticsRunner>.Instance);
        await runner.StartAsync(CancellationToken.None);
        await runner.StopAsync(CancellationToken.None);

        Gallery.RefreshedSubject.OnNext(Listing());

        await Task.Delay(500);
        diagnostics.Refreshed.Should().BeEmpty();
    }

    [Fact]
    public async Task AGalleryRefreshRequestsADiagnosticsRefreshForEveryLoadoutWithAModlist()
    {
        var withList = await CreateLoadout();
        var other = await CreateLoadout();
        await AddInstalled(withList, $"Listed {Guid.NewGuid():N}");
        var diagnostics = new RecordingDiagnostics();
        using var runner = new GalleryRefreshDiagnosticsRunner(Gallery, Connection, diagnostics, NullLogger<GalleryRefreshDiagnosticsRunner>.Instance);
        await runner.StartAsync(CancellationToken.None);

        Gallery.RefreshedSubject.OnNext(Listing());

        await Eventually(() => diagnostics.Refreshed.Count > 0);
        diagnostics.Refreshed.Should().Contain(withList.LoadoutId).And.NotContain(other.LoadoutId);
        await runner.StopAsync(CancellationToken.None);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
    }

    private sealed class PlainWriter : IDiagnosticWriter
    {
        public void Write<T>(ref DiagnosticWriterState state, T value) where T : notnull => Write(ref state, value.ToString().AsSpan());

        public void WriteValueType<T>(ref DiagnosticWriterState state, T value) where T : struct => Write(ref state, value.ToString().AsSpan());

        public void Write(ref DiagnosticWriterState state, ReadOnlySpan<char> value) => state.StringBuilder.Append(value);
    }
}
