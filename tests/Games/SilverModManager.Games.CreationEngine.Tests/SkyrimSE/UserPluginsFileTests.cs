using FluentAssertions;
using GameFinder.Wine;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Games.TestFramework;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.StandardGameLocators.TestHelpers;
using Xunit;

namespace NexusMods.Games.CreationEngine.Tests.SkyrimSE;

/// <summary>
/// A Plugins.txt the user already had (written by a mod manager or by hand) is the one the app replaces
/// on the first apply, so it has to be kept and come back when the game is unmanaged.
/// </summary>
public class UserPluginsFileTests(ITestOutputHelper helper) : AIsolatedGameTest<UserPluginsFileTests, CreationEngine.SkyrimSE.SkyrimSE>(helper)
{
    private static readonly byte[] UserBytes = "# the user's own list\n*MyMod.esp\n"u8.ToArray();

    private AbsolutePath PluginsTxt => GameInstallation.Locations.ToAbsolutePath(Game.PluginsFile);

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddCreationEngine()
            .AddUniversalGameLocator<CreationEngine.SkyrimSE.SkyrimSE>(new Version("1.6.1170"), locationsFileSystem: provider =>
            {
                // The known folders resolve inside a throwaway prefix, never the real home.
                var pfx = provider.GetRequiredService<TemporaryFileManager>().CreateFolder("pfx");
                var prefix = new WinePrefix { ConfigurationDirectory = pfx.Path };
                return prefix.CreateOverlayFileSystem(NexusMods.Paths.FileSystem.Shared);
            });
    }

    private async Task UserHadAPluginsFile()
    {
        PluginsTxt.GetFullPath().Should().Contain("/drive_c/users/", "Plugins.txt resolves inside the prefix");
        PluginsTxt.Parent.CreateDirectory();
        await PluginsTxt.WriteAllBytesAsync(UserBytes);
    }

    [Fact]
    public async Task TheFirstApplyKeepsTheUsersPluginsFileInTheStore()
    {
        await UserHadAPluginsFile();
        var loadout = await CreateLoadout();

        await Synchronizer.Synchronize(Refresh(loadout));

        (await PluginsTxt.ReadAllBytesAsync()).Should().NotEqual(UserBytes);
        (await FileStore.HaveFile(UserBytes.xxHash3())).Should().BeTrue();
    }

    [Fact]
    public async Task UnmanagingRestoresTheUsersPluginsFile()
    {
        await UserHadAPluginsFile();
        var loadout = await CreateLoadout();
        await Synchronizer.Synchronize(Refresh(loadout));

        await LoadoutManager.UnManage(GameInstallation);

        PluginsTxt.FileExists.Should().BeTrue();
        (await PluginsTxt.ReadAllBytesAsync()).Should().Equal(UserBytes);
    }

    [Fact]
    public async Task UnmanagingRemovesAPluginsFileTheUserNeverHad()
    {
        var loadout = await CreateLoadout();
        await Synchronizer.Synchronize(Refresh(loadout));

        await LoadoutManager.UnManage(GameInstallation);

        PluginsTxt.FileExists.Should().BeFalse();
    }
}
