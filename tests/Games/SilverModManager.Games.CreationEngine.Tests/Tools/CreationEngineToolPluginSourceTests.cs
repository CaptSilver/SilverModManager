using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games.Tools;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.CreationEngine.Abstractions;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;
using Xunit;

namespace SilverModManager.Games.CreationEngine.Tests.Tools;

/// <summary>
/// The Quick Auto Clean picker lists the plugins the loadout puts in the game's Data folder: enabled mods only,
/// plugin file types only, nothing from subfolders. The game's own masters that are installed are listed too: they
/// are the plugins most often cleaned.
/// </summary>
public class CreationEngineToolPluginSourceTests(ITestOutputHelper helper)
    : AIsolatedGameTest<CreationEngineToolPluginSourceTests, CreationEngine.SkyrimSE.SkyrimSE>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services)
        .AddCreationEngine()
        .AddUniversalGameLocator<CreationEngine.SkyrimSE.SkyrimSE>(new Version("1.6.1170"), locationsFileSystem: IsolatedKnownFolders.Create);

    private async Task AddMod(Loadout.ReadOnly loadout, string name, bool disabled, params string[] files)
    {
        using var tx = Connection.BeginTransaction();
        var group = AddEmptyGroup(tx, loadout.LoadoutId, name);
        foreach (var file in files) AddFile(tx, loadout.LoadoutId, group, new GamePath(LocationId.Game, $"Data/{file}"));
        if (disabled) tx.Add(group.Value, LoadoutItem.Disabled, Null.Instance);
        await tx.Commit();
    }

    private IToolPluginSource Source => (IToolPluginSource)Game;

    [Fact]
    public async Task ListsTheEnabledPluginsInDataInNameOrder()
    {
        var loadout = await CreateLoadout();
        await AddMod(loadout, "Mod", disabled: false, "beta.esm", "Alpha.esp", "Light.esl", "readme.txt", "textures/Gamma.esp");

        Source.PluginNames(Refresh(loadout)).Should().Equal("Alpha.esp", "beta.esm", "Light.esl");
    }

    [Fact]
    public async Task LeavesOutThePluginsOfADisabledMod()
    {
        var loadout = await CreateLoadout();
        await AddMod(loadout, "On", disabled: false, "On.esp");
        await AddMod(loadout, "Off", disabled: true, "Off.esp");

        Source.PluginNames(Refresh(loadout)).Should().Equal("On.esp");
    }

    [Fact]
    public async Task AnEmptyLoadoutHasNoPlugins()
    {
        var loadout = await CreateLoadout();

        Source.PluginNames(loadout).Should().BeEmpty();
    }

    private void OnDisk(string name)
    {
        var file = GameInstallation.Locations.ToAbsolutePath(((ICreationEngineGame)Game).DataPath / name);
        Directory.CreateDirectory(file.Parent.GetFullPath());
        File.WriteAllText(file.GetFullPath(), "TES4");
    }

    [Fact]
    public async Task ListsTheGameShippedMastersThatAreInstalled()
    {
        var loadout = await CreateLoadout();
        OnDisk("Dawnguard.esm");
        OnDisk("Update.esm");
        await AddMod(loadout, "Mod", disabled: false, "Alpha.esp");

        Source.PluginNames(Refresh(loadout)).Should().Equal("Alpha.esp", "Dawnguard.esm", "Update.esm");
    }

    [Fact]
    public async Task LeavesOutAGameMasterThatIsNotInstalled()
    {
        var loadout = await CreateLoadout();
        OnDisk("Dawnguard.esm");

        Source.PluginNames(Refresh(loadout)).Should().Equal("Dawnguard.esm");
    }

    [Fact]
    public async Task ListsAGameMasterOnceWhenAModAlsoPutsItInData()
    {
        var loadout = await CreateLoadout();
        OnDisk("Update.esm");
        await AddMod(loadout, "Patch", disabled: false, "update.esm");

        Source.PluginNames(Refresh(loadout)).Should().ContainSingle();
    }
}
