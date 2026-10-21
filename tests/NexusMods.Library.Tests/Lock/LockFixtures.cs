using System.Security.Cryptography;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.NexusModsLibrary;
using NexusMods.Abstractions.NexusWebApi.Types;
using NexusMods.Hashing.xxHash3;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Abstractions.ElementComparers;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Hashes;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Loadouts;
using NexusMods.Sdk.NexusModsApi;

namespace NexusMods.Library.Tests.Lock;

/// <summary>
/// Builds loadouts and library rows for the lock tests, with the digests chosen by the test.
/// </summary>
internal sealed class LockFixtures(IConnection connection)
{
    public static readonly NexusModsGameId GameId = NexusModsGameId.From(4333);

    public static Sha256Value DigestOf(string content) => Sha256Value.From(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)));

    public async Task<Loadout.ReadOnly> CreateLoadout()
    {
        using var tx = connection.BeginTransaction();
        var metadata = new GameInstallMetadata.New(tx)
        {
            Path = "/tmp/lock-tests-" + Guid.NewGuid(),
            Name = "Lock Test Game",
            Store = GameStore.Unknown,
            GameId = GameId,
        };
        var loadout = new Loadout.New(tx)
        {
            Name = "Lock " + Guid.NewGuid(),
            ShortName = "LK",
            InstallationId = metadata,
            LoadoutKind = LoadoutKind.Default,
            Revision = 0,
            GameVersion = VanityVersion.From("Unknown"),
        };
        var result = await tx.Commit();
        return result.Remap(loadout);
    }

    /// <summary>
    /// Adds a library file. <paramref name="content"/> decides the stored hash and size; <paramref name="sha256"/> is
    /// stored as given, so a test can record a digest that does not describe <paramref name="content"/>, or none.
    /// </summary>
    public async Task<LibraryFile.ReadOnly> AddLibraryFile(string fileName, string content, Sha256Value? sha256, Action<ITransaction, LibraryFile.New>? extend = null)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        using var tx = connection.BeginTransaction();
        var file = new LibraryFile.New(tx, out var id)
        {
            FileName = fileName,
            Hash = bytes.xxHash3(),
            Size = Size.From((ulong)bytes.Length),
            LibraryItem = new LibraryItem.New(tx, id) { Name = fileName },
        };
        if (sha256 is { } digest) tx.Add(id, LibraryFile.Sha256, digest);
        extend?.Invoke(tx, file);
        var result = await tx.Commit();
        return result.Remap(file);
    }

    public Task<LibraryFile.ReadOnly> AddNexusFile(string fileName, string content, Sha256Value? sha256, string modName, string version, uint modId, uint fileId)
    {
        return AddLibraryFile(fileName, content, sha256, (tx, file) =>
        {
            var id = file.Id;
            var page = new NexusModsModPageMetadata.New(tx)
            {
                Uid = new ModUid(ModId.From(modId), GameId),
                Name = modName,
                GameDomain = GameDomain.From("eldenring"),
                UpdatedAt = DateTimeOffset.UnixEpoch.UtcDateTime,
                DataUpdatedAt = DateTimeOffset.UnixEpoch.UtcDateTime,
            };
            var metadata = new NexusModsFileMetadata.New(tx)
            {
                Uid = new FileUid(FileId.From(fileId), GameId),
                Name = fileName,
                Version = version,
                UploadedAt = DateTimeOffset.UnixEpoch.UtcDateTime,
                ModPageId = page,
            };
            tx.Add(id, NexusModsLibraryItem.FileMetadataId, metadata.Id);
            tx.Add(id, NexusModsLibraryItem.ModPageMetadataId, page.Id);
        });
    }

    public Task<LibraryFile.ReadOnly> AddGitHubFile(string assetName, string content, Sha256Value pinned, string tag, string organization = "owner", string repository = "tool", ulong? repositoryId = null)
    {
        return AddLibraryFile(assetName, content, pinned, (tx, file) =>
        {
            var id = file.Id;
            _ = new GitHubReleaseLibraryFile.New(tx, id)
            {
                LibraryFile = file,
                Tag = tag,
                AssetName = assetName,
                PinnedSha256 = pinned,
            };
            if (repositoryId is { } repositoryNumber)
            {
                tx.Add(id, GitHubReleaseLibraryFile.RepositoryId, repositoryNumber);
                return;
            }

            tx.Add(id, GitHubReleaseLibraryFile.Organization, organization);
            tx.Add(id, GitHubReleaseLibraryFile.Repository, repository);
        });
    }

    public async Task Link(Loadout.ReadOnly loadout, LibraryFile.ReadOnly file, string groupName, bool disabled = false)
    {
        using var tx = connection.BeginTransaction();
        var group = new LoadoutItemGroup.New(tx, out var id)
        {
            IsGroup = true,
            LoadoutItem = new LoadoutItem.New(tx, id) { Name = groupName, LoadoutId = loadout.LoadoutId },
        };
        _ = new LibraryLinkedLoadoutItem.New(tx, id)
        {
            LibraryItemId = file.Id,
            LoadoutItemGroup = group,
        };
        if (disabled) tx.Add(id, LoadoutItem.Disabled, Null.Instance);
        await tx.Commit();
    }
}
