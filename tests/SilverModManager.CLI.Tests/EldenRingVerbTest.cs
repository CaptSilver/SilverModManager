using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.DataModel.Undo;
using SilverModManager.Games.FromSoft;
using SilverModManager.Games.FromSoft.EldenRing;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.ProxyConsole;
using SilverModManager.SingleProcess;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.CLI.Tests;

/// <summary>
/// Runs verbs against an Elden Ring loadout in an isolated database, and reads the revision list the way
/// <c>loadout revert</c> numbers it.
/// </summary>
public abstract class EldenRingVerbTest<TSelf>(ITestOutputHelper helper) : AIsolatedGameTest<TSelf, EldenRingGame>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        base.AddServices(services);
        services
            .AddFromSoftGames()
            .AddSingleton<IGameLocator>(provider => new UniversalStubbedGameLocator<EldenRingGame>(
                provider,
                provider.GetRequiredService<IFileSystem>(),
                provider.GetRequiredService<TemporaryFileManager>(),
                stores: [GameStore.Steam])
            {
                LocatorIds = [LocatorId.From("3976631895")],
            })
            .AddSingleton<CommandLineConfigurator>()
            .AddDefaultParsers()
            .AddCLI()
            .ReplaceDesktopNotifier();
        return services;
    }

    protected async Task<(int ExitCode, string Output)> Run(params string[] args)
    {
        var renderer = new LoggingRenderer();
        var exitCode = await ServiceProvider.GetRequiredService<CommandLineConfigurator>().RunAsync(args, renderer, CancellationToken.None);
        return (exitCode, string.Concat(renderer.Logs.OfType<Text>().Select(t => t.Template + string.Concat(t.Arguments))));
    }

    /// <summary>The number of the newest revision, as <c>loadout revert -r</c> counts them.</summary>
    protected int LatestRevisionNumber(LoadoutId loadout) =>
        ServiceProvider.GetRequiredService<UndoService>().RevisionsFor(loadout.Value).Count - 1;

    protected async Task<LoadoutItemGroupId> AddMod(LoadoutId loadout, string name, LoadoutItemGroupId? parent = null)
    {
        using var tx = Connection.BeginTransaction();
        await AddModAsync(tx, [(RelativePath)$"mod/{name.Replace(' ', '_')}/file.txt"], loadout, name, parentGroup: parent);
        await tx.Commit();
        var group = LoadoutItem.FindByLoadout(Connection.Db, loadout).OfTypeLoadoutItemGroup()
            .Last(g => g.AsLoadoutItem().Name == name);
        return LoadoutItemGroupId.From(group.Id);
    }

    protected async Task<LoadoutItemGroupId> AddCollection(LoadoutId loadout, string name)
    {
        using var tx = Connection.BeginTransaction();
        _ = new CollectionGroup.New(tx, out var id)
        {
            IsReadOnly = false,
            LoadoutItemGroup = new LoadoutItemGroup.New(tx, id)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, id) { Name = name, LoadoutId = loadout },
            },
        };
        var result = await tx.Commit();
        return LoadoutItemGroupId.From(result[id].Value);
    }

    protected bool IsDisabled(LoadoutItemGroupId group) =>
        LoadoutItem.Load(Connection.Db, LoadoutItemId.From(group.Value)).IsDisabled;
}
