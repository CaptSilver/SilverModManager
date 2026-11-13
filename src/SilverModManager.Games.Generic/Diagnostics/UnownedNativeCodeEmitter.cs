using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Diagnostics.Values;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using NexusMods.Paths;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.Generic.Diagnostics;

/// <summary>
/// Reports native code the game loads that no enabled mod provides: a loader's plugin folder, the loader's own runtime
/// files, and DLLs sitting beside the game's executables. The first is a warning because the game loads every library
/// there, the second a suggestion because the loader needs those files on purpose, and a game-folder DLL is a warning
/// only when an executable of the game names it (or a mod loader's proxy name makes it load) and Wine does not shadow
/// it; otherwise it is a suggestion.
/// </summary>
/// <remarks>
/// A file in the loadout's Overrides group counts as not provided: the group holds what was found in the game
/// folder, not what the user installed through a mod. Files the game or the store put there, and files an earlier
/// apply deployed that the next apply removes, are not reported. Registered once for every game, so a game needs no
/// emitter list entry; a game that names no loader and has no executable-folder DLLs yields nothing.
/// </remarks>
public sealed class UnownedNativeCodeEmitter(GameRootLibraries rootLibraries, IFileHashesService fileHashes, ILogger<UnownedNativeCodeEmitter>? logger = null) : ASyncTreeEmitter
{
    private const string LoaderName = "mod loader";

    private static readonly DiagnosticId UnownedId = new(Diagnostics.Source, number: 11);
    private static readonly DiagnosticId LoaderFilesId = new(Diagnostics.Source, number: 12);
    private static readonly DiagnosticId UnloadedId = new(Diagnostics.Source, number: 13);

    /// <inheritdoc />
    protected override async IAsyncEnumerable<Diagnostic> DiagnoseTree(Loadout.ReadOnly loadout, FrozenDictionary<GamePath, SyncNode> syncTree, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        var installation = loadout.InstallationInstance;
        var game = installation.Game;
        var nativeCode = game as IGameNativeCode;
        var executableFolders = game.ExecutableFolders(installation);

        var provided = ModProvidedFiles.For(loadout);
        var gameInstalled = GameInstalledPaths.For(loadout.Db, loadout.LoadoutId);
        var prefix = installation.LocatorResult.LinuxCompatabilityDataProvider;
        var ignored = new Dictionary<DiagnosticId, IReadOnlyList<DiagnosticDismissals.Dismissed>>
        {
            [UnownedId] = DiagnosticDismissals.For(loadout.Db, loadout.LoadoutId, UnownedId),
            [LoaderFilesId] = DiagnosticDismissals.For(loadout.Db, loadout.LoadoutId, LoaderFilesId),
            [UnloadedId] = DiagnosticDismissals.For(loadout.Db, loadout.LoadoutId, UnloadedId),
        };
        var storeFiles = new Lazy<IReadOnlySet<GamePath>>(() => fileHashes.GetGameFiles(installation).Select(f => f.Path).ToHashSet());

        var candidates = syncTree
            .Where(pair => pair.Key.LocationId == LocationId.Game)
            .Where(pair => nativeCode?.RoleOf(pair.Key) is not null || IsRootLibrary(pair.Key, executableFolders))
            .Where(pair => IsUnmanaged(pair.Key, pair.Value, provided, gameInstalled))
            .OrderBy(pair => pair.Key.ToString(), StringComparer.Ordinal)
            .ToArray();

        var runtimeFiles = new List<(GamePath Path, SyncNode Node)>();
        foreach (var (path, node) in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = path.Path.ToString();

            switch (nativeCode?.RoleOf(path))
            {
                case NativeCodeRole.LoaderPlugin:
                    if (IsIgnored(ignored[UnownedId], path, node)) continue;
                    yield return WithActions(Diagnostics.CreateUnownedNativeCode(game.DisplayName, file, "plugin folder", PluginDetails()), loadout, path, node, steamAppId: null);
                    continue;
                case NativeCodeRole.LoaderRuntime:
                    if (!IsIgnored(ignored[LoaderFilesId], path, node)) runtimeFiles.Add((path, node));
                    continue;
            }

            var verdict = await rootLibraries.HowLoaded(installation.Locations.ToAbsolutePath(path.Parent), path.FileName, prefix, cancellationToken);
            if (verdict.Load == RootLoad.Loaded)
            {
                if (IsIgnored(ignored[UnownedId], path, node)) continue;
                var onStore = storeFiles.Value.Contains(path);
                yield return WithActions(Diagnostics.CreateUnownedNativeCode(game.DisplayName, file, "game folder", GameFolderDetails(game.DisplayName, onStore)),
                    loadout, path, node, onStore ? SteamAppId(installation) : null);
            }
            else
            {
                if (IsIgnored(ignored[UnloadedId], path, node)) continue;
                yield return WithActions(Diagnostics.CreateUnloadedGameRootLibrary(game.DisplayName, file, UnloadedReason(file, verdict)), loadout, path, node, steamAppId: null);
            }
        }

        if (runtimeFiles.Count > 0)
        {
            logger?.LogTrace("{Count} loader files in {Game} have no mod that provides them", runtimeFiles.Count, game.DisplayName);
            var list = Diagnostics.CreateUnownedLoaderFiles(game.DisplayName, LoaderName, ListOf(runtimeFiles.Select(f => f.Path)));
            yield return list with { Actions = [.. ListActionsFor(loadout, runtimeFiles), IgnoreAction(list, runtimeFiles)] };
        }
    }

