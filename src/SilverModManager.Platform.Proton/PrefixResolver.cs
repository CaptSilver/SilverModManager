using System.Runtime.InteropServices;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Platform.Proton;

/// <summary>
/// Picks the prefix from what the locator recorded. Steam's own prefix is taken from the installation
/// itself; any other layer comes from <see cref="GameLocatorResult.Compatibility"/>. If both exist and they
/// are different directories the installation is ambiguous and nothing is chosen.
/// </summary>
public sealed class PrefixResolver : IPrefixResolver
{
    public PrefixResolution Resolve(GameInstallation installation)
    {
        var result = installation.LocatorResult;
        var gameName = installation.Game.DisplayName;

        var steam = SteamCandidate(result);
        var other = result.Compatibility is { Kind: not CompatibilityLayerKind.SteamProton } layer ? layer : null;

        if (steam is not null && other is not null && steam.Prefix != other.Prefix)
        {
            return PrefixResolution.Failed(
                $"{gameName} has more than one Wine prefix: '{steam.Prefix}' ({steam.Kind}) and '{other.Prefix}' ({other.Kind}). " +
                "Not choosing between them; remove the one the game does not use.");
        }

        var chosen = steam ?? other;
        if (chosen is null)
        {
            if (result.Platform != OSPlatform.Windows)
                return PrefixResolution.Native($"{gameName} is a native Linux build and has no Wine prefix.");

            return result.Store == GameStore.ManuallyAdded
                ? PrefixResolution.Failed($"{gameName} was added by path as a Windows build, but no Wine prefix is recorded for it. Add a 'prefix' to its entry in installs.yaml.")
                : PrefixResolution.Failed($"{gameName} runs as a Windows build but no Wine prefix was found for it.");
        }

        if (!ProtonPrefix.IsReady(chosen.Prefix))
        {
            return PrefixResolution.Failed(chosen.Kind == CompatibilityLayerKind.SteamProton
                ? ProtonMessages.MissingPrefix(gameName, chosen.Prefix)
                : $"{gameName} has no Wine prefix at '{chosen.Prefix}' yet. Please launch the game once from its launcher so the prefix is created, then try again.");
        }

        return PrefixResolution.Resolved(new ResolvedPrefix(
            chosen.Prefix,
            chosen.Kind,
            WineBinary: chosen.WineBinary,
            ProtonAppId: chosen.Kind == CompatibilityLayerKind.SteamProton ? SteamAppId(result) : null));
    }

    /// <summary>
    /// The Steam prefix, from the compatibility data provider when the prefix exists, else from the prefix
    /// path the Steam locator recorded for a game that was never launched.
    /// </summary>
    private static CompatibilityLayer? SteamCandidate(GameLocatorResult result)
    {
        if (result.LinuxCompatabilityDataProvider is { } provider)
            return new CompatibilityLayer(CompatibilityLayerKind.SteamProton, provider.WinePrefixDirectoryPath);

        if (result.Compatibility is { Kind: CompatibilityLayerKind.SteamProton } recorded) return recorded;

        return result.Store == GameStore.Steam && result.WinePrefixPath is { } prefix
            ? new CompatibilityLayer(CompatibilityLayerKind.SteamProton, prefix)
            : null;
    }

    private static uint? SteamAppId(GameLocatorResult result)
        => uint.TryParse(result.StoreIdentifier, out var appId) ? appId : null;
}
