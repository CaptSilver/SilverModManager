using JetBrains.Annotations;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Thunderstore;

namespace NexusMods.Networking.Thunderstore;

/// <summary>
/// A newer version of a package that is in the library.
/// </summary>
/// <param name="LibraryFileId">The library file holding <paramref name="Current"/>.</param>
/// <param name="Current">The version in the library.</param>
/// <param name="Newest">The newest active version in the catalog.</param>
[PublicAPI]
public readonly record struct ThunderstoreUpdate(EntityId LibraryFileId, ThunderstoreVersionRef Current, ThunderstoreVersionRef Newest);
