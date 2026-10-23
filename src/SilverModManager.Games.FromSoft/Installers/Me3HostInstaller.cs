using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Library.Installers;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Games.FromSoft.Me3;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.FromSoft.Installers;

/// <summary>
/// Installs the me3 Linux release (<c>me3-linux-amd64.tar.gz</c>) into <see cref="LocationId.Me3Host"/>, so the
/// version in use travels with the loadout. Only the three files me3 needs at run time are kept: the binary and the
/// two Windows components it loads into the game. The release's licence, readme and shell completions are dropped.
/// The Windows zip has no <c>bin/me3</c> and is refused.
/// </summary>
public class Me3HostInstaller : ALibraryArchiveInstaller
{
    public Me3HostInstaller(IServiceProvider serviceProvider)
        : base(serviceProvider, serviceProvider.GetRequiredService<ILogger<Me3HostInstaller>>())
    {
    }

    /// <summary>True when the archive has every file of the Linux release, so installing it cannot be refused.</summary>
    public static bool Accepts(LibraryArchive.ReadOnly archive)
    {
        var files = Me3InstallerFiles.Files(archive).Select(f => NormalizePath(f.Path)).ToHashSet(StringComparer.Ordinal);
        return Me3HostFiles.ReleaseFiles.All(files.Contains);
    }

    private static string NormalizePath(string path) => path.StartsWith("./", StringComparison.Ordinal) ? path[2..] : path;

    public override ValueTask<InstallerResult> ExecuteAsync(
        LibraryArchive.ReadOnly libraryArchive,
        LoadoutItemGroup.New loadoutGroup,
        ITransaction tx,
        Loadout.ReadOnly loadout,
        CancellationToken cancellationToken)
    {
        var files = Me3InstallerFiles.Files(libraryArchive);
        var wanted = new List<(Me3InstallerFiles.ArchiveFile File, string Path)>();
        foreach (var path in Me3HostFiles.ReleaseFiles)
        {
            var file = files.FirstOrDefault(f => NormalizePath(f.Path).Equals(path, StringComparison.Ordinal));
            if (file.Path is null)
            {
                return ValueTask.FromResult<InstallerResult>(new NotSupported(Reason: path == Me3HostFiles.Binary
                    ? "This is not the me3 Linux release (no bin/me3); the Windows zip cannot be used here"
                    : $"The me3 release has no {path}, which me3 loads into the game, so it would not start."));
            }

            wanted.Add((file, path));
        }

        foreach (var (file, path) in wanted)
            Me3InstallerFiles.AddFile(tx, loadout, loadoutGroup, file, LocationId.Me3Host, path, path);

        return ValueTask.FromResult<InstallerResult>(new Success());
    }
}
