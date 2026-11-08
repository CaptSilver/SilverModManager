using NexusMods.Paths;
using System.Reactive.Subjects;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Abstractions.Library;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Wabbajack.Gallery;
using SilverModManager.Wabbajack.Models;
using static SilverModManager.Wabbajack.Tests.Gallery.GalleryLibraryFiles;

namespace SilverModManager.Wabbajack.Tests.Gallery;

public class ModlistIdentityBackfillRunnerTests(ILibraryService libraryService, IConnection connection, TemporaryFileManager temporaryFileManager)
{
    private sealed class FakeGallery : IWabbajackGalleryClient
    {
        public Subject<GalleryListing> RefreshedSubject { get; } = new();
        public GalleryListing? Cached { get; set; }
        public IObservable<GalleryListing> Refreshed => RefreshedSubject;
        public GalleryListing? GetCached() => Cached;
        public Task<GalleryListing> Get(bool refresh = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private ModlistIdentityBackfillRunner Runner(FakeGallery gallery)
        => new(gallery, new ModlistIdentityBackfill(connection, NullLogger<ModlistIdentityBackfill>.Instance), NullLogger<ModlistIdentityBackfillRunner>.Instance);

    private Task<WabbajackModlistLibraryFile.ReadOnly> FromGallery(string name)
        => Add(libraryService, temporaryFileManager, connection, name, "Someone", fromGallery: true);

    private bool HasIdentity(WabbajackModlistLibraryFile.ReadOnly file)
        => WabbajackModlistLibraryFile.Load(connection.Db, file.Id).Contains(WabbajackModlistLibraryFile.MachineUrl);

    private static async Task<bool> Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
        return condition();
    }

    [Fact]
    public async Task StartingWithACachedListingWritesTheIdentity()
    {
        var name = $"Cached {Guid.NewGuid():N}";
        var file = await FromGallery(name);
        var gallery = new FakeGallery { Cached = Listing(Entry(name, machineUrl: "cached")) };
        using var runner = Runner(gallery);

        await runner.StartAsync(TestContext.Current.CancellationToken);

        (await Eventually(() => HasIdentity(file))).Should().BeTrue();
        await runner.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ARefreshWritesTheIdentity()
    {
        var name = $"Refreshed {Guid.NewGuid():N}";
        var file = await FromGallery(name);
        var gallery = new FakeGallery();
        using var runner = Runner(gallery);
        await runner.StartAsync(TestContext.Current.CancellationToken);
        HasIdentity(file).Should().BeFalse();

        gallery.RefreshedSubject.OnNext(Listing(Entry(name, machineUrl: "refreshed")));

        (await Eventually(() => HasIdentity(file))).Should().BeTrue();
        await runner.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task NothingIsWrittenAfterStop()
    {
        var name = $"Stopped {Guid.NewGuid():N}";
        var file = await FromGallery(name);
        var gallery = new FakeGallery();
        using var runner = Runner(gallery);
        await runner.StartAsync(TestContext.Current.CancellationToken);
        await runner.StopAsync(TestContext.Current.CancellationToken);

        gallery.RefreshedSubject.OnNext(Listing(Entry(name, machineUrl: "late")));

        await Task.Delay(500, TestContext.Current.CancellationToken);
        HasIdentity(file).Should().BeFalse();
    }

    [Fact]
    public async Task AFailingRunIsLoggedNotThrown()
    {
        var gallery = new FakeGallery();
        using var runner = Runner(gallery);
        await runner.StartAsync(TestContext.Current.CancellationToken);

        var act = () => gallery.RefreshedSubject.OnNext(null!);

        act.Should().NotThrow();
        await runner.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StartingTwiceThrows()
    {
        using var runner = Runner(new FakeGallery());
        await runner.StartAsync(TestContext.Current.CancellationToken);

        var act = () => runner.StartAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
        await runner.StopAsync(TestContext.Current.CancellationToken);
    }
}
