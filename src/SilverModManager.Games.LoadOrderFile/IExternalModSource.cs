using NexusMods.Paths;

namespace SilverModManager.Games.LoadOrderFile;

/// <summary>
/// Mods a game loads from a folder the app does not manage, such as a store's workshop. The app lists the ones the
/// order file already names, so writing the file does not switch them off.
/// </summary>
public interface IExternalModSource
{
    /// <summary>The names the order file lists the mods under, in a fixed order; empty when the game folder has no such source.</summary>
    IReadOnlyList<string> Find(AbsolutePath gameFolder);
}
