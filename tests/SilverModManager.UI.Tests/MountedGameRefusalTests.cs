using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.App;
using SilverModManager.App.UI.Overlays.Updater;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Jobs;

namespace SilverModManager.UI.Tests;

public class MountedGameRefusalTests(IServiceProvider provider) : AUiTest(provider)
{
    private static readonly GamePath DataRoot = new(LocationId.Game, "Data");

    internal static GameInstallation Installation(string name)
    {
        var game = Substitute.For<IGameData>();
        game.DisplayName.Returns(name);
        var folder = FileSystem.Shared.FromUnsanitizedFullPath("/games/" + name);
        return new GameInstallation(new GameLocatorResult
        {
            Game = game,
            Path = folder,
            Store = GameStore.Steam,
            StoreIdentifier = "1",
            LocatorIds = ImmutableArray<LocatorId>.Empty,
            Locator = Substitute.For<IGameLocator>(),
            Platform = OSPlatform.Windows,
        }, GameLocations.Create(ImmutableDictionary<LocationId, AbsolutePath>.Empty.Add(LocationId.Game, folder)));
    }

    [Fact]
    public void DescribeIsNullWhenNothingIsHeld()
    {
        MountedGameRefusal.Describe(new HeldMounts(), "Closing").Should().BeNull();
    }

    [Fact]
    public void DescribeNamesTheGameAndTheVerb()
    {
        var held = new HeldMounts();
        held.Hold(Installation("Skyrim"), DataRoot);

        MountedGameRefusal.Describe(held, "Updating").Should().Be("Skyrim is running from a mounted folder. Updating SilverModManager now would crash it. Quit the game first.");
    }

    [Fact]
    public void DescribeNamesAGameOnceWhateverNumberOfRootsItHolds()
    {
        var held = new HeldMounts();
        var skyrim = Installation("Skyrim");
        held.Hold(skyrim, DataRoot);
        held.Hold(skyrim, new GamePath(LocationId.Game, "Plugins"));

        MountedGameRefusal.Describe(held, "Closing").Should().StartWith("Skyrim is running");
    }

    [Fact]
    public void DescribeListsEveryGameThatHoldsAMount()
    {
        var held = new HeldMounts();
        held.Hold(Installation("Skyrim"), DataRoot);
        held.Hold(Installation("Fallout 4"), DataRoot);

        MountedGameRefusal.Describe(held, "Closing").Should().StartWith("Skyrim and Fallout 4 are running from a mounted folder.");
    }

    [Theory]
    [InlineData(WindowCloseReason.WindowClosing, true)]
    [InlineData(WindowCloseReason.Undefined, true)]
    [InlineData(WindowCloseReason.ApplicationShutdown, false)]
    [InlineData(WindowCloseReason.OSShutdown, false)]
    [InlineData(WindowCloseReason.OwnerWindowClosing, false)]
    public void OnlyACloseThePlayerAskedForIsRefused(WindowCloseReason reason, bool refused)
    {
        // A host stop or a logout goes ahead whatever is mounted: refusing would hang the shutdown instead of ending it.
        ShutdownExplainer.CanRefuseClose(reason).Should().Be(refused);
    }

    [Fact]
    public async Task ClosingTheWindowWhileAMountIsHeldKeepsItOpenAndSaysWhy()
    {
        await OnUi(async () =>
        {
            var explainer = new ShutdownExplainer(Substitute.For<IHostApplicationLifetime>(), Substitute.For<IJobMonitor>(), NullLogger<ShutdownExplainer>.Instance);
            var held = new HeldMounts();
            held.Hold(Installation("Skyrim"), DataRoot);
            var shown = new List<string>();
            var window = Track(new Window());
            var closed = false;
            window.Closed += (_, _) => closed = true;
            explainer.Watch(window, held, message => { shown.Add(message); return Task.CompletedTask; });
            ShowRendered(window);

            window.Close();

            closed.Should().BeFalse();
            shown.Should().ContainSingle().Which.Should().Contain("Skyrim").And.Contain("Closing");
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task ClosingTheWindowWithNoMountHeldGoesAhead()
    {
        await OnUi(() =>
        {
            var explainer = new ShutdownExplainer(Substitute.For<IHostApplicationLifetime>(), Substitute.For<IJobMonitor>(), NullLogger<ShutdownExplainer>.Instance);
            var shown = new List<string>();
            var window = Track(new Window());
            var closed = false;
            window.Closed += (_, _) => closed = true;
            explainer.Watch(window, new HeldMounts(), message => { shown.Add(message); return Task.CompletedTask; });
            ShowRendered(window);

            window.Close();

            closed.Should().BeTrue();
            shown.Should().BeEmpty();
        });
    }
}
