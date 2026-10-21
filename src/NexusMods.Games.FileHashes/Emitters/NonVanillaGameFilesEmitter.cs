using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Text;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Diagnostics.Emitters;
using NexusMods.Abstractions.Games.FileHashes;
using NexusMods.Abstractions.Games.FileHashes.Models;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.FileHashes.Emitters;

/// <summary>
/// Reports the files in a game folder that are not the files the store installed. The list is the
/// non-vanilla rows of the installation's local baseline, so it only changes when the baseline is
/// rebuilt: it is the same after a restart and it goes away once a rebuild finds the files vanilla again.
/// </summary>
public class NonVanillaGameFilesEmitter : ILoadoutDiagnosticEmitter
{
    private readonly IFileHashesService _fileHashes;

    public NonVanillaGameFilesEmitter(IFileHashesService fileHashes)
    {
        _fileHashes = fileHashes;
    }

    // The notice does not depend on the sync tree, so the old overload can answer too.
    public IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, CancellationToken cancellationToken)
        => Diagnose(loadout, FrozenDictionary<GamePath, SyncNode>.Empty, cancellationToken);

    public async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, FrozenDictionary<GamePath, SyncNode> syncTree, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        var installation = loadout.Installation;
        var db = _fileHashes.Local;

        var baseline = LocalBaseline.FindByGamePath(db, installation.Path)
            .Where(b => b.Store == installation.Store)
            .Cast<LocalBaseline.ReadOnly?>()
            .FirstOrDefault();
        if (baseline is null) yield break;

        var files = NonVanillaFile.FindByBaseline(db, baseline.Value.Id)
            .OrderBy(f => f.Path.ToString(), StringComparer.Ordinal)
            .ToArray();
        if (files.Length == 0) yield break;

        var list = new StringBuilder();
        foreach (var file in files)
        {
            list.AppendLine($"* `{file.Path}` ({DescribeReason(file.Reason)})");
        }

        yield return Diagnostics.CreateNonVanillaGameFiles(
            Game: installation.Name,
            Store: installation.Store.Value,
            FileCount: files.Length,
            Files: list.ToString()
        );
    }

    private static string DescribeReason(NonVanillaReason reason) => reason switch
    {
        NonVanillaReason.NotInManifest => "not in the store's manifest",
        NonVanillaReason.SizeDiffers => "size differs",
        NonVanillaReason.HashDiffers => "content differs",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
    };
}
