using Avalonia.Threading;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Dialog;
using SilverModManager.App.UI.Dialog.Enums;
using SilverModManager.App.UI.Pages.LoadoutGroupFilesPage;
using SilverModManager.App.UI.Windows;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Dialog;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>
/// The External Changes page offers to move the selected files into a new or an existing mod. The mover itself
/// is covered where it lives; these tests pin when the commands exist, what the dialogs ask and show, and what
/// the page hands to the mover.
/// </summary>
public class MoveExternalChangesTests(IServiceProvider provider) : AUiTest(provider)
{
    private static readonly GamePath ModelFile = new(LocationId.Game, "meshes/rock/rock.nif");
    private static readonly GamePath SecondModelFile = new(LocationId.Game, "meshes/rock/rock2.nif");
    private static readonly GamePath LooseFile = new(LocationId.Game, "tweak.ini");

    private readonly IOverridesMover _mover = Substitute.For<IOverridesMover>();
    private readonly IErrorPresenter _errors = Substitute.For<IErrorPresenter>();
    private readonly ILoadoutManager _loadouts = Substitute.For<ILoadoutManager>();
    private readonly IWindowManager _windows = Substitute.For<IWindowManager>();
    private readonly List<DialogViewModel> _dialogs = [];
    private Func<DialogViewModel, StandardDialogResult> _answer = _ => new StandardDialogResult { ButtonId = ButtonDefinitionId.Cancel };

    private sealed record Seeded(
        LoadoutId Loadout,
        LoadoutItemGroupId Overrides,
        LoadoutItemGroupId PlainMod,
        LoadoutItemGroupId CollectionMember,
        LoadoutItemGroupId Collection,
        LoadoutItemGroupId GameInstalled);

