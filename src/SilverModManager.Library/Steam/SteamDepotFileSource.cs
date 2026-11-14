using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Steam;
using SilverModManager.Abstractions.Steam.DTOs;
using SilverModManager.Abstractions.Steam.Values;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.Steam;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Library.Steam;

/// <summary>
/// Takes files out of Steam depot manifests: one file's bytes into the library, or a manifest's file hashes without
/// any bytes. Fetching bytes always goes through the user's Steam login, because the chunks are encrypted with a
/// depot key only a logged-in session can get; reading hashes can be done from the manifest Steam already cached.
/// </summary>
public sealed class SteamDepotFileSource(
    IServiceProvider provider,
    ISteamSession session,
    IDepotManifestSource manifests,
    ISteamDepotLocations locations,
    ISettingsManager settings) : ISteamDepotFiles
{
    /// <summary>
    /// Fetches one file of a manifest and adds it to the library with the manifest it came from. The bytes are checked
    /// against the SHA-1 the manifest lists (and against <paramref name="expected"/> when given); a file that does not
    /// match is never added.
    /// </summary>
    /// <param name="expected">The SHA-1 the caller was promised, or null to trust the manifest's own.</param>
    /// <param name="handler">Shows the QR code if the login has to be made now; null uses the registered handler.</param>
    /// <exception cref="SteamDepotFileNotFoundException">The manifest lists no such file.</exception>
    /// <exception cref="SteamDepotIntegrityException">The manifest or the downloaded bytes do not have the expected SHA-1.</exception>
    public async Task<LibraryFile.ReadOnly> FetchFile(
        AppId app,
        DepotId depot,
        ManifestId manifestId,
        RelativePath path,
        Sha1Value? expected,
        IAcquisitionContext acquisition,
        CancellationToken cancellationToken,
        IAuthInterventionHandler? handler = null)
    {
        await Connect(handler, cancellationToken);
        var manifest = await session.GetManifestContents(app, depot, manifestId, settings.Get<SteamDepotSettings>().Branch, cancellationToken);

        // A manifest built on Windows may spell a path in another case than the caller does.
        var entry = manifest.Files.FirstOrDefault(file => string.Equals(file.Path.ToString(), path.ToString(), StringComparison.OrdinalIgnoreCase))
            ?? throw new SteamDepotFileNotFoundException($"Manifest {manifestId.Value} of depot {depot.Value} lists no file {MarkdownText.Code(path.ToString())}.");

        var wanted = expected ?? entry.Hash;
        if (!entry.Hash.Equals(wanted))
            throw new SteamDepotIntegrityException($"Manifest {manifestId.Value} lists SHA-1 {entry.Hash} for {MarkdownText.Code(path.ToString())}, not the expected {wanted}.");

        var temporaryFileManager = provider.GetRequiredService<TemporaryFileManager>();
        await using var folder = temporaryFileManager.CreateFolder();
        var downloaded = folder.Path.Combine(entry.Path.FileName);

        await using (var source = session.GetFileStream(app, manifest, entry.Path))
        await using (var destination = downloaded.Create())
        {
            await source.CopyToAsync(destination, cancellationToken);
        }

        var actual = await FileHashing.Digest<Sha1Value, SHA1, Sha1Hasher>(downloaded, cancellationToken);

        if (!actual.Equals(wanted))
            throw new SteamDepotIntegrityException($"SHA-1 mismatch for {MarkdownText.Code(path.ToString())} from Steam: the manifest lists {wanted}, the download has {actual}.");

        using var tx = provider.GetRequiredService<IConnection>().BeginTransaction();
        var libraryFile = await AddLibraryFileJob.Create(provider, tx, downloaded, acquisition);
        var model = new SteamDepotLibraryFile.New(tx, libraryFile.Id)
        {
            LibraryFile = libraryFile,
            AppId = app.Value,
            DepotId = depot.Value,
            ManifestId = manifestId.Value,
            Path = entry.Path.ToString(),
            Sha1 = actual,
        };

        var result = await tx.Commit();
        return result.Remap(model).AsLibraryFile();
    }

    /// <summary>
    /// The SHA-1 of every file in a manifest, so a game file the user already has can be recognised as that build's own.
    /// Steam's own copy of the manifest answers when it is cached (its file names may be encrypted, the hashes are not),
    /// without a login; otherwise the manifest is fetched through the login.
    /// </summary>
    public async Task<ManifestFileHashes> VouchManifest(
        AppId app,
        DepotId depot,
        ManifestId manifestId,
        CancellationToken cancellationToken,
        IAuthInterventionHandler? handler = null)
    {
        foreach (var location in locations.For(app))
        {
            if (!manifests.TryGetManifestPath(location.SteamRoot, manifestId, location.InstallPath, out var cached)) continue;
            if (manifests.TryReadFileHashes(cached) is { } hashes && hashes.DepotId == depot) return hashes;
        }

        await Connect(handler, cancellationToken);
        var manifest = await session.GetManifestContents(app, depot, manifestId, settings.Get<SteamDepotSettings>().Branch, cancellationToken);
        return new ManifestFileHashes(manifest.DepotId, manifest.ManifestId, manifest.Files.Select(file => file.Hash).ToHashSet());
    }

    private Task Connect(IAuthInterventionHandler? handler, CancellationToken cancellationToken)
        => handler is null ? session.Connect(cancellationToken) : session.Connect(handler, cancellationToken);
}
