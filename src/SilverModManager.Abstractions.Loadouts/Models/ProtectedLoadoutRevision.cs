using JetBrains.Annotations;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.BuiltInEntities;
using SilverModManager.MnemonicDB.Abstractions.Models;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Loadouts;

/// <summary>
/// A loadout revision that garbage collection must keep reachable, so that reverting to it still finds the files it
/// referenced. Removed when whatever asked for the protection is finished with it.
/// </summary>
[PublicAPI]
public partial class ProtectedLoadoutRevision : IModelDefinition
{
    private const string Namespace = "SilverModManager.Loadouts.ProtectedLoadoutRevision";

    /// <summary>The loadout the revision belongs to.</summary>
    public static readonly ReferenceAttribute<Loadout> Loadout = new(Namespace, nameof(Loadout)) { IsIndexed = true };

    /// <summary>The transaction whose state is protected.</summary>
    public static readonly ReferenceAttribute<Transaction> Revision = new(Namespace, nameof(Revision));

    /// <summary>Why it is protected, in words a person can read.</summary>
    public static readonly StringAttribute Reason = new(Namespace, nameof(Reason));
}
