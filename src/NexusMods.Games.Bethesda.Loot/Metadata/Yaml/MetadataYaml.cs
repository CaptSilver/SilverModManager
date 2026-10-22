using YamlDotNet.RepresentationModel;
using static NexusMods.Games.Bethesda.Loot.Metadata.Yaml.YamlGetters;

namespace NexusMods.Games.Bethesda.Loot.Metadata.Yaml;

/// <summary>
/// Reads each metadata type from a YAML node, including the shorthand forms such as a bare
/// string for a file. Follows libloot's <c>TryFromYaml</c> implementations in metadata/*.rs.
/// </summary>
/// <remarks>
/// Where libloot names a different object type in an error than the one being read (a file's
/// <c>detail</c> is reported against "plugin cleaning data", message contents against "message"),
/// the same wording is kept so messages match.
/// </remarks>
internal static class MetadataYaml
{
    /// <exception cref="MetadataParseException"/>
    public static File ReadFile(YamlNode node)
    {
        if (CoreSchema.AsString(node) is { } name) return new File(name);

        if (node is not YamlMappingNode mapping)
            throw MetadataParseException.UnexpectedType(PositionOf(node), YamlObjectType.File, ExpectedType.MapOrString);

        var fileName = GetRequiredString(PositionOf(node), mapping, "name", YamlObjectType.File);
        var displayName = GetString(mapping, "display", YamlObjectType.File);
        var detail = GetValue(mapping, "detail") is { } detailNode
            ? ReadMessageContents(detailNode, "detail", YamlObjectType.PluginCleaningData)
            : default;
        var condition = ReadCondition(mapping, "condition", YamlObjectType.File);
        var constraint = ReadCondition(mapping, "constraint", YamlObjectType.File);

        return new File(fileName)
        {
            DisplayName = displayName?.Value,
            Detail = detail,
            Condition = condition,
            Constraint = constraint,
        };
    }

    /// <exception cref="MetadataParseException"/>
    public static Tag ReadTag(YamlNode node)
    {
        if (CoreSchema.AsString(node) is { } shorthand)
        {
            var (shorthandName, shorthandSuggestion) = Tag.NameAndSuggestion(shorthand);
            return new Tag(shorthandName, shorthandSuggestion);
        }

        if (node is not YamlMappingNode mapping)
            throw MetadataParseException.UnexpectedType(PositionOf(node), YamlObjectType.Tag, ExpectedType.MapOrString);

        var written = GetRequiredString(PositionOf(node), mapping, "name", YamlObjectType.Tag);
        var condition = ReadCondition(mapping, "condition", YamlObjectType.Tag);

        var (name, suggestion) = Tag.NameAndSuggestion(written);
        return new Tag(name, suggestion) { Condition = condition };
    }

    /// <exception cref="MetadataParseException"/>
    public static Location ReadLocation(YamlNode node)
    {
        if (CoreSchema.AsString(node) is { } url) return new Location(url);

        if (node is not YamlMappingNode mapping)
            throw MetadataParseException.UnexpectedType(PositionOf(node), YamlObjectType.Location, ExpectedType.MapOrString);

        var link = GetRequiredString(PositionOf(node), mapping, "link", YamlObjectType.Location);
        var name = GetRequiredString(PositionOf(node), mapping, "name", YamlObjectType.Location);

        return new Location(link) { Name = name };
    }

    /// <exception cref="MetadataParseException"/>
    public static Group ReadGroup(YamlNode node)
    {
        var mapping = AsMapping(node, YamlObjectType.Group);

        var name = GetRequiredString(PositionOf(node), mapping, "name", YamlObjectType.Group);
        var description = GetString(mapping, "description", YamlObjectType.Group);
        var after = GetStrings(mapping, "after", YamlObjectType.Group);

        return new Group(name) { Description = description?.Value, AfterGroups = after.ToArray() };
    }

    /// <exception cref="MetadataParseException"/>
    public static MessageContent ReadMessageContent(YamlNode node)
    {
        var mapping = AsMapping(node, YamlObjectType.MessageContent);

        var text = GetRequiredString(PositionOf(node), mapping, "text", YamlObjectType.Message);
        var language = GetRequiredString(PositionOf(node), mapping, "lang", YamlObjectType.Message);

        return new MessageContent(text) { Language = language };
    }

