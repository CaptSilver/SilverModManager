using System.Buffers.Binary;
using System.Runtime.InteropServices;
using NexusMods.Paths;
using SilverModManager.Sdk;
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
/// Replaces the running AppImage with a release asset once its bytes match the SHA-256 GitHub reports for it and
/// its ELF header says it runs on the CPU the app is running on.
/// </summary>
/// <remarks>
/// The download lands beside the target so the final rename stays on one filesystem and is atomic. Linux lets a
/// running executable be replaced by rename, the old inode lives on until the process exits. The CPU check does not
/// depend on the asset name, so a release that lists the wrong CPU's AppImage first cannot replace the app with it.
/// </remarks>
internal sealed class AppImageUpdateInstaller(ILogger logger, UpdateDownload download)
{
    private const UnixFileMode ExecuteBits = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    public async Task InstallAsync(Asset asset, string? appImagePath, Architecture expectedArchitecture, Action<double> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrEmpty(appImagePath))
            throw new AppImageUpdateException("The app was not started from an AppImage ($APPIMAGE is not set), so it cannot update itself. Download the update in the browser instead.");

        if (!File.Exists(appImagePath))
            throw new AppImageUpdateException($"The AppImage {MarkdownText.Code(appImagePath)} does not exist, so it cannot be replaced.");

        var expected = GetExpectedDigest(asset);

        var directory = Path.GetDirectoryName(appImagePath)
            ?? throw new AppImageUpdateException($"{MarkdownText.Code(appImagePath)} has no parent folder to download the update into.");
        var temporary = Path.Combine(directory, $".{Path.GetFileName(appImagePath)}.{Guid.NewGuid():N}.update");
        EnsureWritable(directory, appImagePath, temporary);

        try
        {
            logger.LogInformation("Downloading update `{Asset}` to `{Temporary}`", asset.Name, temporary);
            await download(new Uri(asset.BrowserDownloadUrl), temporary, progress, cancellationToken).ConfigureAwait(false);

            logger.LogInformation("Verifying download of `{Asset}`", asset.Name);
            var actual = await FileHashing.Sha256(FileSystem.Shared.FromUnsanitizedFullPath(temporary), cancellationToken).ConfigureAwait(false);

            if (!actual.Equals(expected))
            {
                logger.LogError("SHA-256 of `{Asset}` is {Actual}, GitHub reports {Expected}", asset.Name, actual, expected);
                throw new AppImageUpdateException($"The downloaded file does not match the SHA-256 GitHub reports for it (expected {MarkdownText.Code(expected)}, got {MarkdownText.Code(actual)}), so it was discarded.");
            }

            EnsureBuiltFor(expectedArchitecture, asset, temporary);

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

    private const int ElfHeaderLength = 20;
    private const int ElfMachineOffset = 18;
    private static readonly byte[] ElfMagic = [0x7f, (byte)'E', (byte)'L', (byte)'F'];

    private void EnsureBuiltFor(Architecture expected, Asset asset, string downloaded)
    {
        var expectedMachine = ReleaseAssetArchitecture.ElfMachine(expected);
        var expectedName = ReleaseAssetArchitecture.Token(expected);
        if (expectedMachine is null || expectedName is null)
            throw new AppImageUpdateException($"There is no update build for this machine's CPU ({expected}), so {MarkdownText.Code(asset.Name)} was discarded.");

        var header = new byte[ElfHeaderLength];
        int read;
        using (var stream = File.OpenRead(downloaded))
            read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);

        if (read < header.Length || !header.AsSpan(0, ElfMagic.Length).SequenceEqual(ElfMagic))
        {
            logger.LogError("`{Asset}` is not an ELF program", asset.Name);
            throw new AppImageUpdateException($"{MarkdownText.Code(asset.Name)} is not a Linux program, so it was discarded.");
        }

        var machine = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(ElfMachineOffset));
        if (machine == expectedMachine) return;

        var actualName = DescribeMachine(machine);
        logger.LogError("`{Asset}` is built for {Actual}, this app runs on {Expected}", asset.Name, actualName, expectedName);
        throw new AppImageUpdateException($"{MarkdownText.Code(asset.Name)} is built for {actualName}, this app runs on {expectedName}; the download was discarded.");
    }

    private static string DescribeMachine(ushort machine)
    {
        foreach (var architecture in new[] { Architecture.X64, Architecture.Arm64 })
        {
            if (ReleaseAssetArchitecture.ElfMachine(architecture) == machine && ReleaseAssetArchitecture.Token(architecture) is { } name) return name;
        }

        return $"another CPU (ELF machine 0x{machine:x})";
    }

    private static Sha256Value GetExpectedDigest(Asset asset)
    {
        try
        {
            return asset.GetSha256()
                ?? throw new AppImageUpdateException($"GitHub reports no SHA-256 for {MarkdownText.Code(asset.Name)}, so the download could not be verified and was not started.");
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
            throw new AppImageUpdateException($"The folder {MarkdownText.Code(directory)} is not writable, so {MarkdownText.Code(Path.GetFileName(appImagePath))} cannot be replaced: {MarkdownText.Message(e)}");
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
