using SilverModManager.Sdk.Errors;
using Microsoft.Extensions.Logging;
using SilverModManager.Networking.GitHub;
using SilverModManager.Networking.GitHub.DTOs;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.App.UI.Overlays.Updater;

/// <summary>
/// Fetches <paramref name="source"/> into <paramref name="destination"/>, reporting progress as 0 to 1.
/// </summary>
internal delegate Task UpdateDownload(Uri source, string destination, Action<double> progress, CancellationToken cancellationToken);

/// <summary>
/// The update could not be installed; the message is meant for the user.
/// </summary>
internal sealed class AppImageUpdateException(string message) : UserFacingException(message)
{
    /// <inheritdoc />
    public override string Title => "The update was not installed";
}

/// <summary>
/// Replaces the running AppImage with a release asset once its bytes match the SHA-256 GitHub reports for it.
/// </summary>
/// <remarks>
/// The download lands beside the target so the final rename stays on one filesystem and is atomic. Linux lets a
/// running executable be replaced by rename, the old inode lives on until the process exits.
/// </remarks>
internal sealed class AppImageUpdateInstaller(ILogger logger, UpdateDownload download)
{
    private const UnixFileMode ExecuteBits = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    public async Task InstallAsync(Asset asset, string? appImagePath, Action<double> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrEmpty(appImagePath))
            throw new AppImageUpdateException("The app was not started from an AppImage ($APPIMAGE is not set), so it cannot update itself. Download the update in the browser instead.");

        if (!File.Exists(appImagePath))
            throw new AppImageUpdateException($"The AppImage `{appImagePath}` does not exist, so it cannot be replaced.");

        var expected = GetExpectedDigest(asset);

        var directory = Path.GetDirectoryName(appImagePath)
            ?? throw new AppImageUpdateException($"`{appImagePath}` has no parent folder to download the update into.");
        var temporary = Path.Combine(directory, $".{Path.GetFileName(appImagePath)}.{Guid.NewGuid():N}.update");
        EnsureWritable(directory, appImagePath, temporary);

        try
        {
            logger.LogInformation("Downloading update `{Asset}` to `{Temporary}`", asset.Name, temporary);
            await download(new Uri(asset.BrowserDownloadUrl), temporary, progress, cancellationToken).ConfigureAwait(false);

            logger.LogInformation("Verifying download of `{Asset}`", asset.Name);
            Sha256Value actual;
            await using (var stream = File.OpenRead(temporary))
            {
                actual = await Sha256Hasher.HashAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            if (!actual.Equals(expected))
            {
                logger.LogError("SHA-256 of `{Asset}` is {Actual}, GitHub reports {Expected}", asset.Name, actual, expected);
                throw new AppImageUpdateException($"The downloaded file does not match the SHA-256 GitHub reports for it (expected {expected}, got {actual}), so it was discarded.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            File.SetUnixFileMode(temporary, File.GetUnixFileMode(appImagePath) | ExecuteBits);
            logger.LogInformation("Replacing `{AppImage}` with the verified download", appImagePath);
            File.Move(temporary, appImagePath, overwrite: true);
        }
        finally
        {
            // After a successful rename there is nothing left under this name.
            TryDelete(temporary);
        }
    }

    private static Sha256Value GetExpectedDigest(Asset asset)
    {
        try
        {
            return asset.GetSha256()
                ?? throw new AppImageUpdateException($"GitHub reports no SHA-256 for `{asset.Name}`, so the download could not be verified and was not started.");
        }
        catch (FormatException e)
        {
            throw new AppImageUpdateException(e.Message);
        }
    }

    private static void EnsureWritable(string directory, string appImagePath, string temporary)
    {
        try
        {
            // Creating the file is the only check that also covers read-only mounts and ACLs.
            using (File.Create(temporary)) { }
            File.Delete(temporary);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            throw new AppImageUpdateException($"The folder `{directory}` is not writable, so `{Path.GetFileName(appImagePath)}` cannot be replaced: {e.Message}");
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Failed to remove the temporary update file `{Path}`", path);
        }
    }
}
