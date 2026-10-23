using JetBrains.Annotations;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk.Library;

namespace SilverModManager.Abstractions.Library;

/// <summary>
/// Looks at a file as it is being added to the library and can attach a model that says what it is.
/// </summary>
[PublicAPI]
public interface ILibraryFileRecogniser
{
    /// <summary>
    /// Called once for each top-level file, before the transaction commits. When the file is an archive,
    /// <paramref name="extractedFolder"/> holds its extracted members and exists only for the duration of the
    /// call: the members' rows are in <paramref name="tx"/> and not in the file store yet, so read the files
    /// from the folder. It is null for a file that is not an archive.
    /// </summary>
    ValueTask Recognise(ITransaction tx, LibraryFile.New file, AbsolutePath? extractedFolder, CancellationToken cancellationToken);
}
