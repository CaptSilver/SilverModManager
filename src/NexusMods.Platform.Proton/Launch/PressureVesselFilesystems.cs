namespace NexusMods.Platform.Proton.Launch;

/// <summary>
/// Adds host folders to the list the Steam runtime container mounts read-write.
/// </summary>
internal static class PressureVesselFilesystems
{
    internal const string Variable = "PRESSURE_VESSEL_FILESYSTEMS_RW";

    /// <summary>
    /// Appends <paramref name="roots"/> to the variable in <paramref name="environment"/>. Entries already there
    /// stay first because the container reads the list in order and the user may have set it; a root is only
    /// added once, so merging again changes nothing. With no roots the variable is left exactly as it was.
    /// </summary>
    internal static void Merge(Dictionary<string, string> environment, IReadOnlyList<string> roots)
    {
        if (roots.Count == 0) return;

        var merged = environment.TryGetValue(Variable, out var existing)
            ? existing.Split(':', StringSplitOptions.RemoveEmptyEntries).ToList()
            : [];
        foreach (var root in roots)
        {
            if (!merged.Contains(root)) merged.Add(root);
        }
        environment[Variable] = string.Join(':', merged);
    }
}
