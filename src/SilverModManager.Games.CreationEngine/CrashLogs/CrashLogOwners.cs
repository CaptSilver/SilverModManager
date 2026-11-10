using System.Collections.Frozen;
using NexusMods.Paths;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Diagnostics.References;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Games.CreationEngine.CrashLogs;

/// <summary>
/// Looks the files a crash report mentions up in the sync tree and names the enabled mod that supplies each. A file
/// that only the Overrides group holds, or that no loose file supplies (a BSA, the game itself), has no owner.
/// </summary>
public sealed class CrashLogOwners(
    FrozenDictionary<GamePath, SyncNode> tree,
    IDb db,
    ModProvidedFiles provided,
    IReadOnlyDictionary<GamePath, (FileIdentity Identity, Size Size)> gameInstalled)
{
    private const string FileKey = "File";
    private const string ModifiedByKey = "Modified by";
    private const string ChainSeparator = " -> ";
    private const int StopAtExecutableFrame = 2;

    private readonly Dictionary<GamePath, ModOwner?> _owners = [];

    /// <summary>The mod that supplies <paramref name="path"/>, or null when no enabled mod does.</summary>
    public ModOwner? OwnerOf(GamePath path)
    {
        if (_owners.TryGetValue(path, out var cached)) return cached;
        return _owners[path] = Resolve(path);
    }

    private ModOwner? Resolve(GamePath path)
    {
        if (!tree.TryGetValue(path, out var node) || !provided.IsProvidedByMod(node)) return null;
        if (OwningGroup.Of(db, node) is not { } group) return null;

        var groupId = LoadoutItemGroupId.From(group);
        var name = LoadoutItemGroup.Load(db, groupId).AsLoadoutItem().Name;
        return new ModOwner(new LoadoutItemGroupReference { TxId = db.BasisTxId, DataId = groupId }, name);
    }

    /// <summary>Who supplies the meshes, textures, plugins and script extender DLLs <paramref name="report"/> touched.</summary>
    public CrashOwnership Of(CrashReport report) => new(Assets(report), Plugins(report), SkseDlls(report), ModifiedByChains(report));

    private List<OwnedPath> Assets(CrashReport report)
    {
        var assets = new List<OwnedPath>();
        var seen = new HashSet<GamePath>();

        void Add(string text, ObjectDetails? source)
        {
            if (CrashLogPaths.Classify(text) is not { } path || !(CrashLogPaths.IsMesh(path) || CrashLogPaths.IsTexture(path))) return;
            if (!seen.Add(path)) return;
            var owner = OwnerOf(path);
            assets.Add(new OwnedPath(path, owner, owner is null && gameInstalled.ContainsKey(path), owner is null && source is not null ? PluginOwnerOf(source) : null));
        }

        foreach (var obj in report.RelevantObjects)
        {
            if (obj.Parenthetical is { } text) Add(text, DetailsOf(report, obj));
        }
        foreach (var details in report.ObjectDetails)
        {
            foreach (var line in details.All.Where(IsAssetLine)) Add(line.Value, details);
        }
        foreach (var request in report.RecentModelRequests) Add(request, null);
        foreach (var resource in report.ResourcePaths) Add(resource, null);
        return assets;
    }

    private static bool IsAssetLine(DetailLine line) =>
        line.Key.StartsWith("Texture", StringComparison.Ordinal)
        || line.Key.StartsWith("EffectTexture", StringComparison.Ordinal)
        || line.Key == FileKey;

    private static ObjectDetails? DetailsOf(CrashReport report, RelevantObject obj) =>
        report.ObjectDetails.FirstOrDefault(d => d.Location == obj.Location && d.Type == obj.Type);

    /// <summary>The owner of the first plugin the object's own block names, so a packed asset still leads to a mod.</summary>
    private ModOwner? PluginOwnerOf(ObjectDetails details) =>
        PluginsOf(details.Own).Select(OwnerOf).FirstOrDefault(o => o is not null);

    private List<OwnedPath> Plugins(CrashReport report)
    {
        var seen = new HashSet<GamePath>();
        var plugins = new List<OwnedPath>();
        var lines = report.ObjectDetails.SelectMany(d => d.All).ToList();
        foreach (var path in PluginsOf(lines).Concat(report.RelevantObjects.Select(o => o.Parenthetical).OfType<string>().Select(CrashLogPaths.Classify).OfType<GamePath>().Where(CrashLogPaths.IsPlugin)))
        {
            if (!seen.Add(path)) continue;
            var owner = OwnerOf(path);
            plugins.Add(new OwnedPath(path, owner, owner is null && gameInstalled.ContainsKey(path)));
        }
        return plugins;
    }

    /// <summary>The plugins a block names: every <c>File:</c> value and the last plugin of each <c>Modified by:</c> chain.</summary>
    private static IEnumerable<GamePath> PluginsOf(IEnumerable<DetailLine> lines)
    {
        foreach (var line in lines)
        {
            var name = line.Key switch
            {
                FileKey => line.Value,
                ModifiedByKey => line.Value[(line.Value.LastIndexOf(ChainSeparator, StringComparison.Ordinal) is var at and >= 0 ? at + ChainSeparator.Length : 0)..],
                _ => null,
            };
            if (name is not null && CrashLogPaths.Classify(name) is { } path && CrashLogPaths.IsPlugin(path)) yield return path;
        }
    }

    private static List<string> ModifiedByChains(CrashReport report) => report.ObjectDetails
        .SelectMany(d => d.All)
        .Where(l => l.Key == ModifiedByKey)
        .Select(l => l.Value)
        .Distinct(StringComparer.Ordinal)
        .ToList();

    /// <summary>
    /// The script extender DLLs among the probable frames before the second one in the game executable. The first
    /// executable frame is where the fault is; a DLL between it and the next is what called it (a hook).
    /// </summary>
    private List<OwnedPath> SkseDlls(CrashReport report)
    {
        var loaded = report.SksePlugins.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<GamePath>();
        var dlls = new List<OwnedPath>();
        var executableFrames = 0;
        foreach (var frame in report.Frames.Where(f => f.Kind == 'P'))
        {
            if (frame.IsExecutable && ++executableFrames >= StopAtExecutableFrame) break;
            if (frame.IsExecutable || !loaded.Contains(frame.Module)) continue;
            if (CrashLogPaths.Classify(frame.Module) is not { } path || !seen.Add(path)) continue;

            var owner = OwnerOf(path);
            dlls.Add(new OwnedPath(path, owner, owner is null && gameInstalled.ContainsKey(path)));
        }
        return dlls;
    }
}
