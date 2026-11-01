using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Loadouts;

/// <summary>What a tool run left behind, beyond the loadout it returns.</summary>
/// <param name="Loadout">The loadout after the run's last sync.</param>
/// <param name="OutputGroup">The group the tool's output was kept in, or null for a tool that keeps none.</param>
/// <param name="ExitCode">The program's exit code, when the tool reports one.</param>
/// <param name="CapturedFiles">How many files went into <paramref name="OutputGroup"/>.</param>
public sealed record ToolRunOutcome(Loadout.ReadOnly Loadout, string? OutputGroup, int? ExitCode, int CapturedFiles);
