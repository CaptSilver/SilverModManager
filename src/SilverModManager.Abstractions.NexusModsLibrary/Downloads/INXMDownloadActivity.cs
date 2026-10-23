using JetBrains.Annotations;
using SilverModManager.Abstractions.Downloads;

namespace SilverModManager.Abstractions.NexusModsLibrary;

/// <summary>
/// Represents an NXM download.
/// </summary>
[PublicAPI]
public interface INXMDownloadJob : IDownloadJob
{
    NexusModsFileMetadata.ReadOnly FileMetadata { get; }
}
