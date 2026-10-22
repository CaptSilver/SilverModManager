using NexusMods.Sdk.Library;

namespace NexusMods.Abstractions.Library;

/// <summary>
/// Adds a Thunderstore package to the library, with the packages it depends on. It exists so the UI and the
/// lock can ask for a package without referencing the project that downloads one.
/// </summary>
public interface IThunderstoreSource
{
    /// <summary>
    /// Downloads the requested version and, if asked, the dependencies it needs, and adds them to the library.
    /// Returns the library files in install order, dependencies before dependents and the root last; a version
    /// the library already holds is returned as it is and not downloaded again, and a package listed in
    /// <see cref="ThunderstoreInstallRequest.InstalledInLoadout"/> is left out. Throws when a package cannot be
    /// resolved or a download does not verify; files added before the failure stay in the library.
    /// </summary>
    ValueTask<IReadOnlyList<LibraryFile.ReadOnly>> AddAsync(ThunderstoreInstallRequest request, CancellationToken cancellationToken);
}
