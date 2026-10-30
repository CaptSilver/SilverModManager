using System.Net;
using System.Text.RegularExpressions;

namespace SilverModManager.Wabbajack.Acquire.Hosts;

/// <summary>
/// Reads the attributes of the tags on a page. The host jobs need one link or one form from a landing page, which a
/// pattern over the tags finds as well as a full HTML parser would, without a dependency.
/// </summary>
public static partial class HtmlTags
{
    /// <summary>Every <paramref name="tagName"/> tag in document order, with attribute names in lower case and values unescaped.</summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, string>> Find(string html, string tagName)
    {
        var tags = new List<IReadOnlyDictionary<string, string>>();
        foreach (Match tag in Regex.Matches(html, $@"<{Regex.Escape(tagName)}(?=[\s/>])([^>]*)>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant))
        {
            var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match attribute in AttributePattern().Matches(tag.Groups[1].Value))
            {
                var value = attribute.Groups[2].Success ? attribute.Groups[2].Value : attribute.Groups[3].Success ? attribute.Groups[3].Value : attribute.Groups[4].Value;
                attributes.TryAdd(attribute.Groups[1].Value, WebUtility.HtmlDecode(value));
            }
            tags.Add(attributes);
        }
        return tags;
    }

    [GeneratedRegex("""([^\s"'<>/=]+)(?:\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s"'>]+)))?""", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex AttributePattern();
}
