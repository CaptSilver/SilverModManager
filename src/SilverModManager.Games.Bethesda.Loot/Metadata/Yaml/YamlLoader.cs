using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace SilverModManager.Games.Bethesda.Loot.Metadata.Yaml;

/// <summary>
/// Loads YAML text into a node tree, reporting failures as <see cref="MetadataParseException"/>
/// with the position libloot would print.
/// </summary>
internal static class YamlLoader
{
    /// <summary>
    /// Every document in <paramref name="yaml"/>. An empty text has none.
    /// </summary>
    /// <remarks>
    /// Unlike saphyr, YamlDotNet rejects a mapping that repeats a key.
    /// </remarks>
    public static YamlStream Load(string yaml)
    {
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (AnchorNotFoundException e)
        {
            // The exception carries the start of the document, not of the alias.
            var position = FindUndefinedAlias(yaml) ?? SourcePosition.From(e.Start);
            throw new MetadataParseException(position, e.Message, e);
        }
        catch (YamlException e)
        {
            throw new MetadataParseException(SourcePosition.From(e.Start), e.Message, e);
        }
        catch (InvalidOperationException e)
        {
            // The scanner throws this, without a position, for some unterminated flow collections.
            throw new MetadataParseException(LastPositionReached(yaml), $"the YAML could not be parsed: {e.Message}", e);
        }

        return stream;
    }

    /// <summary>
    /// How far the parser gets before failing, as the best position to blame.
    /// </summary>
    private static SourcePosition LastPositionReached(string yaml)
    {
        var position = new SourcePosition(1, 0);
        try
        {
            var parser = new Parser(new StringReader(yaml));
            while (parser.MoveNext() && parser.Current is { } current) position = SourcePosition.From(current.End);
        }
        catch (Exception e) when (e is YamlException or InvalidOperationException)
        {
            // The position reached so far is all there is.
        }

        return position;
    }

    /// <summary>
    /// The position of the first alias whose anchor has not been defined by then.
    /// </summary>
    private static SourcePosition? FindUndefinedAlias(string yaml)
    {
        var defined = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var parser = new Parser(new StringReader(yaml));
            while (parser.MoveNext())
            {
                switch (parser.Current)
                {
                    case DocumentStart:
                        defined.Clear();
                        break;
                    case AnchorAlias alias when !defined.Contains(alias.Value.Value):
                        return SourcePosition.From(alias.Start);
                    case NodeEvent node when !node.Anchor.IsEmpty:
                        defined.Add(node.Anchor.Value);
                        break;
                }
            }
        }
        catch (YamlException)
        {
            // Fall back to the exception's own position.
        }

        return null;
    }
}
