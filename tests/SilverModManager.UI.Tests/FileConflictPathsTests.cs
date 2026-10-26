using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.App.UI.Dialog;
using SilverModManager.App.UI.Dialog.Enums;
using SilverModManager.App.UI.Pages.Sorting;
using SilverModManager.App.UI.Windows;
using SilverModManager.Games.FromSoft.EldenRing;
using SilverModManager.Games.FromSoft.Merge;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk.Dialog;
using SilverModManager.UI.Sdk.Dialog.Enums;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.UI.Tests;

/// <summary>
/// The per-path list behind "View" on the File Conflicts page: each conflicting path with its winner and losers,
/// and pinning or unpinning a winner from it.
/// </summary>
public class FileConflictPathsTests(IServiceProvider provider) : AUiTest(provider)
{
    private static readonly GamePath Contested = new(LocationId.Game, "archive/pc/mod/contested.archive");
    private static readonly GamePath Package = new(LocationId.Me3Mods, "pkg/regulation.bin");

    private record Mods(Loadout.ReadOnly Loadout, LoadoutItemGroupId Low, LoadoutItemGroupId High, LoadoutItemGroupPriorityId LowPriority);

    private ILoadoutManager Loadouts => Provider.GetRequiredService<ILoadoutManager>();

    /// <summary>Low (priority 1) and High (priority 2) both provide each of the given paths with different content.</summary>
    private async Task<Mods> SeedConflict(string name, params GamePath[] paths)
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, name);
        using var tx = Connection.BeginTransaction();
        var low = NewGroup(tx, loadout.LoadoutId, "Low Mod");
        var high = NewGroup(tx, loadout.LoadoutId, "High Mod");
        foreach (var path in paths)
        {
            NewFile(tx, loadout.LoadoutId, low, path, 1);
            NewFile(tx, loadout.LoadoutId, high, path, 2);
        }
        var lowPriority = new LoadoutItemGroupPriority.New(tx) { LoadoutId = loadout.LoadoutId, TargetId = low, Priority = ConflictPriority.From(1) };
        _ = new LoadoutItemGroupPriority.New(tx) { LoadoutId = loadout.LoadoutId, TargetId = high, Priority = ConflictPriority.From(2) };
        var result = await tx.Commit();
        return new Mods(
            loadout,
            LoadoutItemGroupId.From(result[low.Value].Value),
            LoadoutItemGroupId.From(result[high.Value].Value),
            LoadoutItemGroupPriorityId.From(result[lowPriority.Id].Value));
    }

    private static LoadoutItemGroupId NewGroup(ITransaction tx, LoadoutId loadoutId, string name)
    {
        var group = new LoadoutItemGroup.New(tx, out var id)
        {
            IsGroup = true,
            LoadoutItem = new LoadoutItem.New(tx, id) { LoadoutId = loadoutId, Name = name },
        };
        return group.Id;
    }

    private static void NewFile(ITransaction tx, LoadoutId loadoutId, LoadoutItemGroupId group, GamePath path, ulong content)
    {
        var id = tx.TempId();
        var item = new LoadoutItem.New(tx, id) { ParentId = group.Value, LoadoutId = loadoutId, Name = path.FileName };
        var withPath = new LoadoutItemWithTargetPath.New(tx, id) { LoadoutItem = item, TargetPath = path.ToGamePathParentTuple(loadoutId.Value) };
        _ = new LoadoutFile.New(tx, id) { LoadoutItemWithTargetPath = withPath, Hash = Hash.From(content), Size = Size.From(content) };
    }

    private FileConflictPathsViewModel OpenPaths(Mods mods, MergePathIndex? merges = null) => new(
        Loadouts,
        mods.Loadout.LoadoutId,
        mods.Low,
        () => FileConflictPaths.Load(Connection, mods.Loadout.LoadoutId, mods.LowPriority, merges));

    /// <summary>The merge index of a FromSoft game whose package files are the given (package, path) pairs.</summary>
    private static MergePathIndex Merges(params (string Package, string RelPath)[] files) => MergePathIndex.Build(
        files.Select((f, i) => new ProviderFile(f.Package, f.RelPath, Hash.From((ulong)i + 1), (ulong)i + 1, f.Package)),
        EldenRingGame.Definition.Formats,
        new HashSet<string>());

    [Fact]
    public async Task EveryConflictingPathShowsItsWinnerAndLosers()
    {
        var mods = await SeedConflict("Per Path Rows", Contested);

        var row = OpenPaths(mods).Rows.Should().ContainSingle().Subject;

        row.Path.Should().Be(Contested.ToString());
        row.WinnerText.Should().Contain("High Mod").And.NotContain("(this)");
        row.LosersText.Should().Contain("Low Mod").And.Contain("(this)");
        row.IsPinned.Should().BeFalse();
        row.CanMakeWinner.Should().BeTrue();
        row.CanClearPin.Should().BeFalse();
    }

    [Fact]
    public async Task MakingThisTheWinnerPinsThePathAndTheRowReloads()
    {
        var mods = await SeedConflict("Pin From Page", Contested);
        var paths = OpenPaths(mods);

        await paths.MakeWinner(Contested);

        LoadoutFileWinner.FindByLoadout(Connection.Db, mods.Loadout.LoadoutId).Should().ContainSingle()
            .Which.WinnerGroupId.Value.Should().Be(mods.Low.Value);
        var row = paths.Rows.Should().ContainSingle().Subject;
        row.WinnerText.Should().Contain("Low Mod").And.Contain("(this)");
        row.IsPinned.Should().BeTrue();
        row.PinText.Should().Contain("Low Mod");
        row.CanMakeWinner.Should().BeFalse();
        row.CanClearPin.Should().BeTrue();
    }

    [Fact]
    public async Task ClearingThePinHandsThePathBackToPriority()
    {
        var mods = await SeedConflict("Clear From Page", Contested);
        var paths = OpenPaths(mods);
        await paths.MakeWinner(Contested);

        await paths.ClearPin(Contested);

        LoadoutFileWinner.FindByLoadout(Connection.Db, mods.Loadout.LoadoutId).Should().BeEmpty();
        var row = paths.Rows.Should().ContainSingle().Subject;
        row.WinnerText.Should().Contain("High Mod");
        row.IsPinned.Should().BeFalse();
        row.CanMakeWinner.Should().BeTrue();
    }

    [Fact]
    public async Task APinOnAnotherModIsNamedAndCanBeReplacedByThisOne()
    {
        var mods = await SeedConflict("Pin To Other", Contested);
        await Loadouts.PinFileWinner(mods.Loadout.LoadoutId, Contested, mods.High);

        var paths = OpenPaths(mods);
        var row = paths.Rows.Should().ContainSingle().Subject;
        row.IsPinned.Should().BeTrue();
        row.PinText.Should().Contain("High Mod");
        row.CanMakeWinner.Should().BeTrue();

        await paths.MakeWinner(Contested);

        paths.Rows.Single().WinnerText.Should().Contain("Low Mod");
    }

    [Fact]
    public async Task APackagePathThatNoOtherPackageProvidesCanBePinned()
    {
        // Two mods writing the same package folder is not a merge: the sync tree's winner, pin included, deploys.
        var mods = await SeedConflict("Shared Package", Package);
        var paths = OpenPaths(mods, Merges(("pkg", "regulation.bin"), ("pkg", "regulation.bin")));

        var row = paths.Rows.Should().ContainSingle().Subject;
        row.CanMakeWinner.Should().BeTrue();
        row.Note.Should().BeNull();

        await paths.MakeWinner(Package);

        paths.Rows.Single().WinnerText.Should().Contain("Low Mod");
    }

    [Fact]
    public async Task AFileThatFeedsAMergeSaysPinsDoNotApplyAndCannotBePinned()
    {
        var mods = await SeedConflict("Merged Package", Contested, Package);
        var paths = OpenPaths(mods, Merges(("pkg", "regulation.bin"), ("pkg", "regulation.bin"), ("other", "regulation.bin")));

        var package = paths.Rows.Single(r => r.Path == Package.ToString());
        package.CanMakeWinner.Should().BeFalse();
        package.Note.Should().NotBeNullOrEmpty();

        var plain = paths.Rows.Single(r => r.Path == Contested.ToString());
        plain.CanMakeWinner.Should().BeTrue();
        plain.Note.Should().BeNull();

        var act = () => paths.MakeWinner(Package);
        await act.Should().ThrowAsync<InvalidOperationException>();
        LoadoutFileWinner.FindByLoadout(Connection.Db, mods.Loadout.LoadoutId).Should().BeEmpty();
    }

    [Fact]
    public async Task ViewOnAModOpensItsPerPathList()
    {
        var mods = await SeedConflict("View Opens List", Contested);
        var windows = Substitute.For<IWindowManager>();
        IDialog? shown = null;
        _ = windows.ShowDialog(Arg.Do<IDialog>(d => shown = d), DialogWindowType.Modeless);
        var page = new FileConflictsViewModel(Provider, windows, mods.Loadout.LoadoutId);

        await page.ViewConflicts(new FileConflictsTreeDataGridAdapter.ViewConflictsMessage(mods.LowPriority));

        var list = shown.Should().BeOfType<Dialog>().Subject.ViewModel.ContentViewModel.Should()
            .BeAssignableTo<IFileConflictPathsViewModel>().Subject;
        list.Rows.Should().ContainSingle().Which.Path.Should().Be(Contested.ToString());
    }

    [Fact]
    public async Task TheViewOffersMakeWinnerOnALosingPathAndClearPinOncePinned()
    {
        var mods = await SeedConflict("Path List View", Contested);
        var paths = OpenPaths(mods);

        Window? window = null;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            window = Track(new Window { Content = new FileConflictPathsView { ViewModel = paths } });
            ShowRendered(window);

            ButtonLabels(window).Should().Contain(SilverModManager.App.UI.Resources.Language.FileConflicts_Paths_MakeWinner)
                .And.NotContain(SilverModManager.App.UI.Resources.Language.FileConflicts_Paths_ClearPin);
        });

        // The rows change off the UI thread once the pin is written; the open view has to pick them up.
        await paths.MakeWinner(Contested);

        await EventuallyOnUi(() =>
        {
            ShowRendered(window!);
            ButtonLabels(window!).Should().Contain(SilverModManager.App.UI.Resources.Language.FileConflicts_Paths_ClearPin)
                .And.NotContain(SilverModManager.App.UI.Resources.Language.FileConflicts_Paths_MakeWinner);
        });
    }

    /// <summary>The captions of the buttons a window shows; a button hidden by its row is not visible.</summary>
    private static string?[] ButtonLabels(Window window) => window.GetVisualDescendants().OfType<Button>()
        .Where(b => b.IsEffectivelyVisible).Select(b => b.Content as string).ToArray();
}
