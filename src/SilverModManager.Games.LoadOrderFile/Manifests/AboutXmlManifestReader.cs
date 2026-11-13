using System.Xml;
using System.Xml.Linq;
using NexusMods.Paths;

namespace SilverModManager.Games.LoadOrderFile.Manifests;

/// <summary>
/// RimWorld's <c>About/About.xml</c>. A mod is listed by its <c>packageId</c>, which the game compares without regard
/// to case, so ids are read lower-cased. The order lists have a plain form and one per game version
/// (<c>loadAfterByVersion/v1.5/li</c>); both count, because the manifest does not say which version is running.
/// </summary>
public sealed class AboutXmlManifestReader : IModManifestReader
{
    public RelativePath ManifestPath => "About/About.xml";

    public ModManifest? Read(byte[] bytes)
    {
        XDocument document;
        try
        {
            using var stream = new MemoryStream(bytes);
            document = XDocument.Load(stream);
        }
        catch (XmlException)
        {
            return null;
        }

        var root = document.Root;
        var id = root?.Element("packageId")?.Value.Trim().ToLowerInvariant();
        if (root is null || string.IsNullOrEmpty(id)) return null;

        return new ModManifest(
            id,
            root.Element("name")?.Value.Trim(),
            Dependencies(root),
            Items(root, "loadAfter"),
            Items(root, "loadBefore"),
            Items(root, "forceLoadAfter"),
            Items(root, "forceLoadBefore"),
            Items(root, "incompatibleWith"));
    }

    private static IReadOnlyList<string> Dependencies(XElement root) =>
    [
        .. root.Elements("modDependencies").Elements("li").Select(PackageIdOf).Where(id => id.Length > 0),
        .. root.Elements("modDependenciesByVersion").Elements().Elements("li").Select(PackageIdOf).Where(id => id.Length > 0),
    ];

    private static string PackageIdOf(XElement item) => Normalise(item.Element("packageId")?.Value);

    private static IReadOnlyList<string> Items(XElement root, string name) =>
    [
        .. root.Elements(name).Elements("li").Select(item => Normalise(item.Value)).Where(id => id.Length > 0),
        .. root.Elements(name + "ByVersion").Elements().Elements("li").Select(item => Normalise(item.Value)).Where(id => id.Length > 0),
    ];

    private static string Normalise(string? text) => (text ?? "").Trim().ToLowerInvariant();
}
