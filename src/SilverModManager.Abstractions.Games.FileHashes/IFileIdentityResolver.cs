using NexusMods.Paths;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Abstractions.Games.FileHashes;

/// <summary>
/// Answers "are these the same bytes?" for identities of different kinds by joining them through
/// <see cref="Models.HashRelation"/> rows that were already recorded. Nothing here reads a file.
/// </summary>
public interface IFileIdentityResolver
{
    /// <summary>
    /// The same bytes' identity in <paramref name="kind"/>, from rows already recorded. A row whose size differs
    /// is not these bytes. Returns the input when it is already of <paramref name="kind"/>. False when no row
    /// joins the two; the caller then reads the file or treats the answer as unknown.
    /// </summary>
    bool TryConvert(FileIdentity identity, Size size, HashKind kind, out FileIdentity converted);

    /// <summary>
    /// <see cref="IdentityMatch.Same"/> or <see cref="IdentityMatch.Different"/> when the kinds agree or one side
    /// converts into the other's; <see cref="IdentityMatch.Unknown"/> otherwise.
    /// </summary>
    IdentityMatch Resolve(FileIdentity left, FileIdentity right, Size size);

    /// <summary>
    /// Every xxHash3 identity the recorded rows join <paramref name="identity"/> and <paramref name="size"/> to,
    /// for callers that must then ask the store which one it holds. An xxHash3 identity is its own candidate;
    /// any other kind yields nothing when no row joins it.
    /// </summary>
    IEnumerable<FileIdentity> StoreKeyCandidates(FileIdentity identity, Size size);

    /// <summary>A memo over this resolver for one pass (a tree build, a reset, a plan).</summary>
    IFileIdentityResolver Scoped();
}
