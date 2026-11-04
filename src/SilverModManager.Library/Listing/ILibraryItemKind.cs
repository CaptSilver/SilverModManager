using JetBrains.Annotations;
using SilverModManager.Sdk.Library;

namespace SilverModManager.Library.Listing;

/// <summary>
/// The names <c>library list</c> gives the kinds of library item.
/// </summary>
[PublicAPI]
public static class LibraryItemKinds
{
    /// <summary>A file of a Nexus Mods mod page.</summary>
    public const string NexusModFile = "nexus-mod-file";

    /// <summary>A file downloaded from some other host.</summary>
    public const string DownloadedFile = "downloaded-file";

    /// <summary>A file the user added from disk.</summary>
    public const string LocalFile = "local-file";

    /// <summary>A Wabbajack modlist.</summary>
    public const string WabbajackModlist = "wabbajack-modlist";

    /// <summary>Everything else: GitHub and Thunderstore files, collections, archive entries.</summary>
    public const string Other = "other";
}

/// <summary>
/// What an <see cref="ILibraryItemKind"/> recognised: the kind and, when the model has one, the version.
/// </summary>
[PublicAPI]
public sealed record LibraryItemKindMatch(string Kind, string? Version);

/// <summary>
/// Recognises a kind of library item whose model lives in a project the library cannot reference, such as Wabbajack
/// modlists. <see cref="LibraryListing"/> asks these before its own rules.
/// </summary>
[PublicAPI]
public interface ILibraryItemKind
{
    /// <summary>
    /// The match for the item, or null when this is not an item of this kind.
    /// </summary>
    LibraryItemKindMatch? Match(LibraryItem.ReadOnly item);
}
