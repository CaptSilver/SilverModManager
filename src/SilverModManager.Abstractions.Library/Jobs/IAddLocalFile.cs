using NexusMods.Paths;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Library;

namespace SilverModManager.Abstractions.Library.Jobs;

/// <summary>
/// A job that adds a local file to the library
/// </summary>
public interface IAddLocalFile : IJobDefinition<LocalFile.ReadOnly>
{
    /// <summary>
    /// The source file path
    /// </summary>
    public AbsolutePath FilePath { get; }
}
