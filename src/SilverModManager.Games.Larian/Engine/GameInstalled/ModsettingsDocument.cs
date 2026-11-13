using System.Text;
using System.Xml;
using SilverModManager.Games.Larian.Engine.Utils.LsxXmlParsing;

namespace SilverModManager.Games.Larian.Engine.GameInstalled;

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
        string Md5,
        string Type = "",
        IReadOnlyDictionary<string, string>? AttributeTypes = null)
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

    /// <summary>
    /// The module UUIDs of a separate order list (Divinity: Original Sin 2's <c>ModOrder</c> node), as read, or null
    /// for a dialect that has only the one list. <see cref="Entries"/> is the order the game loads.
    /// </summary>
    public IReadOnlyList<string>? OrderUuids { get; init; }

    /// <summary>Whether the text started with a byte order mark, which a rewrite keeps.</summary>
    public bool HasBom { get; init; }

    /// <summary>The line ending the file used, <c>"\n"</c> or <c>"\r\n"</c>, which a rewrite keeps.</summary>
    public string LineEnding { get; init; } = "\n";

    /// <exception cref="XmlException">The text is not well-formed XML.</exception>
    /// <exception cref="InvalidDataException">The XML has no `save` root.</exception>
    public static ModsettingsDocument Parse(string xml)
    {
        // A byte order mark that was read as text is the first character; the XML reader would refuse it in some positions.
        var hasBom = xml.StartsWith('\uFEFF');
        if (hasBom) xml = xml[1..];
        var lineEnding = xml.Contains("\r\n") ? "\r\n" : "\n";

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
        return new ModsettingsDocument { VersionAttributes = versionAttributes, Entries = entries, HasBom = hasBom, LineEnding = lineEnding };
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

    /// <summary>
    /// The file as the game reads it: the given `&lt;version&gt;` attributes, then one module node per entry in the
    /// order given. Indented like <see cref="ModsettingsFileFormat"/> writes it, and declared UTF-8 because that
    /// is what the bytes are. The byte order mark and line ending of <paramref name="layoutFrom"/>, the file being
    /// rewritten, are kept; with no file there is neither.
    /// </summary>
    internal static string Render(IReadOnlyDictionary<string, string> versionAttributes, IEnumerable<ModuleEntry> entries, ModsettingsDocument? layoutFrom = null)
    {
        using var writer = new Utf8StringWriter();
        var settings = new XmlWriterSettings { Indent = true, NewLineChars = layoutFrom?.LineEnding ?? "\n" };
        using (var xml = XmlWriter.Create(writer, settings))
        {
            xml.WriteStartDocument();
            xml.WriteStartElement("save");

            xml.WriteStartElement("version");
            foreach (var (name, value) in versionAttributes) xml.WriteAttributeString(name, value);
            xml.WriteEndElement();

            xml.WriteStartElement("region");
            xml.WriteAttributeString("id", "ModuleSettings");
            xml.WriteStartElement("node");
            xml.WriteAttributeString("id", "root");
            xml.WriteStartElement("children");
            xml.WriteStartElement("node");
            xml.WriteAttributeString("id", "Mods");
            xml.WriteStartElement("children");

            foreach (var entry in entries)
            {
                ModsettingsFileFormat.WriteModuleShortDesc(xml, new LsxXmlFormat.ModuleShortDesc
                {
                    Folder = entry.Folder,
                    Name = entry.Name,
                    PublishHandle = entry.PublishHandle,
                    Uuid = entry.Uuid,
                    Version = entry.Version64,
                    Md5 = entry.Md5,
                });
            }

            xml.WriteEndElement(); // children
            xml.WriteEndElement(); // node Mods
            xml.WriteEndElement(); // children
            xml.WriteEndElement(); // node root
            xml.WriteEndElement(); // region
            xml.WriteEndElement(); // save
            xml.WriteEndDocument();
        }

        return layoutFrom is { HasBom: true } ? "\uFEFF" + writer.ToString() : writer.ToString();
    }

    /// <summary>A string writer that says UTF-8 in the XML declaration, where the default says UTF-16.</summary>
    private sealed class Utf8StringWriter : StringWriter
    {
        public override Encoding Encoding => new UTF8Encoding(false);
    }
}
