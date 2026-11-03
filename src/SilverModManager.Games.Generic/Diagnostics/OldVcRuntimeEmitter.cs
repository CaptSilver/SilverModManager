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
/// loadout and not of the game folder. A 32-bit file loads the runtime from the prefix's <c>syswow64</c> folder and a
/// 64-bit one from <c>system32</c>, so each is judged by its own copy; a prefix without <c>syswow64</c> is a 32-bit
/// prefix and keeps its 32-bit runtime in <c>system32</c>. A runtime file whose version cannot be read, such as Wine's
/// own placeholder, is not reported: it says nothing about an installed Microsoft runtime. The separate components
/// check covers a game that declares the runtime as a requirement; this one follows what the loadout actually deploys.
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

    /// <summary>A file that imports the runtime, and which runtime it loads.</summary>
    private readonly record struct Importer(GamePath Path, bool Is64Bit);

    /// <summary>The importers of one bitness whose runtime in the prefix is old or missing.</summary>
    private sealed record OldRuntime(bool Is64Bit, List<Importer> Importers, AbsolutePath Runtime, string Found);

    /// <inheritdoc />
    protected override async IAsyncEnumerable<Diagnostic> DiagnoseTree(Loadout.ReadOnly loadout, FrozenDictionary<GamePath, SyncNode> syncTree, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        var installation = loadout.InstallationInstance;
        var resolution = prefixResolver.Resolve(installation);
        if (!resolution.IsResolved) yield break;

        var importing = FilesImportingTheRuntime(loadout, installation, syncTree, cancellationToken);
        if (importing.Count == 0) yield break;

        var old = new List<OldRuntime>();
        foreach (var is64Bit in new[] { false, true })
        {
            var importers = importing.Where(i => i.Is64Bit == is64Bit).ToList();
            if (importers.Count == 0) continue;

            var runtime = RuntimeFor(resolution.Prefix, is64Bit);
            var found = FindProblem(runtime);
            if (found is not null) old.Add(new OldRuntime(is64Bit, importers, runtime, found));
        }
        if (old.Count == 0) yield break;

        var files = old.SelectMany(o => o.Importers).Select(i => i.Path).ToList();
        logger?.LogTrace("{Count} deployed files import the Visual C++ runtime and the prefix's runtime is old or missing for them", files.Count);
        yield return Diagnostics.CreateOldVisualCppRuntime(installation.Game.DisplayName, FormatFiles(files), FormatRuntimes(resolution.Prefix, old), Instructions(installation, resolution.Prefix))
            with { Actions = [DiagnosticActions.SetUpPrefix([FixComponent])] };
    }

    /// <summary>
    /// The runtime a process of the given bitness loads. A prefix has <c>syswow64</c> only when it is 64-bit.
    /// </summary>
    private static AbsolutePath RuntimeFor(ResolvedPrefix prefix, bool is64Bit)
    {
        var windows = prefix.Path / "drive_c/windows";
        var folder = !is64Bit && (windows / "syswow64").DirectoryExists() ? "syswow64" : "system32";
        return windows / folder / RuntimeLibrary;
    }

    /// <summary>
    /// What is wrong with the runtime file, as it reads after "is", or null when it is recent enough or cannot be judged.
    /// </summary>
    private string? FindProblem(AbsolutePath runtime)
    {
        if (!runtime.FileExists) return "missing";

        var version = ReadVersion(runtime);
        if (version is null)
        {
            logger?.LogTrace("`{File}` has no readable version, so the runtime is not judged old", runtime);
            return null;
        }
        return version >= MinimumVersion ? null : $"version {version}";
    }

    private List<Importer> FilesImportingTheRuntime(Loadout.ReadOnly loadout, GameInstallation installation, FrozenDictionary<GamePath, SyncNode> syncTree, CancellationToken cancellationToken)
    {
        var provided = ModProvidedFiles.For(loadout);
        var found = new List<Importer>();
        foreach (var (path, node) in syncTree.OrderBy(p => p.Key.ToString(), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A file of the game's own is not the loadout's doing, and one that is not on disk cannot be read.
            // A file adopted into the Overrides group is a leftover, not something a mod deploys.
            if (path.LocationId != LocationId.Game || !ImageExtensions.Contains(path.Extension)) continue;
            if (!node.HaveDisk || !provided.IsProvidedByMod(node)) continue;

            if (ReadImports(installation.Locations.ToAbsolutePath(path)) is { } imports && imports.Libraries.Any(IsRuntimeLibrary))
                found.Add(new Importer(path, imports.Is64Bit));
        }
        return found;
    }

    private NativeImageImports? ReadImports(AbsolutePath file)
    {
        try
        {
            return imageReader.ReadImports(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger?.LogTrace(e, "Could not read the imports of `{File}`", file);
            return null;
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

    private static string FormatRuntimes(ResolvedPrefix prefix, List<OldRuntime> old)
    {
        var sb = new StringBuilder();
        foreach (var o in old)
        {
            var bits = o.Is64Bit ? "64-bit" : "32-bit";
            sb.Append("* `").Append(o.Runtime.RelativeTo(prefix.Path)).Append("`, for ").Append(bits).Append(" files, is ").AppendLine(o.Found);
        }
        return sb.ToString();
    }

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
