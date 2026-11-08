using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using SilverModManager.Sdk.Hashes;
using NexusMods.Paths;
using SilverModManager.Abstractions.Cli;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Ini;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Ini;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.ProxyConsole;

namespace SilverModManager.DataModel.CommandLine.Verbs;

/// <summary>
/// The INI verbs: a loadout's copies of the game's INI files, the tweak layers laid over them, and the bytes the next
/// Apply writes, and the loadout's own tweak. They go through <see cref="GameInis"/>, <see cref="IniTweakLayers"/> and
/// <see cref="IniEdits"/>, the code the INI page uses.
/// </summary>
internal static class LoadoutIniVerbs
{
    private const string UnknownPath = "unknown";

    // The "loadout" module comes from AddLoadoutManagementVerbs.
    public static IServiceCollection AddLoadoutIniVerbs(this IServiceCollection services) => services
        .AddModule("loadout ini", "Commands for a loadout's copies of the game's INI files and the tweaks laid over them")
        .AddVerb(() => List)
        .AddVerb(() => Show)
        .AddVerb(() => Set)
        .AddVerb(() => Unset)
        .AddVerb(() => Replace);

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
            await renderer.Text(IniDocument.Parse(await fileStore.Load(found[0].Identity)).ToText());
            return 0;
        }

        part ??= "result";
        if (part == "tweak")
        {
            if (IniTweakLayers.UserTweak(loadout, path) is { } user) await renderer.Text(IniDocument.Parse(await fileStore.Load(user.Identity)).ToText());
            return 0;
        }

        if (GameInis.StateOf(loadout, path) is null)
            throw new LoadoutVerbRefusedException($"{fileName} has no copy in this loadout yet. The first Apply takes one from the game folder.");

        if (part == "baseline")
        {
            await renderer.Text(IniDocument.Parse(await GameInis.LoadBaseline(fileStore, loadout, path)).ToText());
            return 0;
        }

        await renderer.Text(IniDocument.Parse(await GameInis.Render(game, fileStore, loadout, path)).ToText());
        await WriteOwnershipWarning(renderer, loadout);
        return 0;
    }

    private const string AfterWrite = "Run 'loadout synchronize' to write it to the game folder. 'loadout revisions' lists the restore point; 'loadout revert' undoes it until the next Apply.";

    private static async Task WriteOwnershipWarning(IRenderer renderer, Loadout.ReadOnly loadout)
    {
        if (!Loadout.OwnsGameInis.Get(loadout))
            await renderer.TextLine("{0}", OwnershipWarning);
    }

    [Verb("loadout ini set", "Sets one key in the loadout's own tweak of a game INI, creating the tweak when there is none. The tweak is laid over the mods' tweaks on every Apply. A value starting with '-' is given as --value=-1. Writes a restore point")]
    private static async Task<int> Set([Injected] IRenderer renderer,
        [Option("l", "loadout", "Loadout to change")] Loadout.ReadOnly loadout,
        [Option("f", "file", "The INI's file name as 'loadout ini list' prints it")] string file,
        [Option("s", "section", "The section, without brackets")] string section,
        [Option("k", "key", "The key")] string key,
        [Option("v", "value", "The value; a value starting with '-' is given as --value=-1")] string value,
        [Injected] IConnection connection,
        [Injected] IFileStore fileStore)
    {
        var (game, _) = ResolveGame(loadout);
        var path = ResolveFile(game, file);
        var fileName = path.FileName.ToString();

        IniEdits.CheckTweakName(section, key, value);
        if (GameInis.AppSetsKey(game, loadout, path, section, key) is { } sets)
            throw new LoadoutVerbRefusedException(AppSetsMessage(section, key, fileName, sets));

        var (tweak, identity) = await IniEdits.LoadLoadoutTweak(fileStore, loadout, path);
        var old = tweak.Get(section, key);
        if (old == value)
        {
            await renderer.TextLine("{0}", $"{MarkdownText.Code($"[{section}] {key}")} is already {MarkdownText.Code(value)} in the loadout tweak of {MarkdownText.Code(fileName)}.");
            return 0;
        }

        tweak.Set(section, key, value);
        var bytes = tweak.ToBytes();
        await LoadoutSnapshot.WriteRestorePoint(connection, loadout.LoadoutId);
        await IniEdits.SetLoadoutTweak(connection, fileStore, loadout, path, bytes, identity);

        var was = old is null ? "was not set" : $"was {MarkdownText.Code(old)}";
        await renderer.TextLine("{0}", $"Set {MarkdownText.Code($"[{section}] {key}={value}")} in the loadout tweak of {MarkdownText.Code(fileName)} ({was}). {AfterWrite}");
        await WriteOwnershipWarning(renderer, loadout);
        return 0;
    }

    [Verb("loadout ini unset", "Removes a key from the loadout's own tweak of a game INI, every line that sets it. Writes a restore point")]
    private static async Task<int> Unset([Injected] IRenderer renderer,
        [Option("l", "loadout", "Loadout to change")] Loadout.ReadOnly loadout,
        [Option("f", "file", "The INI's file name as 'loadout ini list' prints it")] string file,
        [Option("s", "section", "The section, without brackets")] string section,
        [Option("k", "key", "The key")] string key,
        [Injected] IConnection connection,
        [Injected] IFileStore fileStore)
    {
        var (game, _) = ResolveGame(loadout);
        var path = ResolveFile(game, file);
        var fileName = path.FileName.ToString();

        IniEdits.CheckTweakName(section, key, null);
        var (tweak, identity) = await IniEdits.LoadLoadoutTweak(fileStore, loadout, path);
        var removed = false;
        while (tweak.Remove(section, key)) removed = true;
        if (!removed)
        {
            await renderer.TextLine("{0}", $"The loadout tweak of {MarkdownText.Code(fileName)} does not set {MarkdownText.Code($"[{section}] {key}")}.");
            return 0;
        }

        var bytes = tweak.ToBytes();
        await LoadoutSnapshot.WriteRestorePoint(connection, loadout.LoadoutId);
        await IniEdits.SetLoadoutTweak(connection, fileStore, loadout, path, bytes, identity);

        await renderer.TextLine("{0}", $"Removed {MarkdownText.Code($"[{section}] {key}")} from the loadout tweak of {MarkdownText.Code(fileName)}. {AfterWrite}");
        await WriteOwnershipWarning(renderer, loadout);
        return 0;
    }

    [Verb("loadout ini replace", "Replaces the whole of the loadout's own tweak of a game INI with the given INI text, or with the file --from names. An empty --text clears the tweak. Writes a restore point")]
    private static async Task<int> Replace([Injected] IRenderer renderer,
        [Option("l", "loadout", "Loadout to change")] Loadout.ReadOnly loadout,
        [Option("f", "file", "The INI's file name as 'loadout ini list' prints it")] string file,
        [Option("text", "text", "The new tweak as INI text", true)] string? text,
        [Option("", "from", "A file to take the new tweak from, as an absolute path", true)] AbsolutePath? from,
        [Injected] IConnection connection,
        [Injected] IFileStore fileStore)
    {
        var (game, _) = ResolveGame(loadout);
        var path = ResolveFile(game, file);
        var fileName = path.FileName.ToString();

        if (text is null == from is null)
            throw new LoadoutVerbRefusedException("Give either --text or --from.");

        var (current, identity) = await IniEdits.LoadLoadoutTweak(fileStore, loadout, path);
        var replacement = from is { } source
            ? IniDocument.Parse(await ReadFrom(source))
            : IniDocument.Parse(text ?? "", current.Encoding);
        if (replacement.ToText() == current.ToText())
        {
            await renderer.TextLine("{0}", $"The loadout tweak of {MarkdownText.Code(fileName)} already has that text.");
            return 0;
        }

        // An empty tweak is zero bytes, as the INI page's reset leaves it, not a byte order mark.
        var bytes = replacement.Lines.Count == 0 ? [] : replacement.ToBytes();

        foreach (var section in replacement.Sections)
            foreach (var key in replacement.Entries(section).Select(e => e.Key).Distinct(StringComparer.OrdinalIgnoreCase))
                if (GameInis.AppSetsKey(game, loadout, path, section, key) is { } sets)
                    await renderer.TextLine("{0}", $"Note: {AppSetsMessage(section, key, fileName, sets)}");

        await LoadoutSnapshot.WriteRestorePoint(connection, loadout.LoadoutId);
        await IniEdits.SetLoadoutTweak(connection, fileStore, loadout, path, bytes, identity);

        var settings = replacement.Lines.Count(l => l.Kind == IniLineKind.KeyValue);
        await renderer.TextLine("{0}", $"Replaced the loadout tweak of {MarkdownText.Code(fileName)} ({settings} settings). {AfterWrite}");
        await WriteOwnershipWarning(renderer, loadout);
        return 0;
    }

    private static async Task<byte[]> ReadFrom(AbsolutePath source)
    {
        if (!source.FileExists)
            throw new LoadoutVerbRefusedException($"{MarkdownText.Code(source)} is not a file.");
        return await source.ReadAllBytesAsync();
    }

    private static string AppSetsMessage(string section, string key, string fileName, string sets) =>
        $"The app sets {MarkdownText.Code($"[{section}] {key}")} in {MarkdownText.Code(fileName)} itself ({sets}), so a tweak of it would have no effect.";

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
