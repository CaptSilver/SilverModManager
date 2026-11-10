using SilverModManager.Abstractions.Diagnostics.References;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.CreationEngine.CrashLogs;

/// <summary>A mod that supplies a file, as a reference the diagnostic can render and the name the report text uses.</summary>
public sealed record ModOwner(LoadoutItemGroupReference Reference, string Name);

/// <summary>A game path a crash log mentions and who supplies it in the loadout being checked.</summary>
/// <param name="Owner">The enabled mod whose file this is; null means no loose file in this loadout supplies it.</param>
/// <param name="IsGameFile">Known to GameInstalledPaths.</param>
/// <param name="ViaPlugin">For an asset with no Owner: the owner of the plugin (File:) of the object that referenced it.</param>
public sealed record OwnedPath(GamePath Path, ModOwner? Owner, bool IsGameFile, ModOwner? ViaPlugin = null);

/// <summary>Who supplies the files one crash report touched, split by what kind of file they are.</summary>
public sealed record CrashOwnership(
    IReadOnlyList<OwnedPath> Assets,
    IReadOnlyList<OwnedPath> Plugins,
    IReadOnlyList<OwnedPath> SkseDlls,
    IReadOnlyList<string> ModifiedByChains)
{
    public static readonly CrashOwnership Empty = new([], [], [], []);

    /// <summary>The mod that supplies <paramref name="path"/>, looked up across assets, plugins and DLLs; null when none does.</summary>
    public ModOwner? OwnerOf(GamePath path) =>
        Assets.Concat(Plugins).Concat(SkseDlls).FirstOrDefault(o => o.Path == path)?.Owner;
}
