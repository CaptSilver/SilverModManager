using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Diagnostics.Emitters;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Abstractions.Loadouts.Synchronizers.Rules;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.FileHashes.Emitters;

public class UndeployableLoadoutDueToMissingGameFiles : ILoadoutDiagnosticEmitter
{
    private readonly IConnection _connection;

    public UndeployableLoadoutDueToMissingGameFiles(IServiceProvider serviceProvider)
    {
        _connection = serviceProvider.GetRequiredService<IConnection>();
    }

    public IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, CancellationToken cancellationToken) => throw new NotSupportedException();
    public async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, FrozenDictionary<GamePath, SyncNode> syncTree, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        // A file with no disk part is gone from the folder, and the store can restore it. One with a disk part is
        // there with a hash the app's data for this build doesn't list, so "verify in the store" would be the
        // wrong advice when that data is what's wrong.
        var missing = new Group();
        var mismatched = new Group();

        foreach (var (gamePath, node) in syncTree)
        {
            if (node.SourceItemType is not LoadoutSourceItemType.Game || !node.Actions.HasFlag(Actions.WarnOfUnableToExtract)) continue;
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
                Version: version
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
