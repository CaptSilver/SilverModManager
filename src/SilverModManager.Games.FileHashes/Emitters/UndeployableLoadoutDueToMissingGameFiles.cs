using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Games.Merge;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Rules;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.FileHashes.Emitters;

public class UndeployableLoadoutDueToMissingGameFiles : ILoadoutDiagnosticEmitter
{
    private readonly IConnection _connection;
    private readonly ILogger _logger;

    public UndeployableLoadoutDueToMissingGameFiles(IServiceProvider serviceProvider)
    {
        _connection = serviceProvider.GetRequiredService<IConnection>();
        _logger = serviceProvider.GetService<ILogger<UndeployableLoadoutDueToMissingGameFiles>>() ?? NullLogger<UndeployableLoadoutDueToMissingGameFiles>.Instance;
    }

    public IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, CancellationToken cancellationToken) => throw new NotSupportedException();
    public async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, FrozenDictionary<GamePath, SyncNode> syncTree, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        // A file with no disk part is gone from the folder, and the store can restore it. One with a disk part is
        // there with a hash the app's data for this build doesn't list: verifying in the store is the first thing to
        // try, but when the same files come back, that data is what's wrong and the card says to report it.
        var missing = new Group();
        var mismatched = new Group();

        foreach (var (gamePath, node) in syncTree)
        {
            if (node.SourceItemType is not LoadoutSourceItemType.Game || !node.Actions.HasFlag(Actions.WarnOfUnableToExtract)) continue;
            if (node.HaveDisk)
            {
                _logger.LogDebug("`{Path}` is counted as differing from the app's file data: disk {Disk}, loadout {Loadout}", gamePath, node.Disk.Identity, node.Loadout.Identity);
            }

            (node.HaveDisk ? mismatched : missing).Add(gamePath, node);
        }

        var game = loadout.Installation.Name;
        var store = loadout.Installation.Store.Value;
        var version = loadout.GameVersion.ToString();

        if (missing.Count > 0)
        {
            yield return Diagnostics.CreateUndeployableLoadoutDueToMissingGameFiles(
                Size: missing.Size,
                FileCount: missing.Count,
                Game: game,
                Files: missing.Files.ToString(),
                Store: store,
                Version: version
            );
        }

        if (mismatched.Count > 0)
        {
            yield return Diagnostics.CreateGameFilesDifferFromTheAppsFileData(
                Size: mismatched.Size,
                FileCount: mismatched.Count,
                Game: game,
                Files: mismatched.Files.ToString(),
                Store: store,
                Version: version,
                Verify: EngineMergeDiagnosticsEmitter.VerifyFiles(loadout.Installation.Store)
            );
        }
    }

    private sealed class Group
    {
        public StringBuilder Files { get; } = new();
        public Size Size { get; private set; } = Size.Zero;
        public int Count { get; private set; }

        public void Add(GamePath path, SyncNode node)
        {
            Size += node.Loadout.Size;
            Count++;
            Files.AppendLine($"* `{path}`");
        }
    }
}
