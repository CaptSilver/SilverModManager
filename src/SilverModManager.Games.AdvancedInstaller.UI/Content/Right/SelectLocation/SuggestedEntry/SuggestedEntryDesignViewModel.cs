
using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.AdvancedInstaller.UI.SelectLocation;

public class SuggestedEntryDesignViewModel() : SuggestedEntryViewModel(
    Guid.NewGuid(),
    CreateDesignAbsolutePath(),
    DataLocation,
    new GamePath(LocationId.Game, "Data"))
{
    private static readonly LocationId DataLocation = LocationId.From("Data", "Data folder");

    private static AbsolutePath CreateDesignAbsolutePath()
    {
        var fs = new InMemoryFileSystem();
        return fs.FromUnsanitizedFullPath("C:/Games/Skyrim Special Edition/Data");
    }
}
