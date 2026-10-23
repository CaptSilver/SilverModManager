using JetBrains.Annotations;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.MnemonicAttributes;

namespace SilverModManager.Sdk.Resources;

/// <summary>
/// Represents a resource persisted in the database.
/// </summary>
[PublicAPI]
public partial class PersistedDbResource : IModelDefinition
{
    private const string Namespace = "SilverModManager.Resources.PersistedResource";

    /// <summary>
    /// The raw data.
    /// </summary>
    public static readonly BytesAttribute Data = new(Namespace, nameof(Data));

    /// <summary>
    /// The expiration date.
    /// </summary>
    public static readonly TimestampAttribute ExpiresAt = new(Namespace, nameof(ExpiresAt));

    /// <summary>
    /// The resource identifier as a hash.
    /// </summary>
    public static readonly HashAttribute ResourceIdentifierHash = new(Namespace, nameof(ResourceIdentifierHash));

    public partial struct ReadOnly
    {
        /// <summary>
        /// Whether the resource is expired.
        /// </summary>
        public bool IsExpired => DateTime.UtcNow > ExpiresAt;
    }
}
