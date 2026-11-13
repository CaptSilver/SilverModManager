using System.Text;
using System.Xml;
using SilverModManager.Games.Larian.DivinityOriginalSin2DE;
using SilverModManager.Games.Larian.Engine.GameInstalled;

namespace SilverModManager.Games.Larian.Engine.Modsettings;

/// <summary>
/// Divinity: Original Sin 2's <c>modsettings.lsx</c>. The same list is written twice: a <c>ModOrder</c> node of bare
/// UUIDs and a <c>Mods</c> node of module descriptions. The game reads <c>Mods</c> for its load order, so that is
/// <see cref="ModsettingsDocument.Entries"/>, and <c>ModOrder</c> is kept beside it as read. Attribute types are
/// numeric ids that a rewrite copies from the file. A file always names the adventure the player picked and the
/// gift bags they switched on, which the app cannot know, so it never makes one from nothing.
/// </summary>
public sealed class Dos2ModsettingsSchema : IModsettingsSchema
{
    private const string UuidAttribute = "UUID";

    // The ids the game and Divinity Mod Manager write, in the order they write the attributes.
    private static readonly (string Id, string DefaultType)[] EntryAttributes =
    [
        ("Folder", "30"), ("MD5", "23"), ("Name", "22"), (UuidAttribute, "22"), ("Version", "4"),
    ];

