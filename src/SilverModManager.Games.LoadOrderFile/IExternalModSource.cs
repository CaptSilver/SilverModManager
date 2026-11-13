using NexusMods.Paths;

namespace SilverModManager.Games.LoadOrderFile;

/// <summary>
/// Mods a game loads from a folder the app does not install into, such as a store's workshop or a mod folder the
/// player filled by hand. <see cref="Listing"/> says when the order file lists them.
/// </summary>
public interface IExternalModSource
{
    /// <summary>When the mods this source finds are listed.</summary>
    ExternalListing Listing { get; }

    /// <summary>The names the order file lists the mods under, in a fixed order; empty when the game folder has no such source.</summary>
    IReadOnlyList<string> Find(AbsolutePath gameFolder);
}
