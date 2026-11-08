using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Wabbajack.Gallery;
using static SilverModManager.Wabbajack.Tests.Gallery.GalleryLibraryFiles;

namespace SilverModManager.Wabbajack.Tests.Gallery;

public class ModlistUpdateCheckTests(ITestOutputHelper helper) : UpdateCheckTestBase<ModlistUpdateCheckTests>(helper)
{
    private ModlistUpdateCheck Check => ServiceProvider.GetRequiredService<ModlistUpdateCheck>();

    private static GalleryEntry Newer(string title, string machineUrl = "a-list") => GalleryLibraryFiles.Entry(title, machineUrl: machineUrl) with
    {
        Version = "2.0.0.0",
        ArchiveCount = 12,
        ArchivesSize = 3_000_000_000UL,
    };

    [Fact]
    public async Task AStaleListingStillYieldsOffersMarkedStale()
    {
        var loadout = await CreateLoadout();
        var group = await AddInstalled(loadout, $"Stale {Guid.NewGuid():N}");
        var title = group.AsCollectionGroup().AsLoadoutItemGroup().AsLoadoutItem().Name;
        var fetched = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
        Gallery.Cached = new GalleryListing([Newer(title)], fetched, IsStale: true);

        var offers = await Check.Check(loadout.LoadoutId, CancellationToken.None);

        var offer = offers.Should().ContainSingle().Subject;
        offer.IsStale.Should().BeTrue();
        offer.ListingFetchedAt.Should().Be(fetched);
        offer.Newer.Version.Should().Be("2.0.0.0");
        offer.Installed.Version.Should().Be("1.0.0.0");
        offer.Group.Id.Should().Be(group.Id);
        offer.ArchivesToDownload.Should().Be(12);
        offer.DownloadSize.Value.Should().Be(3_000_000_000UL);
        offer.IsReupload.Should().BeFalse();
    }

    [Fact]
    public async Task NoCacheYieldsNothingAndThrowsNothing()
    {
        var loadout = await CreateLoadout();
        await AddInstalled(loadout, $"NoCache {Guid.NewGuid():N}");
        Gallery.Cached = null;
        Gallery.GetFailure = new GalleryUnavailableException("offline");

        (await Check.Check(loadout.LoadoutId, CancellationToken.None)).Should().BeEmpty();
        (await Check.Check(loadout.LoadoutId, CancellationToken.None)).Should().BeEmpty("a second failed check is as quiet as the first");
        Check.FromCache(Connection.Db, loadout.LoadoutId).Should().BeEmpty();
    }

    [Fact]
    public async Task AnInstalledListWithoutIdentityIsNeverOffered()
    {
        var loadout = await CreateLoadout();
        var group = await AddInstalled(loadout, $"Added from disk {Guid.NewGuid():N}", machineUrl: null);
        var title = group.AsCollectionGroup().AsLoadoutItemGroup().AsLoadoutItem().Name;
        Gallery.Cached = Listing(Newer(title));

        (await Check.Check(loadout.LoadoutId, CancellationToken.None)).Should().BeEmpty();
        Check.FromCache(Connection.Db, loadout.LoadoutId).Should().BeEmpty();
    }

    [Fact]
    public async Task AListThatIsUpToDateOrMissingFromTheListingIsNotOffered()
    {
        var loadout = await CreateLoadout();
        await AddInstalled(loadout, $"Current {Guid.NewGuid():N}", machineUrl: "current");
        await AddInstalled(loadout, $"Unknown {Guid.NewGuid():N}", machineUrl: "unknown");
        Gallery.Cached = Listing(GalleryLibraryFiles.Entry("Current", machineUrl: "current") with { Version = "1.0.0.0" });

        Check.FromCache(Connection.Db, loadout.LoadoutId).Should().BeEmpty();
    }

    [Fact]
    public async Task OnlyTheGroupsOfTheAskedLoadoutAreChecked()
    {
        var first = await CreateLoadout();
        var second = await CreateLoadout();
        await AddInstalled(first, $"First {Guid.NewGuid():N}", machineUrl: "first");
        await AddInstalled(second, $"Second {Guid.NewGuid():N}", machineUrl: "second");
        Gallery.Cached = Listing(Newer("First", "first"), Newer("Second", "second"));

        var offers = Check.FromCache(Connection.Db, first.LoadoutId);

        offers.Should().ContainSingle().Which.Newer.MachineUrl.Should().Be("first");
    }
}
