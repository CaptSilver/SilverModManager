using FluentAssertions;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.App.UI.Pages.LoadoutPage;

namespace SilverModManager.UI.Tests;

public class CollectionPageTabsTests
{
    [Fact]
    public void CollectionWithDownloadPageHasInstalledAndDownloadTabs()
    {
        CollectionPageTabs.For(hasDownloadPage: true)
            .Should().Equal(CollectionPageTab.Installed, CollectionPageTab.Downloads);
    }

    [Fact]
    public void CollectionWithoutDownloadPageHasOnlyTheInstalledTab()
    {
        // A local collection has no revision on Nexus, so nothing to download.
        CollectionPageTabs.For(hasDownloadPage: false).Should().Equal(CollectionPageTab.Installed);
    }

    [Fact]
    public void NexusModsUriPointsAtTheStoredRevision()
    {
        var uri = CollectionPageTabs.NexusModsUri(GameDomain.From("stardewvalley"), CollectionSlug.From("tckf0m"), RevisionNumber.From(80));

        uri.ToString().Should().Be("https://www.nexusmods.com/games/stardewvalley/collections/tckf0m/revisions/80");
    }
}
