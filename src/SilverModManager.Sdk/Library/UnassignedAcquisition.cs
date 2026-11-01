using JetBrains.Annotations;
using SilverModManager.MnemonicDB.Abstractions;

namespace SilverModManager.Sdk.Library;

/// <summary>An item nothing says a game for; it is marked unassigned unless a recogniser names one.</summary>
[PublicAPI]
public sealed class UnassignedAcquisition : IAcquisitionContext
{
    /// <summary>The one instance.</summary>
    public static readonly UnassignedAcquisition Instance = new();

    private UnassignedAcquisition() { }

    /// <inheritdoc />
    public Destination Destination(IDb db) => Library.Destination.Unknown;
}
