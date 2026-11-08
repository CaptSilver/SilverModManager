using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using SilverModManager.Sdk.Hashes;
using NexusMods.Paths;
using SilverModManager.Abstractions.Cli;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Ini;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Ini;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.ProxyConsole;

namespace SilverModManager.DataModel.CommandLine.Verbs;

/// <summary>
/// The INI verbs: a loadout's copies of the game's INI files, the tweak layers laid over them, and the bytes the next
/// Apply writes. They go through <see cref="GameInis"/> and <see cref="IniTweakLayers"/>, the code the INI page uses.
/// </summary>
internal static class LoadoutIniVerbs
{
    private const string UnknownPath = "unknown";

    // The "loadout" module comes from AddLoadoutManagementVerbs.
    public static IServiceCollection AddLoadoutIniVerbs(this IServiceCollection services) => services
        .AddModule("loadout ini", "Commands for a loadout's copies of the game's INI files and the tweaks laid over them")
        .AddVerb(() => List)
        .AddVerb(() => Show);

    private sealed record IniLayerRow(string Name, string Source, string Kind, string FileId, bool On, bool Active);

    private sealed record IniFileRow(string File, string? Path, bool HasBaseline, bool? UpToDate, bool? OnDisk, IniLayerRow[] Layers);

    private sealed record IniListRow(bool OwnsGameInis, IniFileRow[] Files);

    /// <summary>The loadout's game as the INI verbs need it, with the paths of its INI files.</summary>
    /// <exception cref="LoadoutVerbRefusedException">The game does not keep INI files.</exception>
    internal static (IGameIniFiles Game, GamePath[] Paths) ResolveGame(Loadout.ReadOnly loadout)
    {
        // By the game id alone: locating the install would throw for a library that is not mounted.
        if (loadout.TryGetGame(out var data) && data is IGameIniFiles game && game.IniFiles.FileNames.Count > 0)
            return (game, GameInis.Paths(game).ToArray());

        var gameName = loadout.TryGetGame(out var known) ? known.DisplayName : "unknown";
        throw new LoadoutVerbRefusedException($"{loadout.Name} is a {gameName} loadout. The app does not keep that game's INI files, so there is nothing to tweak.");
    }

    /// <summary>The path of the game's INI called <paramref name="fileName"/>, matched without regard to case.</summary>
    /// <exception cref="LoadoutVerbRefusedException">The game has no such INI.</exception>
    internal static GamePath ResolveFile(IGameIniFiles game, string fileName)
    {
        var match = game.IniFiles.FileNames.FirstOrDefault(n => n.Equals(fileName, StringComparison.OrdinalIgnoreCase));
        if (match is not null) return game.IniPath(match);

        var gameName = game is IGameData data ? data.DisplayName : "the game";
        throw new LoadoutVerbRefusedException($"{fileName} is not one of {gameName}'s INI files. They are: {string.Join(", ", game.IniFiles.FileNames)}.");
    }

    [Verb("loadout ini list", "Lists a loadout's game INI files with their path, whether the loadout has a copy and whether the file on disk is what the last Apply wrote, and the tweak layers of each. The path is unknown when the game's install cannot be found. The bake and disk checks are unknown (null in JSON) until an Apply has written the file")]
    private static async Task<int> List([Injected] IRenderer renderer,
        [Option("l", "loadout", "Loadout to list")] Loadout.ReadOnly loadout,
        [Option("j", "json", "Print the files as JSON", true)] bool json,
        [Injected] IFileStore fileStore,
        [Injected] IGameRegistry registry)
    {
        var (game, paths) = ResolveGame(loadout);
        var owns = Loadout.OwnsGameInis.Get(loadout);

        var rows = new List<IniFileRow>();
        foreach (var path in paths)
            rows.Add(await FileRow(fileStore, registry, game, loadout, path));

        if (json)
        {
            await renderer.TextLine(JsonSerializer.Serialize(new IniListRow(owns, [.. rows]), CliJson.Options));
            return 0;
        }

        await renderer.TextLine("{0}", owns ? "Owns the game's INIs: yes" : "Owns the game's INIs: no (nothing is baked; turn it on in the loadout card)");
        foreach (var row in rows)
        {
            await renderer.TextLine("{0}", $"{row.File}  -> {row.Path ?? UnknownPath}  baseline: {YesNo(row.HasBaseline)}  changed since the last Apply: {Changed(row.UpToDate)}  on disk: {OnDisk(row)}");
            if (row.Layers.Length == 0) continue;

            await renderer.TextLine("{0}", $"  {"Layer",-20} {"Source",-24} {"Kind",-5} {"On",-4} {"Active",-7} Id");
            foreach (var layer in row.Layers)
                await renderer.TextLine("{0}", $"  {layer.Name,-20} {layer.Source,-24} {layer.Kind,-5} {YesNo(layer.On),-4} {YesNo(layer.Active),-7} {layer.FileId}");
        }
        return 0;
    }

