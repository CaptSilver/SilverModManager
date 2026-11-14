using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SilverModManager.Abstractions.Library;
using SilverModManager.Library.Workshop;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.Games;
using Xunit;

namespace SilverModManager.Library.Tests.Workshop;

internal static class WorkshopSources
{
    public static IGameRegistry RegistryOf(params GameInstallation[] installs)
    {
        var registry = Substitute.For<IGameRegistry>();
        registry.LocateGameInstallations().Returns([.. installs]);
        return registry;
    }

    public static IModSource SourceOf(WorkshopRig rig) => new SteamWorkshopSource(RegistryOf(rig.Install), rig.View, NullLogger<SteamWorkshopSource>.Instance);
}

public class SteamWorkshopSourceTests
{
    [Fact]
    public async Task ItOwnsNothingInTheLibraryAndOffersNoPages()
    {
        using var rig = new WorkshopRig();
        var source = WorkshopSources.SourceOf(rig);

        source.DisplayName.Should().Be("Steam Workshop");
        source.Owns(default(LibraryItem.ReadOnly)).Should().BeFalse();
        source.PageUri(default).Should().BeNull();
        source.ChangelogUri(default).Should().BeNull();
        await source.SetUpdatesHidden([], true, CancellationToken.None);
    }

    [Fact]
    public async Task ItemsUpdatedBySteamAreNotOfferedToUpdateAll()
    {
        // Update All applies what a source reports, and Steam already applies a Workshop update by itself, so a lit
        // button here would be one that does nothing when clicked.
        using var rig = new WorkshopRig();
        rig.Library.Subscribe(1, folderModified: 1000);
        rig.Api.Add(1, "Newer", updated: 2000);
        var source = WorkshopSources.SourceOf(rig);
        var seen = new List<bool>();
        using var subscription = source.ObserveHasAnyUpdates(rig.Game).Subscribe(seen.Add);

        await source.CheckUpdates(rig.Game, force: false, CancellationToken.None);

        rig.Api.Calls.Should().ContainSingle("the check still asks Steam, so the Workshop rows are fresh");
        (await rig.View.For(rig.Install, CancellationToken.None)).Should().ContainSingle().Which.UpdatedBySteam.Should().BeTrue();
        seen.Should().Equal(false);
        source.AllUpdates(rig.Game).Should().BeEmpty();
    }

    [Fact]
    public async Task TheRefreshButtonAsksSteamAgainAndEnteringThePageDoesNot()
    {
        using var rig = new WorkshopRig();
        rig.Library.Subscribe(1, folderModified: 1000);
        rig.Api.Add(1, "Item", updated: 900);
        var source = WorkshopSources.SourceOf(rig);

        await source.CheckUpdates(rig.Game, force: false, CancellationToken.None);
        await source.CheckUpdates(rig.Game, force: false, CancellationToken.None);
        rig.Api.Calls.Should().ContainSingle();

        await source.CheckUpdates(rig.Game, force: true, CancellationToken.None);
        rig.Api.Calls.Should().HaveCount(2);
    }

    [Fact]
    public async Task AGameWithoutWorkshopItemsIsNotAskedAbout()
    {
        using var rig = new WorkshopRig();
        rig.Library.Subscribe(1, 1000);
        var source = WorkshopSources.SourceOf(rig);

        await source.CheckUpdates(new SilverModManager.Library.Tests.GitHub.FakeGame(GameId.From("Tests.Plain")), force: true, CancellationToken.None);

        rig.Api.Calls.Should().BeEmpty();
        source.AllUpdates(rig.Game).Should().BeEmpty();
    }

    [Fact]
    public async Task ApplyingDoesNothingBecauseSteamAlreadyHasIt()
    {
        using var rig = new WorkshopRig();
        var source = WorkshopSources.SourceOf(rig);

        var outcome = await source.ApplyUpdates(new ModUpdateRequest([], Replace: true, MayOpenBrowser: true), CancellationToken.None);

        outcome.Should().Be(ModUpdateOutcome.Nothing);
        source.WillReplace(new ModUpdateRequest([], true, true)).Should().BeFalse();
    }
}
