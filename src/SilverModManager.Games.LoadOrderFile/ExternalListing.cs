namespace SilverModManager.Games.LoadOrderFile;

/// <summary>How the order file treats the mods an <see cref="IExternalModSource"/> finds.</summary>
public enum ExternalListing
{
    /// <summary>Every mod found is listed, whether or not the file names it. For mods the game cannot run without.</summary>
    Always,

    /// <summary>Only the mods the file already names are listed; the rest stay off.</summary>
    WhenListed,

    /// <summary>
    /// Like <see cref="WhenListed"/>, except that with no order file at all every mod found is listed. For games that
    /// load everything until the file exists.
    /// </summary>
    WhenListedOrNoFile,
}
