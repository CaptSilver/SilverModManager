namespace NexusMods.Games.Bethesda.Archives;

/// <summary>
/// Writes an archive of the kind its <see cref="ArchiveState"/> describes, from the recorded state of each
/// member and its bytes. Disposing it deletes the temporary files it staged the members in.
/// </summary>
public interface IBethesdaArchiveBuilder : IAsyncDisposable
{
    /// <summary>
    /// Stages one member. The stream is read from its current position to its end, compressed or cut into
    /// chunks as the file state says, and not kept: the caller may dispose it once the task completes. Calls
    /// may run concurrently. A file state of another archive kind, or data that does not fit the state,
    /// throws a <see cref="BethesdaArchiveException"/>.
    /// </summary>
    Task AddFile(FileState state, Stream data, CancellationToken cancellationToken);

    /// <summary>
    /// Writes the archive to <paramref name="output"/>, which the builder does not dispose.
    /// </summary>
    Task Build(Stream output, CancellationToken cancellationToken);
}
