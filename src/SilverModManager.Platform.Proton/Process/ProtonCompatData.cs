using NexusMods.Paths;

namespace SilverModManager.Platform.Proton;

/// <summary>
/// Proton wants the folder that holds <c>pfx</c> in <c>STEAM_COMPAT_DATA_PATH</c>, and rewrites <c>WINEPREFIX</c>
/// to <c>pfx</c> itself. Steam records the <c>pfx</c> folder; Heroic records the folder above it. Whoever compares
/// a process's environment with a recorded prefix has to accept both spellings.
/// </summary>
internal static class ProtonCompatData
{
    private const string PfxName = "pfx";

    /// <summary>The compatdata folder and its <c>pfx</c> folder for a prefix recorded as either of them.</summary>
    public static (string CompatData, string Prefix) Of(string recorded)
    {
        var trimmed = recorded.TrimEnd('/');
        return Path.GetFileName(trimmed) == PfxName
            ? (Path.GetDirectoryName(trimmed) ?? trimmed, trimmed)
            : (trimmed, trimmed + "/" + PfxName);
    }

    /// <summary>The folder to hand Proton as <c>STEAM_COMPAT_DATA_PATH</c> for a recorded prefix.</summary>
    public static AbsolutePath CompatDataOf(AbsolutePath prefix) => prefix.FileName == PfxName ? prefix.Parent : prefix;
}
