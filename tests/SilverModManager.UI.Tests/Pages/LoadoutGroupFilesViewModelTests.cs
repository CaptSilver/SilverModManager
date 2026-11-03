using Avalonia.Threading;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Loadouts;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Pages.LoadoutGroupFilesPage;
using SilverModManager.App.UI.Windows;
using SilverModManager.Sdk.Games;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>
/// The open-in-editor and remove commands depend only on the page's own state, so they exist from construction
/// and stay quiet while the page has no context or selection.
/// </summary>
public class LoadoutGroupFilesViewModelTests(IServiceProvider provider) : AUiTest(provider)
{
    private static readonly GamePath SomeFile = new(LocationId.Game, "meshes/rock/rock.nif");

    private LoadoutGroupFilesViewModel CreateViewModel() => new(
        NullLogger<LoadoutGroupFilesViewModel>.Instance,
        Substitute.For<IWindowManager>(),
        new ServiceCollection().BuildServiceProvider(),
        Connection);

    [Fact]
    public async Task Commands_ExistBeforeThePageIsActivated()
    {
        var viewModel = await OnUi(() => Task.FromResult(CreateViewModel()));

        viewModel.OpenEditorCommand.Should().NotBeNull();
        viewModel.RemoveCommand.Should().NotBeNull();
    }

