using System.Reactive.Linq;
using System.Windows.Input;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.App.UI.Resources;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk;

namespace SilverModManager.App.UI.Pages.Sorting;

/// <summary>A mod the picker offers as the other end of a new rule.</summary>
public sealed record FileConflictRuleCandidate(LoadoutItemGroupId Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>One stored rule that names the mod, worded from that mod's side.</summary>
public sealed class FileConflictRuleRow
{
    public LoadoutGroupRuleId RuleId { get; }
    public string Text { get; }

    /// <summary>The collection that supplied the rule; null for a rule the player added.</summary>
    public string? CollectionText { get; }
    public ICommand RemoveCommand { get; }

    internal FileConflictRuleRow(LoadoutGroupRuleId ruleId, string text, string? collectionText, FileConflictRulesViewModel owner)
    {
        RuleId = ruleId;
        Text = text;
        CollectionText = collectionText;
        RemoveCommand = ReactiveCommand.CreateFromTask(() => owner.RemoveRule(ruleId), outputScheduler: RxApp.MainThreadScheduler);
    }
}

public interface IFileConflictRulesViewModel : IViewModelInterface
{
    IReadOnlyList<FileConflictRuleRow> Rows { get; }
    IReadOnlyList<FileConflictRuleCandidate> Candidates { get; }
    FileConflictRuleCandidate? SelectedCandidate { get; set; }

    /// <summary>Why the last rule was refused; null when there is nothing to say.</summary>
    string? Error { get; }
    ICommand AddAfterCommand { get; }
    ICommand AddBeforeCommand { get; }
}

/// <summary>
/// The Rules editor behind a mod of the File Conflicts page: the Before/After rules that name the mod, and a picker
/// to add one against another mod. A rule that would contradict the stored ones is refused inline.
/// </summary>
public partial class FileConflictRulesViewModel : AViewModel<IFileConflictRulesViewModel>, IFileConflictRulesViewModel
{
    private readonly ILoadoutManager _loadouts;
    private readonly LoadoutId _loadoutId;
    private readonly LoadoutItemGroupId _group;
    private readonly IConnection _connection;

    [Reactive] public partial IReadOnlyList<FileConflictRuleRow> Rows { get; private set; }
    [Reactive] public partial IReadOnlyList<FileConflictRuleCandidate> Candidates { get; private set; }
    [Reactive] public partial FileConflictRuleCandidate? SelectedCandidate { get; set; }
    [Reactive] public partial string? Error { get; private set; }

    public ICommand AddAfterCommand { get; }
    public ICommand AddBeforeCommand { get; }

    public FileConflictRulesViewModel(IConnection connection, ILoadoutManager loadouts, LoadoutId loadoutId, LoadoutItemGroupId group)
    {
        _connection = connection;
        _loadouts = loadouts;
        _loadoutId = loadoutId;
        _group = group;

        var hasSelection = this.WhenAnyValue(vm => vm.SelectedCandidate).Select(static candidate => candidate is not null);
        AddAfterCommand = ReactiveCommand.CreateFromTask(() => AddSelected(GroupRuleKind.After), hasSelection, RxApp.MainThreadScheduler);
        AddBeforeCommand = ReactiveCommand.CreateFromTask(() => AddSelected(GroupRuleKind.Before), hasSelection, RxApp.MainThreadScheduler);

        Candidates = LoadCandidates();
        Rows = LoadRows();
    }

    private Task AddSelected(GroupRuleKind kind) => SelectedCandidate is { } other ? AddRule(other.Id, kind) : Task.CompletedTask;

    /// <summary>Adds a rule saying this mod wins over (<see cref="GroupRuleKind.After"/>) or loses to the other mod.</summary>
    public async Task AddRule(LoadoutItemGroupId other, GroupRuleKind kind)
    {
        var result = await _loadouts.AddGroupRule(_loadoutId, _group, other, kind);
        if (result.IsCycle)
        {
            var db = _connection.Db;
            Error = string.Format(Language.FileConflicts_Rules_Loop, NameOf(db, result.Cycle.First), NameOf(db, result.Cycle.Second));
            return;
        }

        Error = null;
        Rows = LoadRows();
    }

    public async Task RemoveRule(LoadoutGroupRuleId ruleId)
    {
        await _loadouts.RemoveGroupRule(ruleId);
        Error = null;
        Rows = LoadRows();
    }

    private IReadOnlyList<FileConflictRuleCandidate> LoadCandidates()
    {
        var db = _connection.Db;
        return LoadoutItemGroupPriority.FindByLoadout(db, _loadoutId)
            .Select(static p => p.TargetId)
            .Where(id => id != _group)
            .Select(id => new FileConflictRuleCandidate(id, NameOf(db, id)))
            .OrderBy(static c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private IReadOnlyList<FileConflictRuleRow> LoadRows()
    {
        var db = _connection.Db;
        return LoadoutGroupRule.FindByLoadout(db, _loadoutId)
            .Where(rule => rule.SourceId == _group || rule.OtherId == _group)
            .Select(rule =>
            {
                // Worded from this mod's side: "B wins over A" is "loses to B" on A's page.
                var thisIsSource = rule.SourceId == _group;
                var otherId = thisIsSource ? rule.OtherId : rule.SourceId;
                var wins = (rule.Kind == GroupRuleKind.After) == thisIsSource;
                var text = string.Format(wins ? Language.FileConflicts_Rules_Wins : Language.FileConflicts_Rules_Loses, NameOf(db, otherId));
                return new FileConflictRuleRow(rule.LoadoutGroupRuleId, text, CollectionOf(db, rule), this);
            })
            .OrderBy(static row => row.Text, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// A rule between two mods of one installed collection is the collection's: the rows carry no author, and a
    /// collection only ever orders its own mods. Only installed collections are read-only; "My Mods" and the
    /// player's own collections are collection groups too, and a rule between two of their mods is the player's.
    /// </summary>
    private static string? CollectionOf(IDb db, LoadoutGroupRule.ReadOnly rule)
    {
        var source = LoadoutItem.Load(db, rule.SourceId);
        var other = LoadoutItem.Load(db, rule.OtherId);
        if (!source.HasParent() || !other.HasParent() || source.ParentId != other.ParentId) return null;
        if (!source.Parent.TryGetAsCollectionGroup(out var collection) || !collection.IsReadOnly) return null;

        return string.Format(Language.FileConflicts_Rules_FromCollection, collection.AsLoadoutItemGroup().AsLoadoutItem().Name);
    }

    private static string NameOf(IDb db, LoadoutItemGroupId id) => LoadoutItem.Load(db, id).Name;
}
