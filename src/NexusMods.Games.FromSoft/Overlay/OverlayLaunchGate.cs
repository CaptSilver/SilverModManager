using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Loadouts;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Platform.Proton.Launch;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using NexusMods.Sdk.Settings;

namespace NexusMods.Games.FromSoft.Overlay;

/// <summary>
/// Gets a modded FromSoft launch ready for its overlay and looks at what the session left behind. Does nothing
/// for another game, and nothing for a loadout without mods: that one starts through Steam as it is and is not
/// vetoed for lacking me3.
/// </summary>
public sealed class OverlayLaunchGate(
    IConnection connection,
    ISettingsManager settings,
    IFileSystem fileSystem,
    IExecutableLocator executables,
    ISandboxProbe probe,
    ILogger<OverlayLaunchGate> logger) : ILaunchGate
{
    public const string Bwrap = OverlayCommand.Program;

    /// <inheritdoc/>
    public Veto? Check(LaunchContext context)
    {
        if (ModdedGame(context) is not { } game) return null;

        var installation = context.Installation;
        if (executables.Find(Bwrap) is not { } bwrap)
            return new Veto($"Cannot start {installation.Game.DisplayName} with mods: bwrap was not found on PATH. It keeps the game's own folder from being written to; install bubblewrap and try again.");

        if (probe.Probe(bwrap) is { } probeFailure)
            return new Veto($"Cannot start {installation.Game.DisplayName} with mods: bwrap cannot make a user namespace here ({probeFailure}). Without one the game's folder cannot be kept from being written to.");

        if (installation.LocatorResult.StoreUpdatePending)
            return new Veto($"Cannot start {installation.Game.DisplayName} with mods: a Steam update is waiting for it. Let the update finish in Steam first, then launch again.");

        if (game.Loader.CheckHost(installation, context.LoadoutName) is { } hostVeto) return hostVeto;

        var layout = LayoutOf(context);
        var pending = UpperdirAudit.PendingFindings(layout);
        if (pending.Count > 0)
            return new Veto($"Cannot start {installation.Game.DisplayName} with mods: the last session left {pending.Count} unresolved finding(s) in its overlay ({Describe(pending)}). The health check lists them; they are kept in {layout.Findings}, and the launch is refused until they are discarded.");

        return null;
    }

    /// <inheritdoc/>
    public async Task Prepare(LaunchContext context, CancellationToken cancellationToken)
    {
        if (ModdedGame(context) is not { } game) return;

        var layout = LayoutOf(context);
        var installation = context.Installation;
        layout.Upper.CreateDirectory();
        layout.Work.CreateDirectory();
        game.Loader.PrepareHost(installation);

        var written = await WriteConfigurationLayer(layout, game, installation, cancellationToken);

        var snapshot = await GameFolderSnapshot.Take(layout.Lower, cancellationToken);
        new OverlaySessionState(written, snapshot).Save(layout);
        logger.LogInformation("Prepared the overlay of {Game} at {Root}: {Count} configuration files, {Snapshot} game files recorded", installation.Game.DisplayName, layout.Root, written.Count, snapshot.Entries.Count);
    }

    /// <inheritdoc/>
    public async Task Finish(LaunchContext context)
    {
        if (ModdedGame(context) is not { } game) return;

        var layout = LayoutOf(context);
        var findings = await UpperdirAudit.Run(layout, game.Overlay);

        if (OverlaySessionState.Load(layout) is { } state)
        {
            var changes = state.Snapshot.Compare(await GameFolderSnapshot.Take(layout.Lower));
            if (changes.Count > 0)
            {
                logger.LogError("The game folder {Folder} changed during a modded session: {Paths}", layout.Lower, string.Join(", ", changes.Select(c => $"{c.Path} ({c.Change})")));
                UpperdirAudit.RecordGameFolderChanges(layout, changes);
            }
        }

        if (findings.Count > 0) logger.LogWarning("The overlay of {Loadout} holds {Count} files that need a look: {Paths}", context.LoadoutName, findings.Count, string.Join(", ", findings.Select(f => f.Path)));
    }

    /// <summary>
    /// Copies every file of the configuration layer into the upper folder. The files are read from where the
    /// synchronizer has just deployed them, so a mod that is disabled or removed is not copied. A file written by an
    /// earlier session that is no longer in the layer is removed, or a removed mod would keep acting on the game.
    /// </summary>
    private async Task<Dictionary<string, string>> WriteConfigurationLayer(OverlayLayout layout, IFromSoftGame game, GameInstallation installation, CancellationToken cancellationToken)
    {
        var layer = game.Loader.ConfigurationLayer(installation);
        var transforms = game.LayerTransforms;
        var written = new Dictionary<string, string>();

        if (layer.DirectoryExists())
        {
            var layerRoot = layer.GetFullPath();
            foreach (var file in Directory.EnumerateFiles(layerRoot, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(layerRoot, file).Replace('\\', '/');
                var targetPath = layout.Upper / relative;
                targetPath.Parent.CreateDirectory();
                var target = targetPath.GetFullPath();

                if (transforms.FirstOrDefault(t => t.AppliesTo(relative)) is { } transform)
                    await transform.WriteAsync(file, target, cancellationToken);
                else
                    File.Copy(file, target, overwrite: true);

                written[relative] = await UpperdirAudit.HashOf(layout, target, cancellationToken);
            }
        }

        if (OverlaySessionState.Load(layout) is { } previous)
        {
            foreach (var stale in previous.Written.Keys.Where(p => !written.ContainsKey(p)))
            {
                var target = Path.Combine(layout.Upper.GetFullPath(), stale);
                if (File.Exists(target)) File.Delete(target);
            }
        }

        return written;
    }

    private IFromSoftGame? ModdedGame(LaunchContext context)
    {
        if (context.Installation.Game is not IFromSoftGame game) return null;
        var loadout = Loadout.Load(connection.Db, context.LoadoutId);
        return game.Loader.IsModded(loadout) ? game : null;
    }

    private OverlayLayout LayoutOf(LaunchContext context)
        => OverlayLayout.For(settings.Get<FromSoftSettings>(), fileSystem, context.Installation, context.LoadoutId);

    private static string Describe(IReadOnlyList<AuditFinding> findings)
        => string.Join(", ", findings.Take(5).Select(f => f.Path)) + (findings.Count > 5 ? ", ..." : "");
}
