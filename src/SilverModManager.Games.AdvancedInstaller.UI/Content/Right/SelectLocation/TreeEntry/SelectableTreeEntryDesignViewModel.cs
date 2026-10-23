
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.AdvancedInstaller.UI.SelectLocation;

public class SelectableTreeEntryDesignViewModel() : SelectableTreeEntryViewModel(
    new GamePath(LocationId.Game, ""),
    SelectableDirectoryNodeStatus.Regular);
