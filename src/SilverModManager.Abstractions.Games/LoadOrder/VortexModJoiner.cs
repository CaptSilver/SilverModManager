using SilverModManager.Abstractions.Collections.Json;

namespace SilverModManager.Abstractions.Games.LoadOrder;

/// <summary>Finds the <c>mods[]</c> entry a Vortex install id belongs to.</summary>
public sealed class VortexModJoiner(IReadOnlyList<Mod> mods)
{
    /// <summary>
    /// By the mod's logical file name, else its file expression (with or without the extension), else its name when exactly
    /// one mod has it, else the Nexus mod id the install id carries. Returns the index, or null with the reason it names
    /// no mod or several.
    /// </summary>
    public (int? Index, string? Reason) Find(string installId)
    {
        for (var i = 0; i < mods.Count; i++)
        {
            if (Same(mods[i].Source.LogicalFilename, installId)) return (i, null);
        }

        for (var i = 0; i < mods.Count; i++)
        {
            var expression = mods[i].Source.FileExpression.Path;
            if (!string.IsNullOrEmpty(expression) && (Same(expression, installId) || Same(Path.GetFileNameWithoutExtension(expression), installId))) return (i, null);
        }

        // A display name can be shared by two mods; it only counts when it names one.
        var named = Enumerable.Range(0, mods.Count).Where(i => Same(mods[i].Name, installId)).ToArray();
        if (named.Length == 1) return (named[0], null);

        if (!VortexInstallId.TryParse(installId, out var parsed)) return (null, $"no mod in the collection matches '{installId}'");

        var candidates = Enumerable.Range(0, mods.Count)
            .Where(i => mods[i].Source.Type == ModSourceType.NexusMods && mods[i].Source.ModId.Value == parsed.NexusModId)
            .ToArray();
        if (candidates.Length == 0) return (null, $"no mod in the collection has Nexus mod id {parsed.NexusModId} (from '{installId}')");
        if (candidates.Length == 1) return (candidates[0], null);

        // Two files of one Nexus page. Only the spaced form carries a version; the archive form stays a refusal.
        if (parsed.Version is not null)
        {
            var byVersion = candidates.Where(i => Same(mods[i].Version, parsed.Version)).ToArray();
            if (byVersion.Length == 1) return (byVersion[0], null);
            if (byVersion.Length > 1) candidates = byVersion;

            var byName = candidates.Where(i => Same(mods[i].Name, parsed.Name) || Same(mods[i].Source.LogicalFilename, parsed.Name)
                || Same(Path.GetFileNameWithoutExtension(mods[i].Source.FileExpression.Path), parsed.Name)).ToArray();
            if (byName.Length == 1) return (byName[0], null);
        }

        return (null, $"several mods in the collection have Nexus mod id {parsed.NexusModId} and none is the one '{installId}' names");
    }

    private static bool Same(string? left, string right) => left is not null && left.Equals(right, StringComparison.OrdinalIgnoreCase);
}