    // A path is ignored when this loadout dismissed it and the file still has the bytes it had then.
    private static bool IsIgnored(IReadOnlyList<DiagnosticDismissals.Dismissed> dismissed, GamePath path, SyncNode node)
        => DiagnosticDismissals.Covers(dismissed, DiagnosticDismissals.SubjectOf(path), node.Disk.Identity);

    private static DiagnosticAction IgnoreAction(Diagnostic diagnostic, IEnumerable<(GamePath Path, SyncNode Node)> files)
        => DiagnosticActions.DismissDiagnosticSubjects(diagnostic.Id, diagnostic.Title,
            [.. files.Select(f => (DiagnosticDismissals.SubjectOf(f.Path), (string?)DiagnosticDismissals.IdentityText(f.Node.Disk.Identity)))]);

    private static Diagnostic WithActions(Diagnostic diagnostic, Loadout.ReadOnly loadout, GamePath path, SyncNode node, string? steamAppId)
        => diagnostic with { Actions = ActionsFor(loadout, diagnostic, path, node, steamAppId) };

    // The one place that decides which buttons a finding gets, so a new button is added once. A file with no loadout
    // row (dropped in after the last apply) has nothing to remove yet, but can still be ignored.
    private static ImmutableArray<DiagnosticAction> ActionsFor(Loadout.ReadOnly loadout, Diagnostic diagnostic, GamePath path, SyncNode node, string? steamAppId)
    {
        var actions = new List<DiagnosticAction>();
        if (OwningGroup.Of(loadout.Db, node) is { } group)
            actions.Add(DiagnosticActions.RemoveLoadoutFile(path, group));
        if (steamAppId is not null)
            actions.Add(DiagnosticActions.VerifyGameFilesInSteam(steamAppId));
        actions.Add(IgnoreAction(diagnostic, [(path, node)]));
        return [.. actions];
    }

    // One action per group: a request removes every path from the group it names, so mixed groups cannot share one.
    private static ImmutableArray<DiagnosticAction> ListActionsFor(Loadout.ReadOnly loadout, IReadOnlyList<(GamePath Path, SyncNode Node)> files)
    {
        var byGroup = new Dictionary<EntityId, List<GamePath>>();
        foreach (var (path, node) in files)
        {
            if (OwningGroup.Of(loadout.Db, node) is not { } group) continue;
            if (!byGroup.TryGetValue(group, out var paths)) byGroup[group] = paths = [];
            paths.Add(path);
        }

        var actions = byGroup.Select(g => DiagnosticActions.RemoveLoadoutFiles(g.Value, g.Key)).ToList();
        return [.. actions];
    }

    private static bool IsRootLibrary(GamePath path, IReadOnlyList<GamePath> executableFolders)
        => path.Extension == new Extension(".dll") && executableFolders.Contains(path.Parent);

    private static string? SteamAppId(GameInstallation installation)
    {
        var locator = installation.LocatorResult;
        return locator.Store == GameStore.Steam && ulong.TryParse(locator.StoreIdentifier, NumberStyles.None, CultureInfo.InvariantCulture, out _)
            ? locator.StoreIdentifier
            : null;
    }

    private static bool IsUnmanaged(GamePath path, SyncNode node, ModProvidedFiles provided, IReadOnlyDictionary<GamePath, (FileIdentity Identity, Size Size)> gameInstalled)
    {
        if (!node.HaveDisk) return false;
        if (gameInstalled.ContainsKey(path)) return false;
        if (node.SourceItemType is LoadoutSourceItemType.Game or LoadoutSourceItemType.Intrinsic) return false;
        if (provided.IsProvidedByMod(node)) return false;

        // The app wrote it in an earlier apply and no enabled mod has it now, so the next apply takes it off disk.
        if (node.HavePrevious && !node.HaveLoadout) return false;

        // A row of the Overrides group is a leftover; a disk-only file has no loadout side at all.
        return true;
    }

    private static MarkdownFragment PluginDetails() => new("""
A plugin is built for one game version and one loader build. A leftover from a mod you removed, or from an older
install, can crash the game on startup or when it loads a save.

If you do not know where it came from, delete the file. If a mod you still use needs it, install that mod through the
app so the loadout owns the file.
""");

    private static MarkdownFragment GameFolderDetails(string gameName, bool shippedByStore)
    {
        var sb = new StringBuilder()
            .Append(gameName).AppendLine(" loads this library from its own folder: an executable there names it, or it has a name mod loaders use.")
            .AppendLine()
            .AppendLine("If you do not know where it came from, delete the file. If a mod you still use needs it, install that mod through the")
            .AppendLine("app so the loadout owns the file.");
        if (shippedByStore)
        {
            sb.AppendLine()
                .AppendLine("The store ships a file of this name too, and the app does not hold the store's copy. After you delete this one the game")
                .AppendLine("needs the store to put its own back: use the verify button to have Steam do it.");
        }
        return new MarkdownFragment(sb.ToString());
    }

    private static MarkdownFragment UnloadedReason(string file, RootLoadVerdict verdict)
        => verdict.Load == RootLoad.ShadowedByWine
            ? new MarkdownFragment($"Wine loads its own {MarkdownText.Code(file)} unless the launch options set {MarkdownText.Code(verdict.OverrideThatWouldLoad)}; with that override this file would load.")
            : new MarkdownFragment("No executable in the folder names it and it has none of the names mod loaders use, so as far as the app can tell the game does not load it from here. The game may still load it by name at run time.");

    private static MarkdownFragment ListOf(IEnumerable<GamePath> paths)
    {
        var sb = new StringBuilder();
        foreach (var path in paths) sb.Append("* ").AppendLine(MarkdownText.Code(path.Path));
        return new MarkdownFragment(sb.ToString());
    }
}