    private async Task<Seeded> SeedLoadout(bool withMods = true)
    {
        var loadout = await SeedLoadoutRow(GameStore.Unknown, "move-external-" + Guid.NewGuid().ToString("N"));
        using var tx = Connection.BeginTransaction();

        _ = new LoadoutOverridesGroup.New(tx, out var overrides)
        {
            OverridesForId = loadout,
            LoadoutItemGroup = new LoadoutItemGroup.New(tx, overrides)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, overrides) { Name = "Overrides", LoadoutId = loadout.Id },
            },
        };
        foreach (var path in new[] { ModelFile, SecondModelFile, LooseFile })
            AddFile(tx, loadout.Id, overrides, path);

        if (!withMods)
        {
            var bare = await tx.Commit();
            return new Seeded(loadout.LoadoutId, LoadoutItemGroupId.From(bare[overrides]), default, default, default, default);
        }

        _ = new LoadoutItemGroup.New(tx, out var plain)
        {
            IsGroup = true,
            LoadoutItem = new LoadoutItem.New(tx, plain) { Name = "Plain mod", LoadoutId = loadout.Id },
        };

        _ = new CollectionGroup.New(tx, out var collection)
        {
            IsReadOnly = false,
            LoadoutItemGroup = new LoadoutItemGroup.New(tx, collection)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, collection) { Name = "A collection", LoadoutId = loadout.Id },
            },
        };
        _ = new LoadoutItemGroup.New(tx, out var member)
        {
            IsGroup = true,
            LoadoutItem = new LoadoutItem.New(tx, member) { Name = "Collection member", LoadoutId = loadout.Id, ParentId = LoadoutItemGroupId.From(collection) },
        };

        _ = new GameInstalledItem.New(tx, out var installed)
        {
            Source = "SkyrimCreation",
            SourceId = "item",
            Confidence = "GameRecord",
            CanBeClaimed = false,
            DepotShipped = false,
            LoadoutItemGroup = new LoadoutItemGroup.New(tx, installed)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, installed) { Name = "Installed by the game", LoadoutId = loadout.Id },
            },
        };

        var result = await tx.Commit();
        LoadoutItemGroupId Id(EntityId id) => LoadoutItemGroupId.From(result[id]);
        return new Seeded(loadout.LoadoutId, Id(overrides), Id(plain), Id(member), Id(collection), Id(installed));
    }

    private static void AddFile(ITransaction tx, LoadoutId loadout, EntityId parent, GamePath path)
    {
        _ = new LoadoutFile.New(tx, out var fileId)
        {
            LoadoutItemWithTargetPath = new LoadoutItemWithTargetPath.New(tx, fileId)
            {
                LoadoutItem = new LoadoutItem.New(tx, fileId)
                {
                    LoadoutId = loadout,
                    ParentId = LoadoutItemGroupId.From(parent),
                    Name = path.Path.FileName,
                },
                TargetPath = path.ToGamePathParentTuple(loadout.Value),
            },
            Hash = Hash.From(0xC0FFEE),
            Size = Size.From(10),
        };
    }

    private async Task<LoadoutGroupFilesViewModel> PageOver(LoadoutItemGroupId group)
    {
        _windows.ShowDialog(Arg.Any<IDialog>(), Arg.Any<DialogWindowType>()).Returns(call =>
        {
            var viewModel = ((Dialog)call.Arg<IDialog>()).ViewModel;
            _dialogs.Add(viewModel);
            return _answer(viewModel);
        });

        var services = new ServiceCollection()
            .AddSingleton(Connection)
            .AddSingleton(_mover)
            .AddSingleton(_errors)
            .AddSingleton(_loadouts)
            .AddSingleton(_windows)
            .AddSingleton(Substitute.For<IWindowNotificationService>())
            .BuildServiceProvider();
        return await OnUi(() => Task.FromResult(new LoadoutGroupFilesViewModel(
            NullLogger<LoadoutGroupFilesViewModel>.Instance, _windows, services, Connection)
        {
            Context = new LoadoutGroupFilesPageContext { GroupIds = [group], IsReadOnly = false },
        }));
    }

    private static async Task Select(LoadoutGroupFilesViewModel page, params GamePath[] paths) =>
        await Dispatcher.UIThread.InvokeAsync(() => page.SelectedItems = paths.Select(p => new CompositeItemModel<GamePath>(p)).ToArray());

    private static MoveToModDialogViewModel Content(DialogViewModel dialog) => (MoveToModDialogViewModel)dialog.ContentViewModel!;

    private static IDialogStandardContentViewModel NameContent(DialogViewModel dialog) => (IDialogStandardContentViewModel)dialog.ContentViewModel!;

    private static StandardDialogResult Accept(string input = "") => new() { ButtonId = ButtonDefinitionId.Accept, InputText = input };

    private static OverridesMovePreview NoConflict(GamePath path, LoadoutItemGroupId target) =>
        new(path, true, false, Hash.From(0xC0FFEE), false, false, null, target, true);

    #region when the commands exist

    [Fact]
    public async Task TheCommandsAreOfferedOnExternalChangesOnly()
    {
        var seeded = await SeedLoadout();

        var external = await PageOver(seeded.Overrides);
        var ordinary = await PageOver(seeded.PlainMod);

        external.IsExternalChanges.Should().BeTrue();
        ordinary.IsExternalChanges.Should().BeFalse("a mod's own files page has nothing to move out of External Changes");
    }

    [Fact]
    public async Task AModsFilesPageCannotMove()
    {
        var seeded = await SeedLoadout();
        var page = await PageOver(seeded.PlainMod);
        await Select(page, ModelFile);

        page.MoveToNewModCommand.CanExecute().Should().BeFalse();
        page.MoveToExistingModCommand.CanExecute().Should().BeFalse();
    }

    [Fact]
    public async Task ExternalChangesCannotMoveWithoutASelection()
    {
        var seeded = await SeedLoadout();
        var page = await PageOver(seeded.Overrides);

        page.MoveToNewModCommand.CanExecute().Should().BeFalse();
        page.MoveToExistingModCommand.CanExecute().Should().BeFalse();

        await Select(page, ModelFile, LooseFile);
        page.MoveToNewModCommand.CanExecute().Should().BeTrue();
        page.MoveToExistingModCommand.CanExecute().Should().BeTrue();

        await Select(page);
        page.MoveToNewModCommand.CanExecute().Should().BeFalse("clearing the selection takes the files away");
    }

    #endregion

    #region move to a new mod

    [Fact]
    public async Task TheNewModIsProposedAsTheFirstSelectedFilesTopFolder()
    {
        var seeded = await SeedLoadout();
        var page = await PageOver(seeded.Overrides);
        await Select(page, ModelFile, LooseFile);
        string? proposed = null;
        _answer = dialog =>
        {
            proposed = NameContent(dialog).InputText;
            return new StandardDialogResult { ButtonId = ButtonDefinitionId.Cancel };
        };

        await OnUi(() => page.MoveToNewMod());

        proposed.Should().Be("meshes");
    }

    [Fact]
    public async Task ALooseFileInTheGameRootProposesItsOwnName()
    {
        var seeded = await SeedLoadout();
        var page = await PageOver(seeded.Overrides);
        await Select(page, LooseFile);
        string? proposed = null;
        _answer = dialog =>
        {
            proposed = NameContent(dialog).InputText;
            return new StandardDialogResult { ButtonId = ButtonDefinitionId.Cancel };
        };

        await OnUi(() => page.MoveToNewMod());

        proposed.Should().Be("tweak");
    }

    [Fact]
    public async Task CancellingTheNameDialogChangesNothing()
    {
        var seeded = await SeedLoadout();
        var page = await PageOver(seeded.Overrides);
        await Select(page, ModelFile);

        await OnUi(() => page.MoveToNewMod());

        await _mover.DidNotReceiveWithAnyArgs().CreateEmptyGroup(default, default!);
        await _mover.DidNotReceiveWithAnyArgs().Move(default, default!, default, default!);
    }

    [Fact]
    public async Task ANewModIsCreatedWithTheTypedNameAndTheFilesAreMovedIntoIt()
    {
        var seeded = await SeedLoadout();
        var page = await PageOver(seeded.Overrides);
        await Select(page, ModelFile, SecondModelFile);
        var created = seeded.PlainMod;
        _mover.CreateEmptyGroup(seeded.Loadout, "Rock replacer").Returns(created);
        _mover.Preview(seeded.Loadout, Arg.Any<GamePath[]>(), created)
            .Returns(call => call.Arg<GamePath[]>().Select(p => NoConflict(p, created)).ToArray());
        _mover.Move(seeded.Loadout, Arg.Any<GamePath[]>(), created, Arg.Any<MoveOptions>())
            .Returns(new OverridesMoveResult([ModelFile, SecondModelFile], []));
        _answer = _ => Accept("Rock replacer");

        await OnUi(() => page.MoveToNewMod());

        await _mover.Received(1).CreateEmptyGroup(seeded.Loadout, "Rock replacer");
        await _mover.Received(1).Move(
            seeded.Loadout,
            Arg.Is<GamePath[]>(paths => paths.Length == 2 && paths.Contains(ModelFile) && paths.Contains(SecondModelFile)),
            created,
            Arg.Is<MoveOptions>(o => !o.PinMovedPaths));
        _dialogs.Should().HaveCount(1, "a fresh mod at the top of the order has nothing to warn about");
        await _loadouts.DidNotReceiveWithAnyArgs().RemoveItems(default!);
    }

    [Fact]
    public async Task ABlankNameIsNotAccepted()
    {
        var seeded = await SeedLoadout();
        var page = await PageOver(seeded.Overrides);
        await Select(page, ModelFile);
        _answer = _ => Accept("   ");

        await OnUi(() => page.MoveToNewMod());

        await _mover.DidNotReceiveWithAnyArgs().CreateEmptyGroup(default, default!);
        await _errors.Received(1).Report(Arg.Any<OverridesMoveRefusedException>(), Arg.Any<string>());
    }

    [Fact]
    public async Task AFolderInTheSelectionMovesTheFilesBelowIt()
    {
        var seeded = await SeedLoadout();
        var page = await PageOver(seeded.Overrides);
        await Select(page, new GamePath(LocationId.Game, "meshes"), LooseFile);
        var created = seeded.PlainMod;
        _mover.CreateEmptyGroup(seeded.Loadout, "Stuff").Returns(created);
        _mover.Preview(seeded.Loadout, Arg.Any<GamePath[]>(), created)
            .Returns(call => call.Arg<GamePath[]>().Select(p => NoConflict(p, created)).ToArray());
        _mover.Move(seeded.Loadout, Arg.Any<GamePath[]>(), created, Arg.Any<MoveOptions>())
            .Returns(new OverridesMoveResult([], []));
        _answer = _ => Accept("Stuff");

        await OnUi(() => page.MoveToNewMod());

        await _mover.Received(1).Move(
            seeded.Loadout,
            Arg.Is<GamePath[]>(paths => paths.Length == 3
                && paths.Contains(ModelFile) && paths.Contains(SecondModelFile) && paths.Contains(LooseFile)),
            created,
            Arg.Any<MoveOptions>());
    }

    [Fact]
    public async Task DecliningTheNewModConfirmationTakesTheEmptyModAwayAgain()
    {
        var seeded = await SeedLoadout();
        var page = await PageOver(seeded.Overrides);
        await Select(page, ModelFile);
        var created = seeded.PlainMod;
        _mover.CreateEmptyGroup(seeded.Loadout, "Rock").Returns(created);
        _mover.Preview(seeded.Loadout, Arg.Any<GamePath[]>(), created).Returns(
        [
            new OverridesMovePreview(ModelFile, true, false, Hash.From(0xC0FFEE), false, false, null, seeded.CollectionMember, false),
        ]);
        _answer = dialog => dialog.ContentViewModel is MoveToModDialogViewModel
            ? new StandardDialogResult { ButtonId = ButtonDefinitionId.Cancel }
            : Accept("Rock");

        await OnUi(() => page.MoveToNewMod());

        _dialogs.Should().HaveCount(2, "a file pinned to another mod makes even a new mod lose, so the move asks first");
        await _mover.DidNotReceiveWithAnyArgs().Move(default, default!, default, default!);
        await _loadouts.Received(1).RemoveItems(Arg.Is<LoadoutItemGroupId[]>(ids => ids.SequenceEqual(new[] { created })));
    }

    [Fact]
    public async Task ARefusedMoveIntoANewModTakesTheEmptyModAwayAgain()
    {
        var seeded = await SeedLoadout();
        var page = await PageOver(seeded.Overrides);
        await Select(page, ModelFile);
        var created = seeded.PlainMod;
        var refusal = new OverridesMoveRefusedException("The game does not let `meshes/rock/rock.nif` be moved.");
        _mover.CreateEmptyGroup(seeded.Loadout, "Rock").Returns(created);
        _mover.Preview(seeded.Loadout, Arg.Any<GamePath[]>(), created).Returns([NoConflict(ModelFile, created)]);
        _mover.Move(seeded.Loadout, Arg.Any<GamePath[]>(), created, Arg.Any<MoveOptions>())
            .Returns<Task<OverridesMoveResult>>(_ => throw refusal);
        _answer = _ => Accept("Rock");

        await OnUi(() => page.MoveToNewMod());

        await _loadouts.Received(1).RemoveItems(Arg.Is<LoadoutItemGroupId[]>(ids => ids.SequenceEqual(new[] { created })));
        await _errors.Received(1).Report(refusal, Arg.Any<string>());
    }

    #endregion

    #region move to an existing mod

    [Fact]
    public async Task ALoadoutWithoutModsIsToldSoInsteadOfShownAnEmptyPicker()
    {
        var seeded = await SeedLoadout(withMods: false);
        var page = await PageOver(seeded.Overrides);
        await Select(page, ModelFile);

        await OnUi(() => page.MoveToExistingMod());

        _dialogs.Should().BeEmpty();
        _mover.ReceivedCalls().Should().BeEmpty();
        await _errors.Received(1).Report(Arg.Any<OverridesMoveRefusedException>(), Arg.Any<string>());
    }

    [Fact]
    public async Task ThePickerListsTheLoadoutsModsOnly()
    {
        var seeded = await SeedLoadout();
        var page = await PageOver(seeded.Overrides);
        await Select(page, ModelFile);
        LoadoutItemGroupId[]? offered = null;
        _answer = dialog =>
        {
            offered = Content(dialog).Mods.Select(m => m.Id).ToArray();
            return new StandardDialogResult { ButtonId = ButtonDefinitionId.Cancel };
        };

        await OnUi(() => page.MoveToExistingMod());

        offered.Should().BeEquivalentTo([seeded.PlainMod, seeded.CollectionMember],
            "Overrides, collections and the game's own content are not destinations");
    }

    [Fact]
    public async Task CancellingThePickerChangesNothing()
    {
        var seeded = await SeedLoadout();
        var page = await PageOver(seeded.Overrides);
        await Select(page, ModelFile);

        await OnUi(() => page.MoveToExistingMod());

        _mover.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task TheConfirmationListsDuplicatesAndWhatTheModLosesTo()
    {
        var seeded = await SeedLoadout();
        var page = await PageOver(seeded.Overrides);
        await Select(page, ModelFile, SecondModelFile);
        var otherWinner = seeded.CollectionMember;
        _mover.Preview(seeded.Loadout, Arg.Any<GamePath[]>(), seeded.PlainMod).Returns(
        [
            new OverridesMovePreview(ModelFile, true, false, Hash.From(0xC0FFEE), true, false, Hash.From(0xBEEF), otherWinner, false),
            NoConflict(SecondModelFile, seeded.PlainMod),
        ]);
        _mover.Move(seeded.Loadout, Arg.Any<GamePath[]>(), seeded.PlainMod, Arg.Any<MoveOptions>())
            .Returns(new OverridesMoveResult([ModelFile, SecondModelFile], []));
        string? details = null;
        _answer = dialog =>
        {
            if (Content(dialog).Mods.Count > 0)
            {
                var picker = Content(dialog);
                picker.SelectedMod = picker.Mods.Single(m => m.Id == seeded.PlainMod);
                return Accept();
            }

            details = Content(dialog).Details;
            return new StandardDialogResult { ButtonId = ButtonDefinitionId.Cancel };
        };

        await OnUi(() => page.MoveToExistingMod());

        details.Should().NotBeNull();
        details.Should().Contain(ModelFile.Path.ToString(), "it is both a duplicate and a loser");
        details.Should().Contain(Hash.From(0xC0FFEE).ToHex(), "the file being moved");
        details.Should().Contain(Hash.From(0xBEEF).ToHex(), "the file it would replace");
        details.Should().Contain("Plain mod").And.Contain("Collection member", "A will lose to B names both mods");
        details.Should().NotContain(SecondModelFile.Path.ToString(), "that file has no conflict to report");
    }

    [Fact]
    public async Task TheKeepWinningCheckboxDecidesWhetherThePathsArePinned()
    {
        foreach (var keepWinning in new[] { true, false })
        {
            _mover.ClearReceivedCalls();
            _dialogs.Clear();
            var seeded = await SeedLoadout();
            var page = await PageOver(seeded.Overrides);
            await Select(page, ModelFile);
            _mover.Preview(seeded.Loadout, Arg.Any<GamePath[]>(), seeded.PlainMod).Returns(
            [
                new OverridesMovePreview(ModelFile, true, false, Hash.From(0xC0FFEE), false, false, null, seeded.CollectionMember, false),
            ]);
            _mover.Move(seeded.Loadout, Arg.Any<GamePath[]>(), seeded.PlainMod, Arg.Any<MoveOptions>())
                .Returns(new OverridesMoveResult([ModelFile], []));
            _answer = dialog =>
            {
                var content = Content(dialog);
                if (content.Mods.Count > 0)
                {
                    content.SelectedMod = content.Mods.Single(m => m.Id == seeded.PlainMod);
                    return Accept();
                }

                content.CanKeepWinning.Should().BeTrue("the mod would lose a file it wins as an external change");
                content.KeepWinning = keepWinning;
                return Accept();
            };

            await OnUi(() => page.MoveToExistingMod());

            await _mover.Received(1).Move(seeded.Loadout, Arg.Any<GamePath[]>(), seeded.PlainMod,
                Arg.Is<MoveOptions>(o => o.PinMovedPaths == keepWinning));
        }
    }

    [Fact]
    public async Task DecliningTheConfirmationMovesNothing()
    {
        var seeded = await SeedLoadout();
        var page = await PageOver(seeded.Overrides);
        await Select(page, ModelFile);
        _mover.Preview(seeded.Loadout, Arg.Any<GamePath[]>(), seeded.PlainMod).Returns([NoConflict(ModelFile, seeded.PlainMod)]);
        _answer = dialog =>
        {
            var content = Content(dialog);
            if (content.Mods.Count == 0) return new StandardDialogResult { ButtonId = ButtonDefinitionId.Cancel };
            content.SelectedMod = content.Mods.Single(m => m.Id == seeded.PlainMod);
            return Accept();
        };

        await OnUi(() => page.MoveToExistingMod());

        _dialogs.Should().HaveCount(2, "the picker, then the confirmation");
        await _mover.DidNotReceiveWithAnyArgs().Move(default, default!, default, default!);
    }

    #endregion

    [Fact]
    public async Task ARefusedMoveIsShownThroughTheErrorPresenter()
    {
        var seeded = await SeedLoadout();
        var page = await PageOver(seeded.Overrides);
        await Select(page, ModelFile);
        var refusal = new OverridesMoveRefusedException("`meshes/rock/rock.nif` is no longer among the external changes, so it was not moved.");
        _mover.CreateEmptyGroup(seeded.Loadout, "Rock").Returns(seeded.PlainMod);
        _mover.Preview(seeded.Loadout, Arg.Any<GamePath[]>(), seeded.PlainMod).Returns([NoConflict(ModelFile, seeded.PlainMod)]);
        _mover.Move(seeded.Loadout, Arg.Any<GamePath[]>(), seeded.PlainMod, Arg.Any<MoveOptions>())
            .Returns<Task<OverridesMoveResult>>(_ => throw refusal);
        _answer = _ => Accept("Rock");

        await OnUi(() => page.MoveToNewMod());

        await _errors.Received(1).Report(refusal, Arg.Any<string>());
    }
}
