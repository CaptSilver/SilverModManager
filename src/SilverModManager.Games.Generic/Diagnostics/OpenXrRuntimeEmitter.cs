using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.Generic.Diagnostics;

/// <summary>
/// Warns when a VR game's loadout replaces <c>openvr_api.dll</c> with OpenComposite and no OpenXR runtime is
/// registered, which is when <c>active_runtime.json</c> is missing or names a library that is not there.
/// </summary>
/// <remarks>
/// OpenComposite is recognised by a mod providing <c>openvr_api.dll</c> in the game folder next to an
/// <c>opencomposite.ini</c>. A mod that ships the real OpenVR library has no ini, so it is left alone.
/// </remarks>
public class OpenXrRuntimeEmitter(IOpenXrConfig config, ILogger<OpenXrRuntimeEmitter>? logger = null) : ASyncTreeEmitter
{
    private static readonly GamePath OpenVrLibrary = new(LocationId.Game, "openvr_api.dll");
    private static readonly GamePath OpenCompositeIni = new(LocationId.Game, "opencomposite.ini");

    /// <inheritdoc />
    protected override async IAsyncEnumerable<Diagnostic> DiagnoseTree(Loadout.ReadOnly loadout, FrozenDictionary<GamePath, SyncNode> syncTree, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        var game = loadout.InstallationInstance.Game;
        if (game is not IVirtualRealityGame) yield break;
        if (!ProvidesOpenComposite(loadout, syncTree)) yield break;

        var manifest = config.ActiveRuntimeFile;
        var problem = FindProblem(manifest);
        if (problem is null) yield break;

        logger?.LogTrace("OpenComposite is deployed and the OpenXR runtime {Manifest} {Problem}", manifest, problem);
        yield return Diagnostics.CreateOpenXrRuntimeNotRegistered(game.DisplayName, manifest.GetFullPath(), problem);
    }

    private static bool ProvidesOpenComposite(Loadout.ReadOnly loadout, FrozenDictionary<GamePath, SyncNode> syncTree)
    {
        if (!syncTree.TryGetValue(OpenVrLibrary, out var library) || !ModProvidedFiles.For(loadout).IsProvidedByMod(library)) return false;
        return syncTree.TryGetValue(OpenCompositeIni, out var ini) && (ini.HaveLoadout || ini.HaveDisk);
    }

    /// <summary>
    /// What is wrong with the runtime registration, phrased to follow "and ...", or null when it is fine.
    /// </summary>
    private string? FindProblem(AbsolutePath manifest)
    {
        if (!manifest.FileExists) return "it does not exist";

        string? library;
        try
        {
            using var document = JsonDocument.Parse(manifest.Read());
            library = document.RootElement.TryGetProperty("runtime", out var runtime) && runtime.TryGetProperty("library_path", out var path)
                ? path.GetString()
                : null;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            // A link whose target is gone passes the existence check as a link, and cannot be opened.
            return "it does not exist";
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            logger?.LogTrace(e, "Could not read `{Manifest}`", manifest);
            return "it is not a readable runtime manifest";
        }

        if (string.IsNullOrWhiteSpace(library)) return "it does not name a runtime library";

        // A bare file name is looked up by the system's library search, which this check cannot repeat.
        if (library.IndexOf('/') < 0) return null;

        // A relative path is relative to the manifest wherever a link to it leads.
        var resolvedManifest = ResolvedPath.Of(manifest.GetFullPath());
        var target = Path.IsPathRooted(library) ? library : Path.Combine(Path.GetDirectoryName(resolvedManifest) ?? "", library);
        return manifest.FileSystem.FromUnsanitizedFullPath(target).FileExists ? null : $"it points to `{library}`, which does not exist";
    }
}
