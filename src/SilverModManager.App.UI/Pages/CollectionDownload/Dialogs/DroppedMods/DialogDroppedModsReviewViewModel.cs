using ReactiveUI;
using ReactiveUI.SourceGenerators;
using SilverModManager.Collections;
using SilverModManager.UI.Sdk;

namespace SilverModManager.App.UI.Pages.CollectionDownload.Dialogs.DroppedMods;

/// <summary>A mod the update took out of the collection, and whether the user wants it removed.</summary>
public partial class DroppedModChoice(DroppedMod mod) : ReactiveObject
{
    public DroppedMod Mod { get; } = mod;

    public string Name => Mod.Name;

    /// <summary>Remove the mod. Ticked to begin with, since the dialog's question is whether to remove them.</summary>
    [Reactive] public partial bool Remove { get; set; } = true;
}

public interface IDialogDroppedModsReviewViewModel : IViewModelInterface
{
    IReadOnlyList<DroppedModChoice> Mods { get; }
}

/// <summary>The checklist of the mods an update left out of the collection.</summary>
public class DialogDroppedModsReviewViewModel(IEnumerable<DroppedMod> mods) : AViewModel<IDialogDroppedModsReviewViewModel>, IDialogDroppedModsReviewViewModel
{
    public IReadOnlyList<DroppedModChoice> Mods { get; } = mods.Select(static mod => new DroppedModChoice(mod)).ToArray();

    /// <summary>The mods that are still ticked.</summary>
    public IReadOnlyList<DroppedMod> Selected() => Mods.Where(static choice => choice.Remove).Select(static choice => choice.Mod).ToArray();
}
