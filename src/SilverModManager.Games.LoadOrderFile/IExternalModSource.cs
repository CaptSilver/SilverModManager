using NexusMods.Paths;
using SilverModManager.Games.LoadOrderFile.Manifests;
using SilverModManager.Games.LoadOrderFile.Naming;

namespace SilverModManager.Games.LoadOrderFile;

/// <summary>
/// Mods a game loads from a folder the app does not install into, such as a store's workshop or a mod folder the
/// player filled by hand. <see cref="Listing"/> says when the order file lists them.
/// </summary>
public interface IExternalModSource
{
    /// <summary>When the mods this source finds are listed.</summary>
    ExternalListing Listing { get; }

    /// <summary>The names the order file lists the mods under, in a fixed order; empty when the game folder has no such source.</summary>
    IReadOnlyList<string> Find(AbsolutePath gameFolder);

    /// <summary>
    /// The mods <see cref="Find"/> lists, with the manifest each carries, for the diagnostics that read a mod's order
    /// rules. A source whose mods carry no manifest has nothing to report.
    /// </summary>
    IReadOnlyList<ExternalMod> FindMods(AbsolutePath gameFolder) => [];
}

/// <summary>A mod an <see cref="IExternalModSource"/> found: the folder it sits in and what its manifest says.</summary>
public sealed record ExternalMod(string FolderName, ModManifest Manifest)
{
    /// <summary>The mods among <paramref name="folders"/> that <paramref name="namer"/> reads a manifest from; none when the namer reads no manifests.</summary>
    internal static IReadOnlyList<ExternalMod> Read(IExternalModNamer namer, IEnumerable<AbsolutePath> folders)
        => namer is not ManifestNamer manifests
            ? []
            : folders.Select(folder => manifests.Manifest(folder) is { } manifest ? new ExternalMod(folder.FileName, manifest) : null).OfType<ExternalMod>().ToList();
}
