using JetBrains.Annotations;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Thunderstore;

namespace SilverModManager.Networking.Thunderstore;

/// <summary>
/// A newer version of a package that is in the library.
/// </summary>
/// <param name="LibraryFileId">The library file holding <paramref name="Current"/>.</param>
/// <param name="Current">The version in the library.</param>
/// <param name="Newest">The newest active version in the catalog.</param>
[PublicAPI]
public readonly record struct ThunderstoreUpdate(EntityId LibraryFileId, ThunderstoreVersionRef Current, ThunderstoreVersionRef Newest);
