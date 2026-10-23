namespace SilverModManager.Games.Bethesda.Loot.Metadata;

/// <summary>
/// Thrown when a regex plugin name does not compile.
/// </summary>
public sealed class InvalidPluginNameException(string name, Exception inner)
    : ArgumentException($"\"{name}\" is not a valid regular expression: {inner.Message}", nameof(name), inner);
