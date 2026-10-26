using System.Windows.Input;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.App.UI.Resources;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk;

namespace SilverModManager.App.UI.Pages.Sorting;

/// <summary>A conflicting path with the actions the list offers on it.</summary>
public sealed class FileConflictPathRow
{
    public string Path { get; }
    public string WinnerText { get; }
    public string LosersText { get; }
    public bool IsPinned { get; }

    /// <summary>Who the path is pinned to; empty when it is not pinned.</summary>
    public string PinText { get; }

    /// <summary>Why a pin does nothing here; null for a path pins apply to.</summary>
    public string? Note { get; }
    public bool CanMakeWinner { get; }
    public bool CanClearPin { get; }
    public ICommand MakeWinnerCommand { get; }
    public ICommand ClearPinCommand { get; }

    internal FileConflictPathRow(FileConflictPath path, FileConflictPathsViewModel owner)
    {
        Path = path.Path.ToString();
        WinnerText = path.ThisWins
            ? string.Format(Language.FileConflicts_Paths_Winner, string.Format(Language.FileConflicts_Paths_This, path.WinnerName))
            : string.Format(Language.FileConflicts_Paths_Winner, path.WinnerName);
        LosersText = string.Format(Language.FileConflicts_Paths_Losers, string.Join(", ", path.Losers));
        IsPinned = path.PinnedToName is not null;
        PinText = path.PinnedToName is { } pinned ? string.Format(Language.FileConflicts_Paths_Pinned, pinned) : string.Empty;
        Note = path.PinsApply ? null : Language.FileConflicts_Paths_NoPins;
        CanMakeWinner = path.PinsApply && !path.ThisWins;
        CanClearPin = IsPinned;
        MakeWinnerCommand = ReactiveCommand.CreateFromTask(() => owner.MakeWinner(path.Path), outputScheduler: RxApp.MainThreadScheduler);
        ClearPinCommand = ReactiveCommand.CreateFromTask(() => owner.ClearPin(path.Path), outputScheduler: RxApp.MainThreadScheduler);
    }
}

public interface IFileConflictPathsViewModel : IViewModelInterface
{
    IReadOnlyList<FileConflictPathRow> Rows { get; }
}

/// <summary>
/// The per-path list behind "View" on a mod of the File Conflicts page: who wins each contested path, and a pin
/// that makes the mod win a path whatever the priorities say.
/// </summary>
public partial class FileConflictPathsViewModel : AViewModel<IFileConflictPathsViewModel>, IFileConflictPathsViewModel
{
    private readonly ILoadoutManager _loadouts;
    private readonly LoadoutId _loadoutId;
    private readonly LoadoutItemGroupId _thisGroup;
    private readonly Func<IReadOnlyList<FileConflictPath>> _load;
    private IReadOnlyList<FileConflictPath> _paths = [];

    [Reactive] public partial IReadOnlyList<FileConflictPathRow> Rows { get; private set; }

    /// <param name="thisGroup">The mod the list was opened for, the one a pin makes the winner.</param>
    /// <param name="load">Reads the paths again; called on construction and after every pin change.</param>
    public FileConflictPathsViewModel(ILoadoutManager loadouts, LoadoutId loadoutId, LoadoutItemGroupId thisGroup, Func<IReadOnlyList<FileConflictPath>> load)
    {
        _loadouts = loadouts;
        _loadoutId = loadoutId;
        _thisGroup = thisGroup;
        _load = load;
        Rows = BuildRows();
    }

    public async Task MakeWinner(GamePath path)
    {
        if (_paths.FirstOrDefault(p => p.Path == path) is { PinsApply: false })
            throw new InvalidOperationException($"{path} feeds a merge, which a pin does not change.");

        await _loadouts.PinFileWinner(_loadoutId, path, _thisGroup);
        Rows = BuildRows();
    }

    public async Task ClearPin(GamePath path)
    {
        await _loadouts.ClearFileWinner(_loadoutId, path);
        Rows = BuildRows();
    }

    private IReadOnlyList<FileConflictPathRow> BuildRows()
    {
        _paths = _load();
        return _paths.Select(path => new FileConflictPathRow(path, this)).ToArray();
    }
}
