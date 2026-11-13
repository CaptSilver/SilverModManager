using SilverModManager.Games.Larian.Engine.GameInstalled;

namespace SilverModManager.Games.Larian.Engine.Modsettings;

/// <summary>
/// One dialect of <c>modsettings.lsx</c>: how its text becomes a <see cref="ModsettingsDocument"/>, how a list of
/// entries becomes text again, and which entries belong to the game rather than to the app's paks.
/// </summary>
public interface IModsettingsSchema
{
    /// <exception cref="System.Xml.XmlException">The text is not well-formed XML.</exception>
    /// <exception cref="InvalidDataException">The XML is not a mod list of this dialect.</exception>
    ModsettingsDocument Parse(string xml);

    /// <summary>
    /// The file's text. <paramref name="layoutFrom"/> is the file being rewritten, whose byte order mark and line
    /// ending carry over; null when there is no file.
    /// </summary>
    string Render(IReadOnlyDictionary<string, string> version, IEnumerable<ModsettingsDocument.ModuleEntry> entries, ModsettingsDocument? layoutFrom);

    /// <summary>The <c>&lt;version&gt;</c> attributes of a file the current game writes, for when there is none to keep.</summary>
    IReadOnlyDictionary<string, string> DefaultVersion { get; }

    /// <summary>
    /// The entries of <paramref name="document"/> that are the game's own, in file order. The app writes them
    /// back as they were and puts its paks after them.
    /// </summary>
    /// <param name="adventureUuids">The modules of the loadout that are adventures, for dialects that tell the game's entries apart by them.</param>
    IReadOnlyList<ModsettingsDocument.ModuleEntry> GameEntries(ModsettingsDocument document, IReadOnlySet<string> adventureUuids);

    /// <summary>Whether a file can be written when the game has not made one.</summary>
    bool CanCreateFromNothing { get; }

    /// <summary>What a file written from nothing lists first. Empty when <see cref="CanCreateFromNothing"/> is false.</summary>
    IReadOnlyList<ModsettingsDocument.ModuleEntry> EntriesWhenNoFile { get; }
}
