namespace SilverModManager.Sdk.Hashes;

/// <summary>
/// The answer to "are these the same bytes?" for two <see cref="FileIdentity"/> values. Two digests of different
/// kinds cannot answer that without a recorded relation or a read, so that case is <see cref="Unknown"/>, not
/// <see cref="Different"/>.
/// </summary>
public enum IdentityMatch : byte
{
    /// <summary>Same kind, same digest.</summary>
    Same,

    /// <summary>Same kind, different digest.</summary>
    Different,

    /// <summary>The kinds differ, so the digests say nothing either way.</summary>
    Unknown,
}
