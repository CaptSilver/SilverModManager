using NexusMods.Paths;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace NexusMods.App.UI.Pages.Wabbajack;

/// <summary>A file of the game folder the list would replace, and whether the user wants to keep their own copy.</summary>
public class WabbajackGameFileChoice(RelativePath path) : ReactiveObject
{
    public RelativePath Path { get; } = path;

    /// <summary>Keep the game folder's copy instead of the list's.</summary>
    [Reactive] public bool Keep { get; set; }
}
