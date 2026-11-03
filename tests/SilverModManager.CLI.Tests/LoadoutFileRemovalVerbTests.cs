using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
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
/// <c>loadout group items delete</c> and <c>loadout files remove</c> take file rows out of a loadout through
/// <see cref="ILoadoutFileRemover"/>, so the terminal and the app agree on what a removal is.
/// </summary>
[Collection("ConsoleOutput")]
public class LoadoutFileRemovalVerbTests(ITestOutputHelper helper) : AIsolatedGameTest<LoadoutFileRemovalVerbTests, EldenRingGame>(helper)
{
    private RecordingRemover _recorder = null!;

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        base.AddServices(services);
        // Wrap the real remover so a test can see what a verb asked of it, while the removal still happens.
        var real = services.Last(d => d.ServiceType == typeof(ILoadoutFileRemover)).ImplementationType
                   ?? throw new InvalidOperationException("The remover is registered by type.");
        services.AddSingleton<ILoadoutFileRemover>(sp =>
            _recorder = new RecordingRemover((ILoadoutFileRemover)ActivatorUtilities.CreateInstance(sp, real)));
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

    private async Task<(int ExitCode, string Output)> Run(params string[] args)
    {
        var renderer = new LoggingRenderer();
        var exitCode = await ServiceProvider.GetRequiredService<CommandLineConfigurator>().RunAsync(args, renderer, CancellationToken.None);
        return (exitCode, string.Concat(renderer.Logs.OfType<Text>().Select(t => t.Template + string.Concat(t.Arguments))));
    }

    private static readonly RelativePath[] ModPaths = [(RelativePath)"mod/a/one.txt", (RelativePath)"mod/a/two.txt"];

    private async Task<(Loadout.ReadOnly Loadout, LoadoutItemGroupId Mod)> LoadoutWithAMod(string name, LoadoutItemGroupId? parent = null)
    {
        var loadout = await CreateLoadout();
        using var tx = Connection.BeginTransaction();
        await AddModAsync(tx, ModPaths, loadout.LoadoutId, name, parentGroup: parent);
        await tx.Commit();
        var mod = LoadoutItem.FindByLoadout(Connection.Db, loadout.LoadoutId).OfTypeLoadoutItemGroup().Single(g => g.AsLoadoutItem().Name == name);
        return (loadout, LoadoutItemGroupId.From(mod.Id));
    }

    private async Task<LoadoutItemGroupId> AddLockedCollection(LoadoutId loadout, string name)
    {
        using var tx = Connection.BeginTransaction();
        _ = new CollectionGroup.New(tx, out var id)
        {
            IsReadOnly = true,
            LoadoutItemGroup = new LoadoutItemGroup.New(tx, id)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, id) { Name = name, LoadoutId = loadout },
            },
        };
        var result = await tx.Commit();
        return LoadoutItemGroupId.From(result[id].Value);
    }

    private int RowsUnder(LoadoutItemGroupId group) =>
        LoadoutItemGroup.Load(Connection.Db, group).Children.OfTypeLoadoutItemWithTargetPath().Count();

    [Fact]
    public async Task GroupItemsDelete_GoesThroughTheFileRemover()
    {
        var (loadout, mod) = await LoadoutWithAMod("Plain Mod");

        var (exit, output) = await Run("loadout", "group", "items", "delete", "-l", loadout.LoadoutId.ToString(), "-g", "Plain Mod", "-f", "mod/a/*.txt");

        exit.Should().Be(0, output);
        var call = _recorder.Calls.Should().ContainSingle().Subject;
        RowsUnder(mod).Should().Be(0);
        call.Request.LoadoutId.Should().Be(loadout.LoadoutId);
        call.Request.OnlyInGroups.Should().Equal(mod);
        call.Request.Paths.Select(p => p.Path.ToString()).Should().BeEquivalentTo(["mod/a/one.txt", "mod/a/two.txt"]);
        call.IncludeLocked.Should().BeTrue("the verb has always deleted from any group");
    }

    [Fact]
    public async Task GroupItemsDelete_StillDeletesFromAWabbajackMod()
    {
        var loadout = await CreateLoadout();
        var list = await AddLockedCollection(loadout.LoadoutId, "A Modlist");
        using (var tx = Connection.BeginTransaction())
        {
            await AddModAsync(tx, ModPaths, loadout.LoadoutId, "Listed Mod", parentGroup: list);
            await tx.Commit();
        }
        var mod = LoadoutItemGroupId.From(LoadoutItem.FindByLoadout(Connection.Db, loadout.LoadoutId).OfTypeLoadoutItemGroup()
            .Single(g => g.AsLoadoutItem().Name == "Listed Mod").Id);
        RowsUnder(mod).Should().Be(2);

        var (exit, output) = await Run("loadout", "group", "items", "delete", "-l", loadout.LoadoutId.ToString(), "-g", "Listed Mod", "-f", "mod/a/one.txt");

        exit.Should().Be(0, output);
        RowsUnder(mod).Should().Be(1);
    }

    [Fact]
    public async Task FilesRemove_ListsLockedRowsAndSkipsThemWithoutTheFlag()
    {
        var loadout = await CreateLoadout();
        var list = await AddLockedCollection(loadout.LoadoutId, "A Modlist");
        using (var tx = Connection.BeginTransaction())
        {
            await AddModAsync(tx, [(RelativePath)"mod/shared/x.txt"], loadout.LoadoutId, "Listed Mod", parentGroup: list);
            await AddModAsync(tx, [(RelativePath)"mod/shared/x.txt"], loadout.LoadoutId, "Mine");
            await tx.Commit();
        }
        var groups = LoadoutItem.FindByLoadout(Connection.Db, loadout.LoadoutId).OfTypeLoadoutItemGroup().ToArray();
        var listed = LoadoutItemGroupId.From(groups.Single(g => g.AsLoadoutItem().Name == "Listed Mod").Id);
        var mine = LoadoutItemGroupId.From(groups.Single(g => g.AsLoadoutItem().Name == "Mine").Id);

        var (exit, output) = await Run("loadout", "files", "remove", "-l", loadout.LoadoutId.ToString(), "-p", "mod/shared/x.txt");

        exit.Should().Be(0, output);
        output.Should().Contain("A Modlist is a Nexus collection; it decides what it contains");
        RowsUnder(mine).Should().Be(0);
        RowsUnder(listed).Should().Be(1, "the locked row is skipped without the flag");

        var (exit2, output2) = await Run("loadout", "files", "remove", "-l", loadout.LoadoutId.ToString(), "-p", "mod/shared/x.txt", "--include-locked");
        exit2.Should().Be(0, output2);
        RowsUnder(listed).Should().Be(0);
    }

    private sealed class RecordingRemover(ILoadoutFileRemover inner) : ILoadoutFileRemover
    {
        public List<(FileRemovalRequest Request, bool IncludeLocked)> Calls { get; } = [];

        public FileRemovalPlan Plan(FileRemovalRequest request) => inner.Plan(request);

        public Task<FileRemovalResult> Remove(FileRemovalRequest request, bool includeLocked)
        {
            Calls.Add((request, includeLocked));
            return inner.Remove(request, includeLocked);
        }
    }
}
