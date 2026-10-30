using NexusMods.Hashing.xxHash3;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Wabbajack.Tests;

/// <summary>Builds the xxHash64 identities a modlist names its files with.</summary>
internal static class Xx64
{
    public static FileIdentity From(ulong value) => FileIdentity.XxHash64(Hash.From(value));

    /// <summary>The raw xxHash64 of an identity made by this class.</summary>
    public static Hash XxHash64Value(this FileIdentity identity) => identity.TryXxHash64(out var hash) ? hash : throw new InvalidOperationException($"{identity} is not an xxHash64");

    public static FileIdentity Of(byte[] bytes) => FileIdentity.XxHash64(Xx64Hasher.Hash(bytes));
}
