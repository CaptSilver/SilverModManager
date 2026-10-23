namespace NexusMods.Games.CreationEngine;

/// <summary>
/// Thrown when a plugin list on disk isn't valid text in the encoding its game reads it in. Rewriting it with
/// substitute characters would corrupt the lines the app doesn't own, so nothing is written.
/// </summary>
public sealed class UndecodablePluginListException(string fileName, string encodingName) : Exception(
    $"{fileName} was not read because it is not valid {encodingName} text. " +
    "Fix or remove the lines with characters that aren't, then apply the loadout again.")
{
    public string FileName { get; } = fileName;
}
