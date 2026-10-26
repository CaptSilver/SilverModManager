using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.App.UI.Dialog;
using SilverModManager.App.UI.Dialog.Enums;
using SilverModManager.App.UI.Pages.Sorting;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.Windows;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk.Dialog;
using SilverModManager.UI.Sdk.Dialog.Enums;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.UI.Tests;

/// <summary>
/// The Rules editor behind a mod of the File Conflicts page: the Before/After rules naming the mod, adding one
/// against another mod and deleting one.
/// </summary>
public class FileConflictRulesTests(IServiceProvider provider) : AUiTest(provider)
{
    private record Mods(Loadout.ReadOnly Loadout, LoadoutItemGroupId A, LoadoutItemGroupId B, LoadoutItemGroupId C, LoadoutItemGroupPriorityId APriority);

    private ILoadoutManager Loadouts => Provider.GetRequiredService<ILoadoutManager>();

    /// <summary>Three mods A, B and C with priorities 1, 2 and 3, in the player's editable "My Mods" collection.</summary>
    private async Task<Mods> Seed(string name)
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, name);
        using var tx = Connection.BeginTransaction();
        var myMods = NewCollection(tx, loadout.LoadoutId, "My Mods", isReadOnly: false);
        var a = NewGroup(tx, loadout.LoadoutId, "Mod A", myMods);
        var b = NewGroup(tx, loadout.LoadoutId, "Mod B", myMods);
        var c = NewGroup(tx, loadout.LoadoutId, "Mod C", myMods);
        var aPriority = new LoadoutItemGroupPriority.New(tx) { LoadoutId = loadout.LoadoutId, TargetId = a, Priority = ConflictPriority.From(1) };
        _ = new LoadoutItemGroupPriority.New(tx) { LoadoutId = loadout.LoadoutId, TargetId = b, Priority = ConflictPriority.From(2) };
        _ = new LoadoutItemGroupPriority.New(tx) { LoadoutId = loadout.LoadoutId, TargetId = c, Priority = ConflictPriority.From(3) };
        var result = await tx.Commit();
        return new Mods(
            loadout,
            LoadoutItemGroupId.From(result[a.Value].Value),
            LoadoutItemGroupId.From(result[b.Value].Value),
            LoadoutItemGroupId.From(result[c.Value].Value),
            LoadoutItemGroupPriorityId.From(result[aPriority.Id].Value));
    }

    private static LoadoutItemGroupId NewCollection(ITransaction tx, LoadoutId loadoutId, string name, bool isReadOnly)
    {
        _ = new CollectionGroup.New(tx, out var id)
        {
            IsReadOnly = isReadOnly,
            LoadoutItemGroup = new LoadoutItemGroup.New(tx, id)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, id) { Name = name, LoadoutId = loadoutId },
            },
        };
        return LoadoutItemGroupId.From(id.Value);
    }

    private static LoadoutItemGroupId NewGroup(ITransaction tx, LoadoutId loadoutId, string name, LoadoutItemGroupId? parent = null)
    {
        var group = new LoadoutItemGroup.New(tx, out var id)
        {
            IsGroup = true,
            LoadoutItem = new LoadoutItem.New(tx, id) { LoadoutId = loadoutId, Name = name, ParentId = parent?.Value ?? default },
        };
        return group.Id;
    }

    private FileConflictRulesViewModel Open(Mods mods, LoadoutItemGroupId group) => new(Connection, Loadouts, mods.Loadout.LoadoutId, group);

    [Fact]
    public async Task ThePickerOffersEveryOtherModButNotThisOne()
    {
        var mods = await Seed("Rules Picker");

        var rules = Open(mods, mods.A);

        rules.Candidates.Select(c => c.Name).Should().BeEquivalentTo("Mod B", "Mod C");
        rules.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task AddingAnAfterRuleStoresItAndListsIt()
    {
        var mods = await Seed("Rules Add After");
        var rules = Open(mods, mods.A);

        await rules.AddRule(mods.B, GroupRuleKind.After);

        var stored = LoadoutGroupRule.FindByLoadout(Connection.Db, mods.Loadout.LoadoutId).Should().ContainSingle().Subject;
        (stored.SourceId, stored.OtherId, stored.Kind).Should().Be((mods.A, mods.B, GroupRuleKind.After));
        rules.Error.Should().BeNull();
        rules.Rows.Should().ContainSingle().Which.Text.Should().Be(string.Format(Language.FileConflicts_Rules_Wins, "Mod B"));
    }

    [Fact]
    public async Task ABeforeRuleReadsAsLosingToTheOtherMod()
    {
        var mods = await Seed("Rules Add Before");
        var rules = Open(mods, mods.C);

        await rules.AddRule(mods.A, GroupRuleKind.Before);

        rules.Rows.Should().ContainSingle().Which.Text.Should().Be(string.Format(Language.FileConflicts_Rules_Loses, "Mod A"));
    }

    [Fact]
    public async Task ARuleNamingThisModAsTheOtherEndIsListedFromThisModsSide()
    {
        var mods = await Seed("Rules Other End");
        await Loadouts.AddGroupRule(mods.Loadout.LoadoutId, mods.B, mods.A, GroupRuleKind.After);

        // B wins over A, so from A's side A loses to B.
        Open(mods, mods.A).Rows.Should().ContainSingle().Which.Text.Should().Be(string.Format(Language.FileConflicts_Rules_Loses, "Mod B"));
        Open(mods, mods.C).Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task ARuleThatCreatesALoopIsRefusedInlineNamingBothMods()
    {
        var mods = await Seed("Rules Loop");
        await Loadouts.AddGroupRule(mods.Loadout.LoadoutId, mods.A, mods.B, GroupRuleKind.After);
        var rules = Open(mods, mods.B);

        await rules.AddRule(mods.A, GroupRuleKind.After);

        rules.Error.Should().Contain("Mod A").And.Contain("Mod B").And.Contain("loop");
        LoadoutGroupRule.FindByLoadout(Connection.Db, mods.Loadout.LoadoutId).Should().ContainSingle();
    }

    [Fact]
    public async Task DeletingARuleRemovesItAndRefreshesTheList()
    {
        var mods = await Seed("Rules Delete");
        var rules = Open(mods, mods.A);
        await rules.AddRule(mods.B, GroupRuleKind.After);
        var row = rules.Rows.Single();

        await rules.RemoveRule(row.RuleId);

        LoadoutGroupRule.FindByLoadout(Connection.Db, mods.Loadout.LoadoutId).Should().BeEmpty();
        rules.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task ARuleBetweenTwoModsOfACollectionIsMarkedWithTheCollection()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Rules Collection");
        LoadoutItemGroupId a, b;
        using (var tx = Connection.BeginTransaction())
        {
            // An installed Nexus collection is read-only; that is what tells its rules apart from the player's.
            var collection = NewCollection(tx, loadout.LoadoutId, "Big Collection", isReadOnly: true);
            var ga = NewGroup(tx, loadout.LoadoutId, "Mod A", collection);
            var gb = NewGroup(tx, loadout.LoadoutId, "Mod B", collection);
            _ = new LoadoutItemGroupPriority.New(tx) { LoadoutId = loadout.LoadoutId, TargetId = ga, Priority = ConflictPriority.From(1) };
            _ = new LoadoutItemGroupPriority.New(tx) { LoadoutId = loadout.LoadoutId, TargetId = gb, Priority = ConflictPriority.From(2) };
            var result = await tx.Commit();
            a = LoadoutItemGroupId.From(result[ga.Value].Value);
            b = LoadoutItemGroupId.From(result[gb.Value].Value);
        }
        await Loadouts.AddGroupRule(loadout.LoadoutId, a, b, GroupRuleKind.After);

        var rules = new FileConflictRulesViewModel(Connection, Loadouts, loadout.LoadoutId, a);
        var row = rules.Rows.Should().ContainSingle().Subject;
        row.CollectionText.Should().Be(string.Format(Language.FileConflicts_Rules_FromCollection, "Big Collection"));

        await rules.RemoveRule(row.RuleId);
        LoadoutGroupRule.FindByLoadout(Connection.Db, loadout.LoadoutId).Should().BeEmpty();
    }

    [Fact]
    public async Task APlayerRuleHasNoCollectionMark()
    {
        var mods = await Seed("Rules No Collection");
        var rules = Open(mods, mods.A);

        await rules.AddRule(mods.B, GroupRuleKind.After);

        rules.Rows.Single().CollectionText.Should().BeNull();
    }

    [Fact]
    public async Task TheRulesActionOpensTheEditorForThatMod()
    {
        var mods = await Seed("Rules Opens");
        var windows = Substitute.For<IWindowManager>();
        IDialog? shown = null;
        _ = windows.ShowDialog(Arg.Do<IDialog>(d => shown = d), DialogWindowType.Modeless);
        var page = new FileConflictsViewModel(Provider, windows, mods.Loadout.LoadoutId);

        await page.OpenRules(new FileConflictsTreeDataGridAdapter.RulesMessage(mods.APriority));

        var editor = shown.Should().BeOfType<Dialog>().Subject.ViewModel.ContentViewModel.Should()
            .BeAssignableTo<IFileConflictRulesViewModel>().Subject;
        editor.Candidates.Select(c => c.Name).Should().BeEquivalentTo("Mod B", "Mod C");
    }

    [Fact]
    public async Task TheViewShowsTheRuleWithADeleteButtonAndTheLoopRefusal()
    {
        var mods = await Seed("Rules View");
        var rules = Open(mods, mods.B);
        await Loadouts.AddGroupRule(mods.Loadout.LoadoutId, mods.A, mods.B, GroupRuleKind.After);
        await rules.AddRule(mods.C, GroupRuleKind.After);

        Window? window = null;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            window = Track(new Window { Content = new FileConflictRulesView { ViewModel = rules } });
            ShowRendered(window);
        });

        // A rule that contradicts the stored one reads as a refusal in the open view.
        await rules.AddRule(mods.A, GroupRuleKind.After);

        await EventuallyOnUi(() =>
        {
            ShowRendered(window!);
            var texts = window!.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToArray();
            texts.Should().Contain(string.Format(Language.FileConflicts_Rules_Wins, "Mod C"));
            texts.Should().Contain(rules.Error);
            window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).Select(b => b.Content as string)
                .Should().Contain(Language.FileConflicts_Rules_Delete);
        });
    }
}
