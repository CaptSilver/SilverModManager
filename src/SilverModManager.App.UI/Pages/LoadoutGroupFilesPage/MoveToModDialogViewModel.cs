using ReactiveUI.SourceGenerators;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.UI.Sdk;

namespace SilverModManager.App.UI.Pages.LoadoutGroupFilesPage;

/// <summary>A mod the External Changes files can be moved into.</summary>
public sealed record MoveTargetChoice(LoadoutItemGroupId Id, string Name)
{
    /// <inheritdoc />
    public override string ToString() => Name;
}

public interface IMoveToModDialogViewModel : IViewModelInterface
{
    /// <summary>What the dialog asks or tells.</summary>
    string Text { get; }

    /// <summary>The mods to pick from. Empty when the dialog only confirms a move.</summary>
    IReadOnlyList<MoveTargetChoice> Mods { get; }

    MoveTargetChoice? SelectedMod { get; set; }

    /// <summary>The duplicates and the files the mod would lose to another mod, one per line. Empty for the picker.</summary>
    string Details { get; }

    /// <summary>Whether the "keep these files winning" checkbox is offered: only when some file would lose.</summary>
    bool CanKeepWinning { get; }

    bool KeepWinning { get; set; }
}

/// <summary>
/// Content of the dialogs behind "Move to existing mod": the picker of destination mods, and the confirmation
/// that lists what the move would replace and what would stop winning.
/// </summary>
public partial class MoveToModDialogViewModel : AViewModel<IMoveToModDialogViewModel>, IMoveToModDialogViewModel
{
    public string Text { get; }
    public IReadOnlyList<MoveTargetChoice> Mods { get; }
    public string Details { get; }
    public bool CanKeepWinning { get; }
    [Reactive] public partial MoveTargetChoice? SelectedMod { get; set; }
    [Reactive] public partial bool KeepWinning { get; set; }

    public MoveToModDialogViewModel(string text, IReadOnlyList<MoveTargetChoice> mods, string details, bool canKeepWinning)
    {
        Text = text;
        Mods = mods;
        Details = details;
        CanKeepWinning = canKeepWinning;
    }
}
