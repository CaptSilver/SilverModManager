using SilverModManager.Games.Bethesda.Loot.Metadata.Yaml;
using NexusMods.Paths;
using YamlDotNet.RepresentationModel;
using static SilverModManager.Games.Bethesda.Loot.Metadata.Yaml.YamlGetters;

namespace SilverModManager.Games.Bethesda.Loot.Metadata;

/// <summary>
/// The contents of a masterlist or userlist: plugin metadata, global messages, known Bash Tags and
/// groups. Follows libloot's metadata/metadata_document.rs.
/// </summary>
public sealed class MetadataDocument
{
    private MetadataDocument(
        Dictionary<Filename, PluginMetadata> plugins,
        List<PluginMetadata> regexPlugins,
        List<Message> globals,
        List<string> bashTags,
        List<Group> groups,
        YamlNode? prelude)
    {
        Plugins = plugins;
        RegexPlugins = regexPlugins;
        Globals = globals;
        BashTags = bashTags;
        Groups = groups;
        Prelude = prelude;
    }

    /// <summary>A document with no metadata but the default group, which every document has.</summary>
    public static MetadataDocument Empty => new([], [], [], [], [new Group()], null);

    /// <summary>The metadata for plugins named exactly, by filename.</summary>
    public IReadOnlyDictionary<Filename, PluginMetadata> Plugins { get; }

    /// <summary>The metadata whose plugin name is a regex, in file order.</summary>
    public IReadOnlyList<PluginMetadata> RegexPlugins { get; }

    /// <summary>The messages that apply to the whole load order.</summary>
    public IReadOnlyList<Message> Globals { get; }

    public IReadOnlyList<string> BashTags { get; }

    /// <summary>The groups in file order; the default group is first unless the file lists it.</summary>
    public IReadOnlyList<Group> Groups { get; }

    /// <summary>
    /// The value of the <c>prelude:</c> key, or null if there is none. It only exists to hold
    /// anchors the rest of the file refers to; nothing reads it as metadata.
    /// </summary>
    public YamlNode? Prelude { get; }

    /// <summary>
    /// Reads metadata from YAML text.
    /// </summary>
    /// <exception cref="MetadataLoadException">The text holds no document, or more than one.</exception>
    /// <exception cref="MetadataParseException">The YAML is malformed or is not valid metadata.</exception>
    /// <exception cref="YamlMergeKeyException">A merge key's value is not a mapping or a sequence of mappings.</exception>
    public static MetadataDocument Load(string yaml)
    {
        var stream = YamlLoader.Load(yaml);

        if (stream.Documents.Count == 0)
            throw new MetadataLoadException(MetadataLoadFailure.NoDocuments, "no YAML document found");
        if (stream.Documents.Count > 1)
            throw new MetadataLoadException(MetadataLoadFailure.MoreThanOneDocument, $"expected 1 YAML document, found {stream.Documents.Count}");

        var root = MergeKeys.Process(stream.Documents[0].RootNode);
        if (root is not YamlMappingNode document)
            throw MetadataParseException.UnexpectedType(PositionOf(root), YamlObjectType.MetadataDocument, ExpectedType.Map);

        var plugins = new Dictionary<Filename, PluginMetadata>();
        var regexPlugins = new List<PluginMetadata>();
        foreach (var pluginNode in GetSlice(document, "plugins", YamlObjectType.MetadataDocument))
        {
            var plugin = MetadataYaml.ReadPluginMetadata(pluginNode);

            if (plugin.IsRegexPlugin)
            {
                regexPlugins.Add(plugin);
            }
            else if (!plugins.TryAdd(new Filename(plugin.Name), plugin))
            {
                // libloot reports the name the earlier entry used.
                var earlier = plugins[new Filename(plugin.Name)];
                throw MetadataParseException.DuplicateEntry(PositionOf(pluginNode), earlier.Name, YamlObjectType.PluginMetadata);
            }
        }

        var globals = GetSlice(document, "globals", YamlObjectType.MetadataDocument)
            .Select(MetadataYaml.ReadMessage)
            .ToList();

        var bashTags = new List<string>();
        foreach (var tagNode in GetSlice(document, "bash_tags", YamlObjectType.MetadataDocument))
        {
            bashTags.Add(CoreSchema.AsString(tagNode)
                ?? throw MetadataParseException.UnexpectedType(PositionOf(tagNode), YamlObjectType.BashTagsElement, ExpectedType.String));
        }

        var groupNames = new HashSet<string>(StringComparer.Ordinal);
        var groups = new List<Group>();
        foreach (var groupNode in GetSlice(document, "groups", YamlObjectType.MetadataDocument))
        {
            var group = MetadataYaml.ReadGroup(groupNode);

            if (!groupNames.Add(group.Name))
                throw MetadataParseException.DuplicateEntry(PositionOf(groupNode), group.Name, YamlObjectType.Group);

            groups.Add(group);
        }

        if (!groupNames.Contains(Group.DefaultName)) groups.Insert(0, new Group());

        return new MetadataDocument(plugins, regexPlugins, globals, bashTags, groups, GetValue(document, "prelude"));
    }

    /// <summary>
    /// Reads a masterlist after replacing its <c>prelude:</c> block with <paramref name="prelude"/>,
    /// the text of the shared prelude file. Error lines refer to the masterlist and the prelude as
    /// the user has them, not to the combined text.
    /// </summary>
    /// <exception cref="MetadataLoadException"/>
    /// <exception cref="MetadataParseException"/>
    /// <exception cref="YamlMergeKeyException"/>
    public static MetadataDocument LoadWithPrelude(string masterlist, string prelude)
    {
        var replaced = Yaml.Prelude.Replace(masterlist, prelude);

        try
        {
            return Load(replaced.Masterlist);
        }
        catch (MetadataParseException e) when (replaced.Diff is { } diff)
        {
            throw e.Adjusted(diff);
        }
        catch (YamlMergeKeyException e) when (replaced.Diff is { } diff)
        {
            throw e.Adjusted(diff);
        }
    }

    /// <summary>
    /// Reads metadata from a file.
    /// </summary>
    /// <exception cref="MetadataLoadException">The file does not exist, or holds no document or more than one.</exception>
    /// <exception cref="MetadataParseException"/>
    /// <exception cref="YamlMergeKeyException"/>
    public static MetadataDocument LoadFile(AbsolutePath path)
    {
        if (!path.FileExists)
            throw new MetadataLoadException(MetadataLoadFailure.PathNotFound, $"path not found: \"{path}\"");

        using var stream = path.Read();
        using var reader = new StreamReader(stream);
        return Load(reader.ReadToEnd());
    }

    /// <summary>
    /// The metadata for <paramref name="pluginName"/>: its exact entry, if any, with every regex
    /// entry that matches merged in, in file order. Null if the result holds nothing but the name.
    /// </summary>
    /// <exception cref="InvalidPluginNameException">The name contains regex characters and is not a valid regex.</exception>
    public PluginMetadata? FindPlugin(string pluginName)
    {
        var metadata = Plugins.TryGetValue(new Filename(pluginName), out var exact)
            ? exact
            : new PluginMetadata(pluginName);

        foreach (var regexPlugin in RegexPlugins)
        {
            if (regexPlugin.NameMatches(pluginName)) metadata = metadata.MergeMetadata(regexPlugin);
        }

        return metadata.HasNameOnly ? null : metadata;
    }
}
