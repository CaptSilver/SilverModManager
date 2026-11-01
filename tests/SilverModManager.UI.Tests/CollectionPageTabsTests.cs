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
    public void CollectionWithInstructionsShowsThemBesideInstalled()
    {
        CollectionPageTabs.For(hasDownloadPage: true, hasInstructions: true)
            .Should().Equal(CollectionPageTab.Installed, CollectionPageTab.Instructions, CollectionPageTab.Downloads);
    }

    [Fact]
    public void CollectionWithoutInstructionsHasNoInstructionsTab()
    {
        CollectionPageTabs.For(hasDownloadPage: true, hasInstructions: false)
            .Should().NotContain(CollectionPageTab.Instructions);
    }

    [Fact]
    public void InstructionsTabDoesNotNeedTheDownloadPage()
    {
        CollectionPageTabs.For(hasDownloadPage: false, hasInstructions: true)
            .Should().Equal(CollectionPageTab.Installed, CollectionPageTab.Instructions);
    }

    [Fact]
    public void WithoutAnInstalledGroupThePageOffersNoInstalledTab()
    {
        CollectionPageTabs.For(hasDownloadPage: true, hasInstalled: false)
            .Should().Equal(CollectionPageTab.Downloads);
    }

    [Fact]
    public void AnInstalledCollectionOpensOnTheInstalledTab()
    {
        var tabs = CollectionPageTabs.For(hasDownloadPage: true, hasInstructions: true);

        CollectionPageTabs.Initial(tabs, isInstalled: true).Should().Be(CollectionPageTab.Installed);
    }

    [Fact]
    public void AnUnfinishedInstallOpensOnDownloadsEvenThoughInstalledIsOffered()
    {
        var tabs = CollectionPageTabs.For(hasDownloadPage: true);

        CollectionPageTabs.Initial(tabs, isInstalled: false).Should().Be(CollectionPageTab.Downloads);
    }

    [Fact]
    public void WithNoDownloadsTabThePageOpensOnTheFirstTab()
    {
        var tabs = CollectionPageTabs.For(hasDownloadPage: false, hasInstructions: true);

        CollectionPageTabs.Initial(tabs, isInstalled: false).Should().Be(CollectionPageTab.Installed);
        CollectionPageTabs.Initial(CollectionPageTabs.For(hasDownloadPage: false, hasInstructions: true, hasInstalled: false), isInstalled: true)
            .Should().Be(CollectionPageTab.Instructions);
    }

    [Fact]
    public void WithNoTabsAtAllThePageFallsBackToInstalled()
    {
        var tabs = CollectionPageTabs.For(hasDownloadPage: false, hasInstalled: false);

        CollectionPageTabs.Initial(tabs, isInstalled: false).Should().Be(CollectionPageTab.Installed);
    }

    [Fact]
    public void NexusModsUriPointsAtTheStoredRevision()
    {
        var uri = CollectionPageTabs.NexusModsUri(GameDomain.From("stardewvalley"), CollectionSlug.From("tckf0m"), RevisionNumber.From(80));

        uri.ToString().Should().Be("https://www.nexusmods.com/games/stardewvalley/collections/tckf0m/revisions/80");
    }
}
