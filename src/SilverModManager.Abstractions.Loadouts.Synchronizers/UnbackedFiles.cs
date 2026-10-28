using NexusMods.Paths;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers;

/// <summary>
/// Loadout files the file store holds no copy of, counted once per distinct content.
/// </summary>
public readonly record struct UnbackedFiles(int Count, Size Total);
