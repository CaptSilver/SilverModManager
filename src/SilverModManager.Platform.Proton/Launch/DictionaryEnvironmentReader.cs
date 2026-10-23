using SilverModManager.Sdk;

namespace SilverModManager.Platform.Proton.Launch;

/// <summary>
/// Reads variables from a captured environment, such as the one a <c>run</c> process handed to the window.
/// </summary>
public sealed class DictionaryEnvironmentReader(IReadOnlyDictionary<string, string> values) : IEnvironmentReader
{
    public string? Get(string name) => values.GetValueOrDefault(name);
}