    public ModsettingsDocument Parse(string xml)
    {
        var hasBom = xml.StartsWith('﻿');
        if (hasBom) xml = xml[1..];
        var lineEnding = xml.Contains("\r\n") ? "\r\n" : "\n";

        var versionAttributes = new Dictionary<string, string>();
        var entries = new List<ModsettingsDocument.ModuleEntry>();
        var order = new List<string>();
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
                case "node" when reader.GetAttribute("id") == "ModOrder" && !reader.IsEmptyElement:
                    ReadOrder(reader, order);
                    break;
                case "node" when reader.GetAttribute("id") == "ModuleShortDesc":
                    entries.Add(ReadEntry(reader));
                    break;
            }
        }

        if (!sawSave) throw new InvalidDataException("modsettings.lsx has no <save> element");
        return new ModsettingsDocument
        {
            VersionAttributes = versionAttributes,
            Entries = entries,
            OrderUuids = order,
            HasBom = hasBom,
            LineEnding = lineEnding,
        };
    }

    private static void ReadOrder(XmlReader reader, List<string> order)
    {
        var depth = reader.Depth;
        while (reader.Read() && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth))
        {
            if (reader is { NodeType: XmlNodeType.Element, Name: "attribute" } && reader.GetAttribute("id") == UuidAttribute)
                order.Add(reader.GetAttribute("value") ?? "");
        }
    }

    private static ModsettingsDocument.ModuleEntry ReadEntry(XmlReader reader)
    {
        var values = new Dictionary<string, string>();
        var types = new Dictionary<string, string>();

        if (!reader.IsEmptyElement)
        {
            var depth = reader.Depth;
            while (reader.Read() && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth))
            {
                if (reader is not { NodeType: XmlNodeType.Element, Name: "attribute" } || reader.GetAttribute("id") is not { } id) continue;

                values[id] = reader.GetAttribute("value") ?? "";
                if (reader.GetAttribute("type") is { } type) types[id] = type;
            }
        }

        string Value(string id) => values.GetValueOrDefault(id, "");
        return new ModsettingsDocument.ModuleEntry(
            Folder: Value("Folder"),
            Name: Value("Name"),
            PublishHandle: "",
            Uuid: Value(UuidAttribute),
            Version64: Value("Version"),
            Md5: Value("MD5"),
            AttributeTypes: types);
    }

    public string Render(IReadOnlyDictionary<string, string> version, IEnumerable<ModsettingsDocument.ModuleEntry> entries, ModsettingsDocument? layoutFrom)
    {
        var list = entries.ToList();
        var orderUuidType = list.Count > 0 ? TypeOf(list[0], UuidAttribute) : "22";

        using var writer = new Utf8StringWriter();
        var settings = new XmlWriterSettings { Indent = true, NewLineChars = layoutFrom?.LineEnding ?? "\n" };
        using (var xml = XmlWriter.Create(writer, settings))
        {
            xml.WriteStartDocument();
            xml.WriteStartElement("save");

            xml.WriteStartElement("header");
            xml.WriteAttributeString("version", "2");
            xml.WriteEndElement();

            xml.WriteStartElement("version");
            foreach (var (name, value) in version) xml.WriteAttributeString(name, value);
            xml.WriteEndElement();

            xml.WriteStartElement("region");
            xml.WriteAttributeString("id", "ModuleSettings");
            xml.WriteStartElement("node");
            xml.WriteAttributeString("id", "root");
            xml.WriteStartElement("children");

            xml.WriteStartElement("node");
            xml.WriteAttributeString("id", "ModOrder");
            xml.WriteStartElement("children");
            foreach (var entry in list)
            {
                xml.WriteStartElement("node");
                xml.WriteAttributeString("id", "Module");
                WriteAttribute(xml, UuidAttribute, entry.Uuid, orderUuidType);
                xml.WriteEndElement();
            }
            xml.WriteEndElement(); // children
            xml.WriteEndElement(); // node ModOrder

            xml.WriteStartElement("node");
            xml.WriteAttributeString("id", "Mods");
            xml.WriteStartElement("children");
            foreach (var entry in list)
            {
                xml.WriteStartElement("node");
                xml.WriteAttributeString("id", "ModuleShortDesc");
                foreach (var (id, _) in EntryAttributes) WriteAttribute(xml, id, ValueOf(entry, id), TypeOf(entry, id));
                xml.WriteEndElement();
            }
            xml.WriteEndElement(); // children
            xml.WriteEndElement(); // node Mods

            xml.WriteEndElement(); // children
            xml.WriteEndElement(); // node root
            xml.WriteEndElement(); // region
            xml.WriteEndElement(); // save
            xml.WriteEndDocument();
        }

        return layoutFrom is { HasBom: true } ? "﻿" + writer.ToString() : writer.ToString();
    }

    private static void WriteAttribute(XmlWriter xml, string id, string value, string type)
    {
        xml.WriteStartElement("attribute");
        xml.WriteAttributeString("id", id);
        xml.WriteAttributeString("value", value);
        xml.WriteAttributeString("type", type);
        xml.WriteEndElement();
    }

    private static string ValueOf(ModsettingsDocument.ModuleEntry entry, string id) => id switch
    {
        "Folder" => entry.Folder,
        "MD5" => entry.Md5,
        "Name" => entry.Name,
        UuidAttribute => entry.Uuid,
        _ => entry.Version64,
    };

    private static string TypeOf(ModsettingsDocument.ModuleEntry entry, string id) =>
        entry.AttributeTypes is { } types && types.TryGetValue(id, out var type)
            ? type
            : EntryAttributes.First(a => a.Id == id).DefaultType;

    public IReadOnlyDictionary<string, string> DefaultVersion { get; } = new Dictionary<string, string>
    {
        ["major"] = "3", ["minor"] = "6", ["revision"] = "9", ["build"] = "0",
    };

    /// <summary>
    /// The game's modules wherever they sit, the adventures of the loadout, and everything ahead of the first of
    /// those: a campaign's own dependencies are written before it, and may be mods. Everything else is an add-on.
    /// </summary>
    public IReadOnlyList<ModsettingsDocument.ModuleEntry> GameEntries(ModsettingsDocument document, IReadOnlySet<string> adventureUuids)
    {
        var entries = document.Entries;
        var firstAdventure = -1;
        for (var i = 0; i < entries.Count && firstAdventure < 0; i++)
        {
            if (adventureUuids.Contains(entries[i].Uuid)) firstAdventure = i;
        }

        return entries
            .Where((entry, index) => Dos2GameModules.Uuids.Contains(entry.Uuid)
                                     || adventureUuids.Contains(entry.Uuid)
                                     || index < firstAdventure)
            .ToList();
    }

    public bool CanCreateFromNothing => false;

    public IReadOnlyList<ModsettingsDocument.ModuleEntry> EntriesWhenNoFile => [];

    private sealed class Utf8StringWriter : StringWriter
    {
        public override Encoding Encoding => new UTF8Encoding(false);
    }
}
