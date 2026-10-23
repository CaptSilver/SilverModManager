using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Games.Unreal;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.CreationEngine.Installers;

/// <summary>
/// Installs mods for Oblivion Remastered, which has three targets: the old Bethesda Data folder buried in the
/// Unreal project, the Unreal pak folders, and the binaries folder beside the script extender. Mixed archives
/// (a pak and a plugin) are common, so each file is routed on its own instead of the archive being given one root.
/// </summary>
/// <remarks>
/// A single pattern installer can't do this: the generic pak installer takes the subtree under the first pak it
/// finds, and the stop-pattern installer has one data root. Routing order per file: a path rooted at the
/// project folder is kept as laid out; the Unreal layout takes what it recognises (paks, LogicMods, UE4SS);
/// the script extender goes to the binaries folder; everything else is a Data file.
/// </remarks>
public class OblivionRemasteredInstaller(IServiceProvider serviceProvider)
    : ALibraryArchiveInstaller(serviceProvider, serviceProvider.GetRequiredService<ILogger<OblivionRemasteredInstaller>>())
{
    /// <summary>The Unreal project folder under the game folder; a path rooted here is already game-relative.</summary>
    public required string ProjectFolder { get; init; }
    public required UnrealModLayout Layout { get; init; }
    public required GamePath DataPath { get; init; }
    public required string DataFolderName { get; init; }
    public required GamePath EnginePath { get; init; }
    public required string[] GameAliases { get; init; }
    public required string[] TopLevelDirs { get; init; }
    public required string[] EngineFiles { get; init; }
    public required Extension[] PluginLikeExtensions { get; init; }
    public required Extension[] ArchiveLikeExtensions { get; init; }

    private const string ScriptExtenderFolder = "OBSE";

    private Regex? _engineFiles;

    // Whole-path match: an engine file only counts at the root of the (stepped-into) archive.
    private Regex EngineFilesRegex => _engineFiles ??= new Regex(
        $"^(?:{string.Join("|", EngineFiles.Select(p => $"(?:{p})"))})$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>The target of each file, in the order given.</summary>
    public IReadOnlyList<GamePath> Route(IReadOnlyList<RelativePath> files)
    {
        var root = EffectiveRoot(files);
        return files.Select(f => Route(f, root)).ToArray();
    }

    private GamePath Route(RelativePath path, RelativePath root)
    {
        if (path.TopParent.ToString().Equals(ProjectFolder, StringComparison.OrdinalIgnoreCase))
            return new GamePath(LocationId.Game, path);

        if (Layout.TryRoute(path, out var unreal))
            return unreal;

        var rel = path.InFolder(root) && root != RelativePath.Empty ? path.RelativeTo(root) : path;
        if (IsIn(rel, DataFolderName))
            rel = rel.RelativeTo(RelativePath.FromUnsanitizedInput(DataFolderName));

        if (IsIn(rel, ScriptExtenderFolder) || EngineFilesRegex.IsMatch(rel.ToString()))
            return EnginePath / rel;

        return DataPath / rel;
    }

    private static bool IsIn(RelativePath path, string folder) =>
        path.Parts.Count() > 1 && path.TopParent.ToString().Equals(folder, StringComparison.OrdinalIgnoreCase);

    // The folder to step into: only when every file sits under one top folder, and that folder is the data
    // folder, a name the game goes by, or a wrapper around something Bethesda-shaped.
    private RelativePath EffectiveRoot(IReadOnlyList<RelativePath> files)
    {
        if (files.Any(f => f.Parts.Count() < 2)) return RelativePath.Empty;

        var tops = files.Select(f => f.TopParent).Distinct().ToArray();
        if (tops.Length != 1) return RelativePath.Empty;

        var top = tops[0].ToString();
        if (top.Equals(ProjectFolder, StringComparison.OrdinalIgnoreCase)) return RelativePath.Empty;
        if (top.Equals(DataFolderName, StringComparison.OrdinalIgnoreCase)) return tops[0];
        if (GameAliases.Contains(top, StringComparer.OrdinalIgnoreCase)) return tops[0];
        if (TopLevelDirs.Contains(top, StringComparer.OrdinalIgnoreCase)) return RelativePath.Empty;

        return files.Any(f => LooksLikeBethesdaContent(f.RelativeTo(tops[0]))) ? tops[0] : RelativePath.Empty;
    }

    private bool LooksLikeBethesdaContent(RelativePath below)
    {
        var top = below.TopParent.ToString();
        if (below.Parts.Count() > 1 && (
                top.Equals(DataFolderName, StringComparison.OrdinalIgnoreCase)
                || top.Equals(ScriptExtenderFolder, StringComparison.OrdinalIgnoreCase)
                || TopLevelDirs.Contains(top, StringComparer.OrdinalIgnoreCase)))
            return true;

        return PluginLikeExtensions.Contains(below.Extension)
               || ArchiveLikeExtensions.Contains(below.Extension)
               || EngineFilesRegex.IsMatch(below.ToString());
    }

    public override ValueTask<InstallerResult> ExecuteAsync(LibraryArchive.ReadOnly libraryArchive, LoadoutItemGroup.New loadoutGroup, ITransaction transaction, Loadout.ReadOnly loadout, CancellationToken cancellationToken)
    {
        var entries = libraryArchive.Children.ToArray();
        if (entries.Length == 0)
            return ValueTask.FromResult(FailWithReason("The provided library archive has no files"));

        var targets = Route(entries.Select(e => e.Path).ToArray());

        for (var i = 0; i < entries.Length; i++)
        {
            var file = entries[i];
            var target = targets[i];
            var libFile = file.AsLibraryFile();
            _ = new LoadoutFile.New(transaction, out var id)
            {
                LoadoutItemWithTargetPath = new LoadoutItemWithTargetPath.New(transaction, id)
                {
                    TargetPath = (loadout.Id, target.LocationId, target.Path),
                    LoadoutItem = new LoadoutItem.New(transaction, id)
                    {
                        Name = file.Path.FileName,
                        LoadoutId = loadout.Id,
                        ParentId = loadoutGroup.Id,
                    },
                },
                Hash = libFile.Hash,
                Size = libFile.Size,
            };
        }

        return ValueTask.FromResult<InstallerResult>(new Success());
    }
}
