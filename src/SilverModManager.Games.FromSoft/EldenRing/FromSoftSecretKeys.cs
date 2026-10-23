using NexusMods.Abstractions.Secrets;

namespace NexusMods.Games.FromSoft.EldenRing;

public static class FromSoftSecretKeys
{
    /// <summary>The Seamless Co-op session password.</summary>
    public static readonly SecretKey CoopPassword = new("fromsoft.ersc-password");
}
