using System.Diagnostics.CodeAnalysis;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.MnemonicDB.Abstractions;

namespace SilverModManager.Wabbajack.Install;

/// <summary>A file row of a group. <see cref="Ids"/> is empty for one made in this run, and <see cref="Wanted"/> is set once this run's list asks for it.</summary>
internal sealed class KnownFile
{
    public List<EntityId> Ids { get; } = [];
    public bool Wanted { get; set; }

    /// <summary>The xxHash64 the list gave the directive this file was written for, when the run that wrote it recorded one.</summary>
    public FileIdentity? ListHash { get; set; }
}

/// <summary>
/// The files one mod group holds, keyed by path and content, with a second index by path alone. A resume asks "what
/// is at this path" for every directive, so walking all rows per question would cost the square of the group's size.
/// Only the thread that writes rows adds to it.
/// </summary>
internal sealed class GroupFiles
{
    private readonly Dictionary<(GamePath Path, FileIdentity Identity), KnownFile> _byKey = new();
    private readonly Dictionary<GamePath, List<(FileIdentity Identity, KnownFile Known)>> _byPath = new();

    public IEnumerable<KnownFile> Known => _byKey.Values;

    public IEnumerable<GamePath> Paths => _byPath.Keys;

    public KnownFile Add(GamePath path, FileIdentity identity, KnownFile known)
    {
        _byKey[(path, identity)] = known;
        if (!_byPath.TryGetValue(path, out var atPath)) _byPath[path] = atPath = [];
        atPath.RemoveAll(row => row.Identity.Equals(identity));
        atPath.Add((identity, known));
        return known;
    }

    public bool TryGet(GamePath path, FileIdentity identity, [MaybeNullWhen(false)] out KnownFile known)
        => _byKey.TryGetValue((path, identity), out known);

    /// <summary>Every row at <paramref name="path"/>, whatever its content.</summary>
    public IReadOnlyList<(FileIdentity Identity, KnownFile Known)> AtPath(GamePath path)
        => _byPath.TryGetValue(path, out var atPath) ? atPath : [];
}
