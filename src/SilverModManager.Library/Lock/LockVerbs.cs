using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Cli;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Library.Sources;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.NexusModsApi;
using SilverModManager.Sdk.ProxyConsole;

namespace SilverModManager.Library.Lock;

internal static class LockVerbs
{
    internal static IServiceCollection AddLockVerbs(this IServiceCollection services) => services
        .AddSingleton<ILibrarySource, NexusLibrarySource>()
        .AddSingleton<ILibrarySource, GitHubLibrarySource>()
        .AddSingleton<ILibrarySource, ThunderstoreLibrarySource>()
        .AddTransient<LockExport>()
        .AddTransient<LockVerify>()
        .AddTransient<LockImport>()
        .AddModule("library", "Commands for the files in the library")
        .AddVerb(() => Verify)
        .AddVerb(() => ExportLock)
        .AddVerb(() => ImportLock);

    [Verb("library verify", "Checks that the library holds every file of a lock with the locked sha256; exits 1 if any does not")]
    private static async Task<int> Verify(
        [Injected] IRenderer renderer,
        [Injected] LockVerify verify,
        [Injected] CancellationToken token,
        [Option("l", "lock", "The lock file to check")] AbsolutePath lockPath,
        [Option("j", "json", "Print the report as JSON", true)] bool json)
    {
        LockFile lockFile;
        try
        {
            lockFile = LockFile.Parse(await File.ReadAllTextAsync(lockPath.ToString(), token));
        }
        catch (Exception exception) when (exception is LockException or IOException)
        {
            await renderer.TextLine(exception.Message);
            return 1;
        }

        var report = await verify.Verify(lockFile, token);
        if (json)
        {
            await renderer.TextLine(report.ToJson());
            return report.ExitCode;
        }

        foreach (var item in report.Items) await renderer.TextLine($"[{item.Level.ToString().ToLowerInvariant()}] {item.Message}");
        return report.ExitCode;
    }

    [Verb("library export-lock", "Writes the Nexus Mods, GitHub release and Thunderstore files of a loadout to a lock file; source = \"thunderstore\" entries are new to erm and it will not read them")]
    private static async Task<int> ExportLock(
        [Injected] IRenderer renderer,
        [Injected] LockExport export,
        [Injected] CancellationToken token,
        [Option("l", "loadout", "The loadout whose mods to pin")] Loadout.ReadOnly loadout,
        [Option("o", "out", "The lock file to write")] AbsolutePath output)
    {
        try
        {
            var lockFile = await export.FromLoadout(loadout, token);
            await File.WriteAllTextAsync(output.ToString(), lockFile.Write(), token);
            await renderer.TextLine($"Wrote {lockFile.Entries.Count} entries to {output}.");
            return 0;
        }
        catch (Exception exception) when (exception is LockException or IOException)
        {
            await renderer.TextLine(exception.Message);
            return 1;
        }
    }

    [Verb("library import-lock", "Downloads the files of a lock into the library, verifies each against its sha256 and installs them into the loadout; exits 1 if any entry fails")]
    private static async Task<int> ImportLock(
        [Injected] IRenderer renderer,
        [Injected] IServiceProvider provider,
        [Injected] IConnection connection,
        [Injected] CancellationToken token,
        [Option("l", "loadout", "The loadout to install into; its game says which Nexus Mods game the file ids belong to")] Loadout.ReadOnly loadout,
        [Option("f", "file", "The lock file to import")] AbsolutePath lockPath)
    {
        LockFile lockFile;
        try
        {
            lockFile = LockFile.Parse(await File.ReadAllTextAsync(lockPath.ToString(), token));
        }
        catch (Exception exception) when (exception is LockException or IOException)
        {
            await renderer.TextLine(exception.Message);
            return 1;
        }

        NexusModsGameId? game = GameInstallMetadata.GameId.TryGetValue(loadout.Installation, out var gameId) ? gameId : null;
        // A loadout whose game is not installed still imports; only sources that need the game's data lose it.
        var gameData = provider.GetRequiredService<IGameRegistry>().TryGetGameInstallation(loadout, out var installation) ? installation.Game : null;
        var import = provider.GetRequiredService<LockImport>();
        var summary = await import.Apply(await import.Plan(lockFile, game, token, gameData), token);

        foreach (var outcome in summary.Verified) await renderer.TextLine($"[verified] {outcome.Message}");
        foreach (var outcome in summary.Skipped) await renderer.TextLine($"[skipped] {outcome.Message}");
        foreach (var outcome in summary.Failed) await renderer.TextLine($"[failed] {outcome.Message}");
        await renderer.TextLine($"{summary.Verified.Count} verified, {summary.Skipped.Count} already in the library, {summary.Failed.Count} failed.");

        // The verified files and the ones that were already there belong in the loadout; a failed entry never does.
        var loadoutManager = provider.GetRequiredService<ILoadoutManager>();
        foreach (var outcome in summary.Verified.Concat(summary.Skipped))
        {
            if (outcome.File is not { } file) continue;
            var item = file.AsLibraryItem();
            var linked = LibraryLinkedLoadoutItem.FindByLibraryItem(connection.Db, item).Any(link => link.AsLoadoutItem().LoadoutId == loadout.LoadoutId);
            if (linked) continue;

            await loadoutManager.InstallItem(item, loadout.LoadoutId);
            await renderer.TextLine($"[installed] {outcome.Entry.Id}");
        }

        return summary.Failed.Count == 0 ? 0 : 1;
    }
}