    /// <summary>
    /// A message's or file's text: a string for the default language, or a list of per-language
    /// contents of which one must be in the default language.
    /// </summary>
    /// <exception cref="MetadataParseException"/>
    public static EquatableArray<MessageContent> ReadMessageContents(YamlNode node, string key, YamlObjectType parentType)
    {
        MessageContent[] contents;
        if (CoreSchema.AsString(node) is { } text)
        {
            contents = [new MessageContent(text)];
        }
        else if (node is YamlSequenceNode sequence)
        {
            contents = sequence.Children.Select(ReadMessageContent).ToArray();
        }
        else
        {
            throw MetadataParseException.UnexpectedValueType(PositionOf(node), key, parentType, ExpectedType.ArrayOrString);
        }

        try
        {
            MessageContent.Validate(contents);
        }
        catch (MultilingualMessageContentException e)
        {
            throw new MetadataParseException(PositionOf(node), e.Message, e);
        }

        return contents;
    }

    /// <exception cref="MetadataParseException"/>
    public static Message ReadMessage(YamlNode node)
    {
        var mapping = AsMapping(node, YamlObjectType.Message);

        var type = GetRequiredString(PositionOf(node), mapping, "type", YamlObjectType.Message) switch
        {
            "warn" => MessageType.Warn,
            "error" => MessageType.Error,
            _ => MessageType.Say,
        };

        var contentNode = GetValue(mapping, "content")
            ?? throw MetadataParseException.MissingKey(PositionOf(node), "content", YamlObjectType.Message);
        var content = ReadMessageContents(contentNode, "content", YamlObjectType.Message);

        var subs = GetStrings(mapping, "subs", YamlObjectType.Message);
        if (subs.Count > 0)
        {
            try
            {
                content = content.Select(c => c with { Text = Message.Format(c.Text, subs) }).ToArray();
            }
            catch (MessageFormatException e)
            {
                throw new MetadataParseException(PositionOf(node), e.Message, e);
            }
        }

        var condition = ReadCondition(mapping, "condition", YamlObjectType.Message);

        return new Message(type, content) { Condition = condition };
    }

    /// <exception cref="MetadataParseException"/>
    public static PluginCleaningData ReadPluginCleaningData(YamlNode node)
    {
        var mapping = AsMapping(node, YamlObjectType.PluginCleaningData);

        var crc = GetUInt32(mapping, "crc", YamlObjectType.PluginCleaningData)
            ?? throw MetadataParseException.MissingKey(PositionOf(node), "crc", YamlObjectType.PluginCleaningData);
        var util = GetRequiredString(PositionOf(node), mapping, "util", YamlObjectType.PluginCleaningData);

        var itm = GetUInt32(mapping, "itm", YamlObjectType.PluginCleaningData) ?? 0;
        var udr = GetUInt32(mapping, "udr", YamlObjectType.PluginCleaningData) ?? 0;
        var nav = GetUInt32(mapping, "nav", YamlObjectType.PluginCleaningData) ?? 0;

        var detail = GetValue(mapping, "detail") is { } detailNode
            ? ReadMessageContents(detailNode, "detail", YamlObjectType.PluginCleaningData)
            : default;

        var condition = ReadCondition(mapping, "condition", YamlObjectType.File);

        return new PluginCleaningData(crc, util)
        {
            ItmCount = itm,
            DeletedReferenceCount = udr,
            DeletedNavmeshCount = nav,
            Detail = detail,
            Condition = condition,
        };
    }

    /// <exception cref="MetadataParseException"/>
    public static PluginMetadata ReadPluginMetadata(YamlNode node)
    {
        var mapping = AsMapping(node, YamlObjectType.PluginMetadata);

        var name = GetRequiredString(PositionOf(node), mapping, "name", YamlObjectType.PluginMetadata);

        PluginMetadata metadata;
        try
        {
            metadata = new PluginMetadata(name);
        }
        catch (InvalidPluginNameException e)
        {
            throw MetadataParseException.InvalidRegex(PositionOf(node), name, e);
        }

        var group = GetString(mapping, "group", YamlObjectType.PluginMetadata);

        return metadata with
        {
            Group = group?.Value,
            LoadAfter = ReadList(mapping, "after", ReadFile),
            Requirements = ReadList(mapping, "req", ReadFile),
            Incompatibilities = ReadList(mapping, "inc", ReadFile),
            Messages = ReadList(mapping, "msg", ReadMessage),
            Tags = ReadList(mapping, "tag", ReadTag),
            DirtyInfo = ReadList(mapping, "dirty", ReadPluginCleaningData),
            CleanInfo = ReadList(mapping, "clean", ReadPluginCleaningData),
            Locations = ReadList(mapping, "url", ReadLocation),
        };
    }

    private static EquatableArray<T> ReadList<T>(YamlMappingNode mapping, string key, Func<YamlNode, T> read)
    {
        return GetSlice(mapping, key, YamlObjectType.PluginMetadata).Select(read).ToArray();
    }
}
