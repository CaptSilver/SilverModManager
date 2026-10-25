using System.Collections.Immutable;
using SilverModManager.Games.FromSoft.Cleanup.SaveToLibrary;
using SilverModManager.UI.Sdk;

namespace SilverModManager.App.UI.Pages.Diagnostics.SaveToLibrary;

/// <summary>One mod that can be saved, with the box the user ticks.</summary>
public sealed class SaveToLibraryRow(ModToSave mod, string link)
{
    public ModToSave Mod { get; } = mod;

    public string Label { get; } = $"{mod.Name} {mod.Version}";

    /// <summary>The page the file is on, shown so the user can see where it comes from.</summary>
    public string Link { get; } = link;

    public bool IsChecked { get; set; } = true;
}

public interface ISaveToLibraryDialogViewModel : IViewModelInterface
{
    string Intro { get; }

    IReadOnlyList<SaveToLibraryRow> Rows { get; }

    /// <summary>What is shown but not downloaded, one line each.</summary>
    IReadOnlyList<string> Notes { get; }

    bool HasRows { get; }

    bool HasNotes { get; }
}

public sealed class SaveToLibraryDialogViewModel(string intro, IReadOnlyList<SaveToLibraryRow> rows, IReadOnlyList<string> notes)
    : AViewModel<ISaveToLibraryDialogViewModel>, ISaveToLibraryDialogViewModel
{
    public string Intro { get; } = intro;

    public IReadOnlyList<SaveToLibraryRow> Rows { get; } = rows;

    public IReadOnlyList<string> Notes { get; } = notes;

    public bool HasRows => Rows.Count > 0;

    public bool HasNotes => Notes.Count > 0;

    /// <summary>The mods whose box is still ticked.</summary>
    public ImmutableArray<ModToSave> Checked() => [..Rows.Where(r => r.IsChecked).Select(r => r.Mod)];
}
