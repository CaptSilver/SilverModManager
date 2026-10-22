using NexusMods.Paths;

namespace NexusMods.Games.CreationEngine.Loot;

/// <summary>
/// The folder that holds the <c>loot</c> directory: downloaded masterlists and the user's userlists. Registered once
/// so the masterlist store and the sort read the same place, and so tests can point both at a temp folder.
/// </summary>
internal sealed record LootDataDirectory(AbsolutePath Path);
