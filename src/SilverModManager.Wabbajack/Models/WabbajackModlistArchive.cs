using JetBrains.Annotations;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Library;

namespace SilverModManager.Wabbajack.Models;

/// <summary>
/// One archive a modlist names, written when the list is added. The hash joins it to the library file that holds the
/// archive; <see cref="FetchedFiles"/> says whether the library gained that file because this list's plan wanted it,
/// which is what separates the list's own downloads from files the user fetched for themselves.
/// </summary>
[PublicAPI]
public partial class WabbajackModlistArchive : IModelDefinition
{
    private const string Namespace = "SilverModManager.Wabbajack.ModlistArchive";

    /// <summary>The list that names the archive.</summary>
    public static readonly ReferenceAttribute<WabbajackModlistLibraryFile> Modlist = new(Namespace, nameof(Modlist)) { IsIndexed = true };

    /// <summary>The xxHash64 the list names the archive by; it matches the library file's <see cref="LibraryFile.XxHash64"/>.</summary>
    public static readonly FileIdentityAttribute Identity = new(Namespace, nameof(Identity)) { IsIndexed = true };

    public static readonly SizeAttribute Size = new(Namespace, nameof(Size));

    public static readonly StringAttribute Name = new(Namespace, nameof(Name));

    /// <summary>
    /// The library file the app fetched for this list: downloaded by the acquirer, matched from the inbox, or
    /// completed by the nxm download the plan was expecting. Empty for an archive the user already had.
    /// </summary>
    /// <remarks>
    /// A collection of at most one file, on purpose. A single-valued reference makes removing the file delete this whole
    /// row (see <c>ITransaction.Delete</c> with <c>recursive</c>), which would lose the archive the list still names.
    /// A collection only has the reference retracted.
    /// </remarks>
    public static readonly ReferencesAttribute<LibraryFile> FetchedFiles = new(Namespace, nameof(FetchedFiles)) { IsIndexed = true };
}
