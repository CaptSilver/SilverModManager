using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Ini;
using SilverModManager.DataModel.Undo;
using SilverModManager.Games.CreationEngine;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.ProxyConsole;
using SilverModManager.SingleProcess;
using SilverModManager.StandardGameLocators.TestHelpers;
using NexusMods.Paths;
using SkyrimSEGame = SilverModManager.Games.CreationEngine.SkyrimSE.SkyrimSE;

namespace SilverModManager.CLI.Tests;

/// <summary>
/// Runs verbs against a Skyrim SE loadout, a game that keeps its own copies of the INI files, and seeds the INIs and
/// tweak layers the verbs read.
/// </summary>
public abstract class SkyrimIniVerbTest<TSelf>(ITestOutputHelper helper) : AIsolatedGameTest<TSelf, SkyrimSEGame>(helper)
{
    /// <summary>While false the registry reports no install for any loadout, as it does when the game's library is not mounted.</summary>
    protected bool LibraryMounted = true;

    private sealed class UnmountableRegistry(IGameRegistry inner, Func<bool> mounted) : IGameRegistry
    {
        public ImmutableArray<GameInstallation> LocateGameInstallations() => inner.LocateGameInstallations();
        public ImmutableArray<GameLocatorProblem> Problems => inner.Problems;
        public void ClearCache() => inner.ClearCache();
        public void RefreshInstallation(GameInstallation located) => inner.RefreshInstallation(located);
        public bool TryGetMetadata(GameInstallation installation, out GameInstallMetadata.ReadOnly metadata) => inner.TryGetMetadata(installation, out metadata);

        public bool TryGetGameInstallation(Loadout.ReadOnly loadout, [NotNullWhen(true)] out GameInstallation? gameInstallation)
        {
            gameInstallation = null;
            return mounted() && inner.TryGetGameInstallation(loadout, out gameInstallation);
        }
    }

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        base.AddServices(services);
        var registration = services.Last(d => d.ServiceType == typeof(IGameRegistry));
        services.Remove(registration);
        services.AddSingleton<IGameRegistry>(provider => new UnmountableRegistry(
            (IGameRegistry)(registration.ImplementationFactory?.Invoke(provider)
                ?? registration.ImplementationInstance
                ?? ActivatorUtilities.CreateInstance(provider, registration.ImplementationType!)),
            () => LibraryMounted));
        return services
            .AddCreationEngine()
            .AddUniversalGameLocator<SkyrimSEGame>(new Version("1.6.1170"), locationsFileSystem: IsolatedKnownFolders.Create)
            .AddSingleton<CommandLineConfigurator>()
            .AddDefaultParsers()
            .AddCLI()
            .ReplaceDesktopNotifier();
    }

    protected IFileStore Store => ServiceProvider.GetRequiredService<IFileStore>();

    protected async Task<(int ExitCode, string Output)> Run(params string[] args)
    {
        var renderer = new LoggingRenderer();
        var exitCode = await ServiceProvider.GetRequiredService<CommandLineConfigurator>().RunAsync(args, renderer, CancellationToken.None);
        return (exitCode, string.Concat(renderer.Logs.OfType<Text>().Select(t => t.Template + string.Concat(t.Arguments))));
    }

    protected int LatestRevisionNumber(LoadoutId loadout) =>
        ServiceProvider.GetRequiredService<UndoService>().RevisionsFor(loadout.Value).Count - 1;

    protected GamePath IniPath(string fileName) => ((IGameIniFiles)GameInstallation.Game).IniPath(fileName);

    /// <summary>Gives the loadout a copy of the INI called <paramref name="fileName"/>, as the first Apply would.</summary>
    protected async Task SeedBaseline(Loadout.ReadOnly loadout, string fileName, string text) =>
        await IniEdits.SetBaseline(Connection, Store, loadout, IniPath(fileName), Encoding.UTF8.GetBytes(text));

    /// <summary>Records <paramref name="bytes"/> as what the last Apply wrote to the INI at <paramref name="path"/>.</summary>
    protected async Task SetLastBake(Loadout.ReadOnly loadout, GamePath path, byte[] bytes)
    {
        var state = GameInis.StateOf(loadout.Rebase(), path) ?? throw new InvalidOperationException($"{path} has no baseline to record a bake on");
        using var tx = Connection.BeginTransaction();
        tx.Add(state.Id, LoadoutIniState.LastBakedHash, bytes.xxHash3());
        await tx.Commit();
    }

    /// <summary>
    /// Adds a group holding one tweak file named <paramref name="tweakFile"/>. With <paramref name="parent"/> the group
    /// sits inside it; <paramref name="settingsGroup"/> marks it as a game settings group and <paramref name="collection"/>
    /// makes it a collection.
    /// </summary>
    protected async Task<LoadoutItemGroupId> AddTweakLayer(
        Loadout.ReadOnly loadout, string modName, string tweakFile, string text, ulong priority,
        LoadoutItemGroupId? parent = null, bool settingsGroup = false, bool collection = false)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await Store.BackupFiles([new ArchivedFileEntry(new MemoryStreamFactory(tweakFile, new MemoryStream(bytes)), FileIdentity.XxHash3(bytes.xxHash3()), Size.FromLong(bytes.Length))]);

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
        AddFile(tx, loadout.LoadoutId, group, new GamePath(LocationId.Game, $"INI Tweaks/{tweakFile}"), text);
        _ = new LoadoutItemGroupPriority.New(tx)
        {
            LoadoutId = loadout.LoadoutId,
            TargetId = group,
            Priority = ConflictPriority.From(priority),
        };
        var result = await tx.Commit();
        return LoadoutItemGroupId.From(result[group.Value]);
    }
}
