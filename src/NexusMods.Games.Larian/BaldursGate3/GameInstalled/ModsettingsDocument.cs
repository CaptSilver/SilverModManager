using System.Xml;
using NexusMods.Games.Larian.BaldursGate3.Utils.LsxXmlParsing;

namespace NexusMods.Games.Larian.BaldursGate3.GameInstalled;

/// <summary>
/// Everything `modsettings.lsx` says, in file order. <see cref="ModsettingsFileFormat"/> only keeps what the
/// load order needs and drops the first GustavDev entry; this keeps every entry and the file's own
/// `&lt;version&gt;` attributes, so the file can be recognised against and rewritten without losing anything.
/// </summary>
public sealed class ModsettingsDocument
{
    /// <summary>One `ModuleShortDesc` node. Every value is the attribute text as written.</summary>
    public sealed record ModuleEntry(
        string Folder,
        string Name,
        string PublishHandle,
        string Uuid,
        string Version64,
        string Md5)
    {
        /// <summary>True when the entry carries a mod.io handle, meaning the game downloaded the module.</summary>
        public bool IsPublished => IsPublishHandle(PublishHandle);
    }

    /// <summary>
    /// "0" is the game's own modules and locally installed mods. Older files and other managers leave the
    /// attribute out, which says the same thing.
    /// </summary>
    public static bool IsPublishHandle(string? handle) => !string.IsNullOrEmpty(handle) && handle != "0";

    /// <summary>The attributes of the `&lt;version&gt;` element (major, minor, revision, build), as written.</summary>
    public required IReadOnlyDictionary<string, string> VersionAttributes { get; init; }

    /// <summary>Every module entry, in file order.</summary>
    public required IReadOnlyList<ModuleEntry> Entries { get; init; }

    /// <summary>The entries the game lists without a mod.io handle: its own modules and locally installed mods.</summary>
    public IEnumerable<ModuleEntry> BaseEntries => Entries.Where(e => !e.IsPublished);

    /// <exception cref="XmlException">The text is not well-formed XML.</exception>
    /// <exception cref="InvalidDataException">The XML has no `save` root.</exception>
    public static ModsettingsDocument Parse(string xml)
    {
        var versionAttributes = new Dictionary<string, string>();
        var entries = new List<ModuleEntry>();
        var sawSave = false;

        using var stringReader = new StringReader(xml);
        using var reader = XmlReader.Create(stringReader);
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element) continue;

            switch (reader.Name)
            {
                case "save":
                    sawSave = true;
                    break;
                case "version" when reader.HasAttributes:
                    while (reader.MoveToNextAttribute()) versionAttributes[reader.Name] = reader.Value;
                    reader.MoveToElement();
                    break;
                case "node" when reader.GetAttribute("id") == "ModuleShortDesc":
                    entries.Add(ReadEntry(reader));
                    break;
            }
        }

        if (!sawSave) throw new InvalidDataException("modsettings.lsx has no <save> element");
        return new ModsettingsDocument { VersionAttributes = versionAttributes, Entries = entries };
    }

    private static ModuleEntry ReadEntry(XmlReader reader)
    {
        string folder = "", name = "", handle = "", uuid = "", version = "", md5 = "";
        if (reader.IsEmptyElement) return new ModuleEntry(folder, name, handle, uuid, version, md5);

        while (reader.Read() && reader is not { NodeType: XmlNodeType.EndElement, Name: "node" })
        {
            if (reader is not { NodeType: XmlNodeType.Element, Name: "attribute" }) continue;

            var value = reader.GetAttribute("value") ?? "";
            switch (reader.GetAttribute("id"))
            {
                case "Folder": folder = value; break;
                case "Name": name = value; break;
                case "PublishHandle": handle = value; break;
                case "UUID": uuid = value; break;
                case "Version64": version = value; break;
                case "MD5": md5 = value; break;
            }
        }

        return new ModuleEntry(folder, name, handle, uuid, version, md5);
    }
}
