using System.Xml;
using System.Xml.Linq;
using SilverModManager.Games.Generic.Simple;

namespace SilverModManager.Games.LoadOrderFile;

/// <summary>
/// Kingdom Come: Deliverance II's <c>mod.manifest</c> names a mod by its <c>modid</c> and, without one, by its
/// <c>name</c>; Mod Organizer 2's plugin lists that in <c>mod_order.txt</c> and falls back to the folder. A mod gets
/// a folder of that name, so the folder and the list agree.
/// </summary>
public sealed class ManifestModIdFolderName : IModFolderNameFromMarker
{
    private static readonly string[] Tags = ["modid", "name"];

    public string? FolderName(byte[] markerFile)
    {
        XDocument manifest;
        try
        {
            using var stream = new MemoryStream(markerFile);
            manifest = XDocument.Load(stream);
        }
        catch (XmlException)
        {
            return null;
        }

        foreach (var tag in Tags)
        {
            var text = manifest.Descendants(tag).FirstOrDefault()?.Value.Trim();
            if (!string.IsNullOrEmpty(text)) return IsFolderName(text) ? text : null;
        }

        return null;
    }

    private static bool IsFolderName(string name)
        => name is not ("." or "..") && name.IndexOfAny(['/', '\\', ':']) < 0 && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
}
