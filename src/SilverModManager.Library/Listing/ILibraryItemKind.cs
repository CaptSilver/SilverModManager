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
/// What an <see cref="ILibraryItemKind"/> recognised: the kind and, when the model has them, the version and the
/// source details <c>library list</c> prints.
/// </summary>
[PublicAPI]
public sealed record LibraryItemKindMatch(string Kind, string? Version = null)
{
    /// <summary>The host a downloaded file came from.</summary>
    public string? Host { get; init; }

    /// <summary>The Nexus Mods game, mod and file ids.</summary>
    public uint? NexusGameId { get; init; }

    /// <inheritdoc cref="NexusGameId"/>
    public uint? NexusModId { get; init; }

    /// <inheritdoc cref="NexusGameId"/>
    public uint? NexusFileId { get; init; }

    /// <summary>The library's mod page row and its name.</summary>
    public string? ModPageId { get; init; }

    /// <inheritdoc cref="ModPageId"/>
    public string? ModPageName { get; init; }

    /// <summary>The mod page's thumbnail.</summary>
    public string? ThumbnailUri { get; init; }
}

/// <summary>
/// Recognises one kind of library item, one implementation per source. <see cref="LibraryListing"/> asks them in
/// <see cref="Order"/> and takes the first match; an item no kind matches is listed as <see cref="LibraryItemKinds.Other"/>.
/// A source whose model lives in a project the library cannot reference, such as Wabbajack modlists, registers its own.
/// </summary>
[PublicAPI]
public interface ILibraryItemKind
{
    /// <summary>
    /// Where this kind is asked among the others, lowest first. Kinds that are more specific than a plain download
    /// (a Wabbajack modlist is also a downloaded file) come before it.
    /// </summary>
    int Order => 0;

    /// <summary>
    /// The match for the item, or null when this is not an item of this kind.
    /// </summary>
    LibraryItemKindMatch? Match(LibraryItem.ReadOnly item);
}
