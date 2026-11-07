using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Mutagen.Bethesda;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.StandardGameLocators.TestHelpers;
using Xunit;
using VR = SilverModManager.Games.CreationEngine.SkyrimVR.SkyrimVR;

namespace SilverModManager.Games.CreationEngine.Tests;

/// <summary>
/// Skyrim VR's six masters are in the loadout as game files whose recorded bytes nothing holds any more (the game
/// store patched them). Rendering the plugins list for the Apply button must read them from the game folder and
/// land on the bytes the Apply wrote, or the button stays lit for good.
/// </summary>
public class PluginsFileUnstoredMastersStatusTests(ITestOutputHelper helper) : AIsolatedGameTest<PluginsFileUnstoredMastersStatusTests, VR>(helper)
{
    private static readonly string[] Masters = ["Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm", "SkyrimVR.esm"];

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        var configured = base.AddServices(services)
            .AddCreationEngine()
            .AddUniversalGameLocator<VR>(new Version("1.4.15"), locationsFileSystem: IsolatedKnownFolders.Create);
        configured.Replace(ServiceDescriptor.Singleton<IStreamSourceDispatcher, NoStoredBytes>());
        return configured;
    }

    /// <summary>Holds no bytes for any identity, like a store that never kept the game's own files.</summary>
    private sealed class NoStoredBytes : IStreamSourceDispatcher
    {
        public ValueTask<Stream?> OpenAsync(FileIdentity identity, CancellationToken cancellationToken = default) => ValueTask.FromResult<Stream?>(null);

        public bool Exists(FileIdentity identity) => false;
    }

    private async Task WriteMasters()
    {
        foreach (var name in Masters)
        {
            var file = GameInstallation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, "Data/" + name));
            file.Parent.CreateDirectory();
            await file.WriteAllBytesAsync(BethesdaPluginBytes.Bytes(GameRelease.SkyrimVR, name));
        }
    }

    [Fact]
    public async Task MastersReadableOnlyFromTheGameFolder_LeaveNothingToApplyAfterAnApply()
    {
        await WriteMasters();
        var loadout = await CreateLoadout();
        await Synchronizer.Synchronize(Refresh(loadout));

        var fresh = Refresh(loadout);
        var metadata = fresh.Installation;
        var tree = Synchronizer.BuildSyncTree(Synchronizer.GetLastScannedDiskState(metadata).ToList(), Synchronizer.GetDiskStateForGame(metadata).ToList(), fresh);
        foreach (var name in Masters)
        {
            tree.Should().ContainKey(new GamePath(LocationId.Game, "Data/" + name));
            var node = tree[new GamePath(LocationId.Game, "Data/" + name)];
            (node.HaveLoadout && node.HaveDisk).Should().BeTrue($"{name} must be the loadout-and-disk case the fallback serves");
        }

        var evaluation = await Synchronizer.EvaluateSynchronize(fresh, Synchronizer.GetLastScannedDiskState(metadata), Synchronizer.GetDiskStateForGame(metadata));

        evaluation.Needed.Should().BeFalse("rendering reads the masters from the game folder, as the Apply did");
    }
}