    [Verb("loadout ini show", "Prints one of a loadout's game INIs as text: the result Apply writes (the default), the loadout's copy before any tweak, the user's own tweak, or one tweak layer by the id 'loadout ini list' prints")]
    private static async Task<int> Show([Injected] IRenderer renderer,
        [Option("l", "loadout", "Loadout to read")] Loadout.ReadOnly loadout,
        [Option("f", "file", "The INI's file name as 'loadout ini list' prints it")] string file,
        [Option("part", "part", "result (what Apply writes; the default), baseline (the loadout's copy before tweaks) or tweak (the user's own tweak)", true)] string? part,
        [Option("layer", "layer", "A layer's id from 'loadout ini list', to read that layer's text instead of a part", true)] string? layer,
        [Injected] IFileStore fileStore)
    {
        var (game, _) = ResolveGame(loadout);
        var path = ResolveFile(game, file);
        var fileName = path.FileName.ToString();

        if (part is not null && layer is not null)
            throw new LoadoutVerbRefusedException("Give either --part or --layer.");
        if (part is not null && part is not ("result" or "baseline" or "tweak"))
            throw new LoadoutVerbRefusedException($"{part} is not a part. Use result, baseline or tweak.");

        if (layer is not null)
        {
            var found = IniTweakLayers.All(loadout, path).Where(t => t.FileId.ToString().Equals(layer, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (found.Length == 0)
                throw new LoadoutVerbRefusedException($"{layer} is not a tweak layer of {fileName}. 'loadout ini list' prints the ids.");
            await renderer.Text("{0}", IniDocument.Parse(await fileStore.Load(found[0].Identity)).ToText());
            return 0;
        }

        part ??= "result";
        if (part == "tweak")
        {
            if (IniTweakLayers.UserTweak(loadout, path) is { } user) await renderer.Text("{0}", IniDocument.Parse(await fileStore.Load(user.Identity)).ToText());
            return 0;
        }

        if (GameInis.StateOf(loadout, path) is null)
            throw new LoadoutVerbRefusedException($"{fileName} has no copy in this loadout yet. The first Apply takes one from the game folder.");

        if (part == "baseline")
        {
            await renderer.Text("{0}", IniDocument.Parse(await GameInis.LoadBaseline(fileStore, loadout, path)).ToText());
            return 0;
        }

        await renderer.Text("{0}", IniDocument.Parse(await GameInis.Render(game, fileStore, loadout, path)).ToText());
        if (!Loadout.OwnsGameInis.Get(loadout))
            await renderer.TextLine("{0}", OwnershipWarning);
        return 0;
    }

    internal const string OwnershipWarning = "This loadout does not own its INIs, so nothing is baked until that is turned on in the loadout card.";

    private static async Task<IniFileRow> FileRow(IFileStore fileStore, IGameRegistry registry, IGameIniFiles game, Loadout.ReadOnly loadout, GamePath path)
    {
        var state = GameInis.StateOf(loadout, path);
        var absolute = TryAbsolutePath(registry, loadout, path);

        var baked = state is { } have && LoadoutIniState.LastBakedHash.TryGetValue(have, out var hash) ? hash : (Hash?)null;

        // Without a recorded bake there is nothing to compare against, so both answers are unknown.
        bool? upToDate = null;
        bool? onDisk = null;
        if (baked is { } bake)
        {
            upToDate = (await GameInis.Render(game, fileStore, loadout, path)).xxHash3() == bake;
            if (absolute is { } file && file.FileExists)
                onDisk = (await file.ReadAllBytesAsync()).xxHash3() == bake;
        }

        var layers = IniTweakLayers.All(loadout, path)
            .Select(t => new IniLayerRow(t.Name, t.SourceName, t.Kind.ToString().ToLowerInvariant(), t.FileId.ToString(), t.IsSwitchedOn, t.IsEnabled))
            .ToArray();
        return new IniFileRow(path.FileName.ToString(), absolute?.ToString(), state is not null, upToDate, onDisk, layers);
    }

    // The path is unknown while the registry cannot find the loadout's install (its library is not mounted).
    private static AbsolutePath? TryAbsolutePath(IGameRegistry registry, Loadout.ReadOnly loadout, GamePath path)
    {
        if (!registry.TryGetGameInstallation(loadout, out var installation)) return null;
        var locations = installation.Locations;
        return locations.ContainsKey(path.LocationId) ? locations.ToAbsolutePath(path) : null;
    }

    private static string YesNo(bool value) => value ? "yes" : "no";

    private static string Changed(bool? upToDate) => upToDate switch
    {
        true => "no",
        false => "yes",
        null => "unknown (never applied)",
    };

    // OnDisk is null without a bake as well as without a file; UpToDate is null only without a bake.
    private static string OnDisk(IniFileRow row) => row switch
    {
        { OnDisk: true } => "as applied",
        { OnDisk: false } => "differs from the last Apply",
        { UpToDate: null } => "never applied",
        _ => "no file",
    };
}
