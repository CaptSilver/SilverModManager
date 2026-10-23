namespace NexusMods.Abstractions.Loadouts.Synchronizers;

/// <summary>
/// What a recognizer found in one pass.
/// </summary>
/// <param name="Items">The content found.</param>
/// <param name="RecordsReadable">False when a record file exists but could not be read. The caller then keeps
/// what it already has instead of treating the content as removed.</param>
public sealed record GameInstalledRecognition(IReadOnlyList<GameInstalledContent> Items, bool RecordsReadable);
