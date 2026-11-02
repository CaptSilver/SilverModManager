using JetBrains.Annotations;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;

namespace SilverModManager.Wabbajack.Models;

/// <summary>
/// A file an install wrote for a directive of a modlist. The loadout row holds the xxHash3 of the stored bytes, which a
/// directive cannot be compared with before its bytes are produced; this records the xxHash64 the list gives that
/// directive, so a later run can tell the directive is already done without locating its archive again.
/// </summary>
[PublicAPI]
[Include<LoadoutFile>]
public partial class WabbajackInstalledFile : IModelDefinition
{
    private const string Namespace = "SilverModManager.Wabbajack.InstalledFile";

    /// <summary>
    /// The xxHash64 identity the list records for the directive, not the one of the bytes written: a recompressed texture or a
    /// rebuilt archive legitimately differs from it. A changed list records another value, so its file is written again.
    /// </summary>
    public static readonly FileIdentityAttribute ListHash = new(Namespace, nameof(ListHash));
}