    [Fact]
    public async Task OpenEditorCommand_WithASelectionButNoContext_CannotRun()
    {
        var viewModel = await OnUi(() => Task.FromResult(CreateViewModel()));

        await OnUi(() =>
        {
            using var activation = viewModel.Activator.Activate();
            viewModel.SelectedItem = new CompositeItemModel<GamePath>(SomeFile);

            viewModel.OpenEditorCommand.CanExecute().Should().BeFalse();
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task RemoveCommand_WithASelectionButNoContext_CannotRun()
    {
        var viewModel = await OnUi(() => Task.FromResult(CreateViewModel()));

        await OnUi(() =>
        {
            using var activation = viewModel.Activator.Activate();
            viewModel.SelectedItem = new CompositeItemModel<GamePath>(SomeFile);

            viewModel.RemoveCommand.CanExecute().Should().BeFalse();
            return Task.CompletedTask;
        });
    }

    private LoadoutGroupFilesViewModel CreateViewModel(IServiceProvider services) => new(
        NullLogger<LoadoutGroupFilesViewModel>.Instance,
        Substitute.For<IWindowManager>(),
        services,
        Connection);

    private async Task<LoadoutItemGroupId> AddGroup(LoadoutId loadout, string name, LoadoutItemGroupId? parent = null, bool collection = false, params GamePath[] files)
    {
        using var tx = Connection.BeginTransaction();
        EntityId id;
        if (collection)
        {
            _ = new CollectionGroup.New(tx, out id)
            {
                IsReadOnly = true,
                LoadoutItemGroup = new LoadoutItemGroup.New(tx, id)
                {
                    IsGroup = true,
                    LoadoutItem = new LoadoutItem.New(tx, id) { Name = name, LoadoutId = loadout },
                },
            };
        }
        else
        {
            _ = new LoadoutItemGroup.New(tx, out id)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, id)
                {
                    Name = name,
                    LoadoutId = loadout,
                    ParentId = parent is { } p ? p.Value : default,
                },
            };
        }

        foreach (var path in files)
        {
            var hash = path.Path.ToString().xxHash3AsUtf8();
            _ = new LoadoutFile.New(tx, out var fileId)
            {
                LoadoutItemWithTargetPath = new LoadoutItemWithTargetPath.New(tx, fileId)
                {
                    LoadoutItem = new LoadoutItem.New(tx, fileId) { LoadoutId = loadout, ParentId = id, Name = path.Path },
                    TargetPath = path.ToGamePathParentTuple(loadout),
                },
                Hash = hash,
                Size = Size.FromLong(1),
            };
        }

        var result = await tx.Commit();
        return LoadoutItemGroupId.From(result[id].Value);
    }

    private int RowsAt(Loadout.ReadOnly loadout, GamePath path) =>
        LoadoutItemWithTargetPath.FindByTargetPath(Connection.Db, path.ToGamePathParentTuple(loadout.Id)).Count;

    private async Task PressRemove(LoadoutGroupFilesViewModel viewModel, LoadoutItemGroupId[] groups, GamePath path, bool readOnly = false)
    {
        await OnUi(() =>
        {
            viewModel.Context = new LoadoutGroupFilesPageContext { GroupIds = groups, IsReadOnly = readOnly };
            viewModel.SelectedItem = new CompositeItemModel<GamePath>(path);
            viewModel.RemoveCommand.Execute(R3.Unit.Default);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task RemoveCommand_GoesThroughTheFileRemoverScopedToThePagesGroups()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Files Page Scope");
        var group = await AddGroup(loadout.LoadoutId, "Mod", files: [SomeFile]);
        var remover = Substitute.For<ILoadoutFileRemover>();
        remover.Remove(Arg.Any<FileRemovalRequest>(), Arg.Any<bool>()).Returns(new FileRemovalResult([SomeFile], default));
        var viewModel = await OnUi(() => Task.FromResult(CreateViewModel(new ServiceCollection().AddSingleton(remover).BuildServiceProvider())));

        await PressRemove(viewModel, [group], SomeFile);

        await Eventually(() => remover.Received(1).Remove(
            Arg.Is<FileRemovalRequest>(r => r.OnlyInGroups != null && r.OnlyInGroups.SequenceEqual(new[] { group })
                                            && r.Paths.SequenceEqual(new[] { SomeFile }) && r.LoadoutId == loadout.LoadoutId),
            true));
    }

    [Fact]
    public async Task RemoveCommand_OnAWabbajackModsFilesPage_StillRemovesTheRow()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Files Page Wabbajack");
        var list = await AddGroup(loadout.LoadoutId, "A Modlist", collection: true);
        var mod = await AddGroup(loadout.LoadoutId, "Listed Mod", parent: list, files: [SomeFile]);
        var viewModel = await OnUi(() => Task.FromResult(CreateViewModel(Provider)));

        // LoadoutViewModel hands a Wabbajack mod's page a writable context: only required Nexus items and game-installed content lock it.
        await PressRemove(viewModel, [mod], SomeFile, readOnly: false);

        await Eventually(() => RowsAt(loadout, SomeFile).Should().Be(0));
    }

    [Fact]
    public async Task RemoveCommand_OnExternalChanges_UnrecordsASelectedDelete()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Files Page Unrecord");
        var synchronizer = (ALoadoutSynchronizer)loadout.InstallationInstance.GetGame().Synchronizer;
        using (var create = Connection.BeginTransaction())
        {
            synchronizer.GetOrCreateOverridesGroup(create, loadout);
            await create.Commit();
        }
        var overrides = LoadoutItemGroupId.From(LoadoutOverridesGroup.FindByOverridesFor(Connection.Db, loadout.Id).Single().Id);
        using (var tx = Connection.BeginTransaction())
        {
            OverridesGroupItems.AddReifiedDelete(tx, Loadout.Load(Connection.Db, loadout.Id), LoadoutOverridesGroupId.From(overrides.Value), SomeFile);
            await tx.Commit();
        }
        synchronizer.DeletedWinners(Loadout.Load(Connection.Db, loadout.Id)).Should().Contain(SomeFile);
        var viewModel = await OnUi(() => Task.FromResult(CreateViewModel(Provider)));

        await PressRemove(viewModel, [overrides], SomeFile);

        await Eventually(() => RowsAt(loadout, SomeFile).Should().Be(0));
        synchronizer.DeletedWinners(Loadout.Load(Connection.Db, loadout.Id)).Should().NotContain(SomeFile);
    }
}
