using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using R3;

namespace SilverModManager.App.UI.Overlays;

public record struct RemoveGameOverlayResult(bool ShouldRemoveGame, bool ShouldDeleteDownloads)
{
    public static readonly RemoveGameOverlayResult Cancel = new(ShouldRemoveGame: false, ShouldDeleteDownloads: false);
}

public interface IRemoveGameOverlayViewModel : IOverlayViewModel<RemoveGameOverlayResult>
{
    string GameName { get; }

    int NumDownloads { get; }

    Size SumDownloadsSize { get; }

    int NumCollections { get; }

    BindableReactiveProperty<bool> ShouldDeleteDownloads { get; }

    /// <summary>
    /// Files of the game's loadouts the app holds no backup copy of; removing the game copies them first when the
    /// game folder is cleaned.
    /// </summary>
    UnbackedFiles Unbacked { get; }

    ReactiveCommand<Unit> CommandCancel { get; }

    ReactiveCommand<Unit> CommandRemove { get; }
}
