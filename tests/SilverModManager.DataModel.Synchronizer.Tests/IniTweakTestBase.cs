using System.Text;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

/// <summary>A loadout of <see cref="IniStubbedGame"/> with helpers that lay tweak files into groups the way installs do.</summary>
public abstract class IniTweakTestBase<TTest>(ITestOutputHelper helper) : AIsolatedGameTest<TTest, IniStubbedGame>(helper)
{
    protected IniStubbedGame Stub => (IniStubbedGame)GameInstallation.Game;

    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services)
        .AddGame<IniStubbedGame>()
        .AddUniversalGameLocator<IniStubbedGame>(Version.Parse("0.0.0"));

    /// <summary>
    /// Adds a group named <paramref name="modName"/> holding the tweak file <paramref name="tweakFile"/> and a priority
    /// row for the group. <paramref name="parent"/> puts the group inside another one, <paramref name="settingsGroup"/>
    /// marks it as a game settings group and <paramref name="collection"/> makes it a collection (a modlist's group).
    /// A nested group gets its own priority row, as an install gives a list's settings group.
    /// With <paramref name="storeBytes"/> false the row points at bytes the file store does not have.
    /// </summary>
    protected async Task<LoadoutItemGroupId> AddTweakMod(
        Loadout.ReadOnly loadout, string modName, string tweakFile, string content, ulong priority, bool disabled = false,
        LoadoutItemGroupId? parent = null, bool settingsGroup = false, bool collection = false, bool storeBytes = true)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        if (storeBytes)
            await FileStore.BackupFiles([new ArchivedFileEntry(new MemoryStreamFactory(tweakFile, new MemoryStream(bytes)), FileIdentity.XxHash3(bytes.xxHash3()), Size.FromLong(bytes.Length))]);

        using var tx = Connection.BeginTransaction();
        LoadoutItemGroupId group;
        if (collection)
        {
            _ = new CollectionGroup.New(tx, out var id)
            {
                IsReadOnly = false,
                LoadoutItemGroup = new LoadoutItemGroup.New(tx, id)
                {
                    IsGroup = true,
                    LoadoutItem = new LoadoutItem.New(tx, id) { Name = modName, LoadoutId = loadout.LoadoutId },
                },
            };
            group = LoadoutItemGroupId.From(id);
        }
        else
        {
            group = AddEmptyGroup(tx, loadout.LoadoutId, modName, parent);
        }

        if (settingsGroup) tx.Add(group.Value, LoadoutItemGroup.GameSettingsGroup, Null.Instance);
        AddFile(tx, loadout.LoadoutId, group, new GamePath(LocationId.Game, $"INI Tweaks/{tweakFile}"), content);
        _ = new LoadoutItemGroupPriority.New(tx)
        {
            LoadoutId = loadout.LoadoutId,
            TargetId = group,
            Priority = ConflictPriority.From(priority),
        };
        if (disabled) tx.Add(group.Value, LoadoutItem.Disabled, Null.Instance);
        var result = await tx.Commit();
        return LoadoutItemGroupId.From(result[group.Value]);
    }
}
