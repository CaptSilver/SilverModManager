using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.FileSystemGlobbing;
using SilverModManager.Sdk.ProxyConsole;

namespace SilverModManager.CLI.OptionParsers;

internal class MatcherParser : IOptionParser<Matcher>
{
    /// <inheritdoc />
    public bool TryParse(string toParse, [MaybeNullWhen(false)] out Matcher value, out string error)
    {
        try
        {
            var matcher = new Matcher();
            matcher.AddInclude(toParse);
            value = matcher;
            error = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            value = default;
            error = exception.Message;
            return false;
        }
    }
}
