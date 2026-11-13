using System.Xml;
using System.Xml.Linq;
using NexusMods.Paths;

namespace SilverModManager.Games.LoadOrderFile;

/// <summary>
/// RimWorld's <c>ModsConfig.xml</c>: the <c>packageId</c> of each active mod, lower-cased, in load order. The game also
/// keeps its own <c>version</c> and <c>knownExpansions</c> in the file, and drops the mods it lists when the version
/// does not match its own, so a render keeps every element but <c>activeMods</c> as the game wrote it.
/// </summary>
public sealed class ModsConfigXmlFormat : ILoadOrderFileFormat
{
    private const string Declaration = "<?xml version=\"1.0\" encoding=\"utf-8\"?>";

    public string? ModOf(RelativePath fileInModFolder)
    {
        var parts = fileInModFolder.Path.Split('/');
        return parts.Length >= 2 ? parts[0] : null;
    }

    public IReadOnlyList<string> Listed(string fileText)
    {
        var root = Parse(fileText);
        if (root is null) return [];

        return root.Elements("activeMods").Elements("li")
            .Select(item => item.Value.Trim().ToLowerInvariant())
            .Where(id => id.Length > 0)
            .ToList();
    }

    public string Render(IReadOnlyList<string> mods, string? currentFileText)
    {
        var active = new XElement("activeMods", mods.Select(mod => new XElement("li", mod)));
        var kept = (currentFileText is null ? null : Parse(currentFileText)) is { } current
            ? current.Elements().ToList()
            : [];

        // Where the game had activeMods stays where it was; a file without it gets one at the end.
        var lines = new List<string>();
        var wroteActive = false;
        foreach (var element in kept)
        {
            if (element.Name == "activeMods")
            {
                if (wroteActive) continue;
                wroteActive = true;
                lines.Add(Indent(active));
            }
            else
            {
                lines.Add(Indent(element));
            }
        }

        if (!wroteActive) lines.Add(Indent(active));

        return $"{Declaration}\n<ModsConfigData>\n{string.Join('\n', lines)}\n</ModsConfigData>";
    }

    private static string Indent(XElement element) => "  " + element.ToString().Replace("\n", "\n  ");

    /// <summary>The root of a <c>ModsConfigData</c> file, or null when the text is not one.</summary>
    private static XElement? Parse(string text)
    {
        try
        {
            var root = XDocument.Parse(text.TrimStart('﻿')).Root;
            return root?.Name == "ModsConfigData" ? root : null;
        }
        catch (XmlException)
        {
            return null;
        }
    }
}
