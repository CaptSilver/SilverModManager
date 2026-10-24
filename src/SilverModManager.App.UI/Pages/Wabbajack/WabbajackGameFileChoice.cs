using NexusMods.Paths;
using ReactiveUI;
using ReactiveUI.SourceGenerators;

namespace SilverModManager.App.UI.Pages.Wabbajack;

/// <summary>A file of the game folder the list would replace, and whether the user wants to keep their own copy.</summary>
public partial class WabbajackGameFileChoice(RelativePath path) : ReactiveObject
{
    public RelativePath Path { get; } = path;

    /// <summary>Keep the game folder's copy instead of the list's.</summary>
    [Reactive] public partial bool Keep { get; set; }
}
