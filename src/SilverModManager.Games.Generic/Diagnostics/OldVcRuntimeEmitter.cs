using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.Generic.Diagnostics;

/// <summary>
/// Warns when the loadout deploys DLLs or executables that import the Visual C++ 2015-2022 runtime and the game's
/// prefix has an older one, or none. A mod DLL built against the current runtime can crash in its entry point on the
/// 14.0 runtime an old <c>vcrun2015</c> verb installs.
/// </summary>
/// <remarks>
/// Only files the loadout provides are read, and only their import tables, so the cost follows the size of the
/// loadout and not of the game folder. A runtime file whose version cannot be read, such as Wine's own placeholder,
/// is not reported: it says nothing about an installed Microsoft runtime. The separate components check covers a
/// game that declares the runtime as a requirement; this one follows what the loadout actually deploys.
/// </remarks>
public class OldVcRuntimeEmitter(IPrefixResolver prefixResolver, INativeImageReader imageReader, ILogger<OldVcRuntimeEmitter>? logger = null) : ASyncTreeEmitter
{
    /// <summary>The first runtime build that mods built with current toolchains are tested against.</summary>
    internal static readonly Version MinimumVersion = new(14, 40);

    private const string RuntimeLibrary = "msvcp140.dll";
    private const string FixComponent = "vcrun2022";
    private const int MaxListedFiles = 10;

    private static readonly string[] ImportedRuntimePrefixes = ["msvcp140", "vcruntime140"];
    private static readonly Extension[] ImageExtensions = [new(".dll"), new(".exe")];

    /// <inheritdoc />
    protected override async IAsyncEnumerable<Diagnostic> DiagnoseTree(Loadout.ReadOnly loadout, FrozenDictionary<GamePath, SyncNode> syncTree, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        var installation = loadout.InstallationInstance;
        var resolution = prefixResolver.Resolve(installation);
        if (!resolution.IsResolved) yield break;

        var importing = FilesImportingTheRuntime(installation, syncTree, cancellationToken);
        if (importing.Count == 0) yield break;

        var runtime = resolution.Prefix.Path / "drive_c/windows/system32" / RuntimeLibrary;
        string found;
        if (!runtime.FileExists)
        {
            found = "missing";
        }
        else
        {
            var version = ReadVersion(runtime);
            if (version is null)
            {
                logger?.LogTrace("`{File}` has no readable version, so the runtime is not judged old", runtime);
                yield break;
            }
            if (version >= MinimumVersion) yield break;
            found = $"version {version}";
        }

        logger?.LogTrace("{Count} deployed files import the Visual C++ runtime and the prefix's {File} is {Found}", importing.Count, RuntimeLibrary, found);
        yield return Diagnostics.CreateOldVisualCppRuntime(installation.Game.DisplayName, FormatFiles(importing), found, Instructions(installation, resolution.Prefix))
            with { Actions = [DiagnosticActions.SetUpPrefix([FixComponent])] };
    }

    private List<GamePath> FilesImportingTheRuntime(GameInstallation installation, FrozenDictionary<GamePath, SyncNode> syncTree, CancellationToken cancellationToken)
    {
        var found = new List<GamePath>();
        foreach (var (path, node) in syncTree.OrderBy(p => p.Key.ToString(), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A file of the game's own is not the loadout's doing, and one that is not on disk cannot be read.
            if (path.LocationId != LocationId.Game || !ImageExtensions.Contains(path.Extension)) continue;
            if (node.SourceItemType != LoadoutSourceItemType.Loadout || !node.HaveLoadout || !node.HaveDisk) continue;

            if (ImportsTheRuntime(installation.Locations.ToAbsolutePath(path))) found.Add(path);
        }
        return found;
    }

    private bool ImportsTheRuntime(AbsolutePath file)
    {
        try
        {
            return imageReader.ImportedLibraries(file).Any(IsRuntimeLibrary);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger?.LogTrace(e, "Could not read the imports of `{File}`", file);
            return false;
        }
    }

    private Version? ReadVersion(AbsolutePath file)
    {
        try
        {
            return imageReader.FileVersion(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger?.LogTrace(e, "Could not read the version of `{File}`", file);
            return null;
        }
    }

    private static bool IsRuntimeLibrary(string name)
        => ImportedRuntimePrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static string FormatFiles(List<GamePath> files)
    {
        var sb = new StringBuilder();
        foreach (var file in files.Take(MaxListedFiles))
            sb.Append("* `").Append(file.Path).AppendLine("`");
        if (files.Count > MaxListedFiles)
            sb.Append("* and ").Append(files.Count - MaxListedFiles).AppendLine(" more");
        return sb.ToString();
    }

    private static string Instructions(GameInstallation installation, ResolvedPrefix prefix)
    {
        if (prefix.Kind == CompatibilityLayerKind.SteamProton)
        {
            var appId = prefix.ProtonAppId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? installation.LocatorResult.StoreIdentifier;
            return $"Run [protontricks](https://github.com/Matoking/protontricks) with the game's Steam app id:\n\n```\nprotontricks {appId} -q {FixComponent}\n```";
        }

        return $"Run [winetricks](https://github.com/Winetricks/winetricks) against the prefix:\n\n```\nWINEPREFIX=\"{prefix.Path.GetFullPath()}\" winetricks -q {FixComponent}\n```";
    }
}
