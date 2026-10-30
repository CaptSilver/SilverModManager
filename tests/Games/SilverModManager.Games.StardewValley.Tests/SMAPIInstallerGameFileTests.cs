using System.Collections.Frozen;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Abstractions.Games.FileHashes.Models;
using SilverModManager.Games.StardewValley.Installers;
using SilverModManager.Sdk;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.IO;

namespace SilverModManager.Games.StardewValley.Tests;

public class SMAPIInstallerGameFileTests : IDisposable
{
    private static readonly byte[] Content = Encoding.UTF8.GetBytes("{ \"deps\": 1 }");
    private static readonly GamePath DepsJson = new(LocationId.Game, "Stardew Valley.deps.json");

    private readonly TemporaryFileManager _temp = new(FileSystem.Shared);
    private readonly IFileStore _store = Substitute.For<IFileStore>();
    private readonly IFileIdentityResolver _resolver = Substitute.For<IFileIdentityResolver>();
    private readonly IFileHashesService _hashes = Substitute.For<IFileHashesService>();
    private readonly IFullFileHasher _hasher = Substitute.For<IFullFileHasher>();

    public void Dispose() => _temp.Dispose();

    private SMAPIInstaller Installer()
    {
        var services = new ServiceCollection()
            .AddSingleton(_resolver)
            .AddSingleton(_hasher)
            .BuildServiceProvider();
        return new SMAPIInstaller(services, NullLogger<SMAPIInstaller>.Instance, _temp, _hashes, _store, Substitute.For<IStreamSourceDispatcher>());
    }

    private static Hash XxHash3Of(byte[] bytes) => MultiHashOf(bytes).AsTask().GetAwaiter().GetResult().XxHash3;

    private static async ValueTask<MultiHash> MultiHashOf(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        return await MultiHasher.HashStream(stream);
    }

    private static FileIdentity Sha1Of(byte[] bytes) => FileIdentity.Sha1(Sha1Value.From(System.Security.Cryptography.SHA1.HashData(bytes)));

    private static GameFileRecord Record(FileIdentity identity) => new() { Path = DepsJson, Size = Size.FromLong(Content.Length), Identity = identity };

    [Fact]
    public async Task FromRecord_Sha1RecordWithARecordedRelation_UsesTheStoreKeyWithoutOpeningTheFile()
    {
        var sha1 = Sha1Of(Content);
        var key = FileIdentity.XxHash3(XxHash3Of(Content));
        _resolver.StoreKeyCandidates(sha1, Record(sha1).Size).Returns([key]);
        _store.HaveFile(key).Returns(true);

        // The file is not on disk: a read would have nothing to open and the answer would be null.
        var missing = _temp.CreateFile().Path.Parent / "never-written.json";

        var source = await Installer().FromRecord(Record(sha1), missing, null!, TestContext.Current.CancellationToken);

        source.Should().NotBeNull();
        source!.Value.Identity.Should().Be(key);
        source.Value.OnDisk.Should().BeNull("a relation answers without a read, so there is no disk copy to archive from");
        await _hasher.DidNotReceiveWithAnyArgs().HashAll(default, default);
    }

    [Fact]
    public async Task FromRecord_Sha1RecordNoRelationReaches_ReadsTheFileOnce()
    {
        var sha1 = Sha1Of(Content);
        _resolver.StoreKeyCandidates(sha1, Record(sha1).Size).Returns([]);
        _hashes.AttestedFiles(Arg.Any<GameInstallation>()).Returns(FrozenDictionary<GamePath, AttestedLookup>.Empty);

        await using var file = _temp.CreateFile();
        await File.WriteAllBytesAsync(file.Path.ToString(), Content, TestContext.Current.CancellationToken);
        _hasher.HashAll(file.Path, Arg.Any<CancellationToken>()).Returns(_ => MultiHashOf(Content));

        var source = await Installer().FromRecord(Record(sha1), file.Path, null!, TestContext.Current.CancellationToken);

        source.Should().NotBeNull();
        source!.Value.Identity.Should().Be(FileIdentity.XxHash3(XxHash3Of(Content)));
        source.Value.OnDisk.Should().Be(file.Path);
        await _hasher.Received().HashAll(file.Path, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FromRecord_Sha1RecordWithNoRelationAndNoFile_IsNull()
    {
        var sha1 = Sha1Of(Content);
        _resolver.StoreKeyCandidates(sha1, Record(sha1).Size).Returns([]);

        var source = await Installer().FromRecord(Record(sha1), _temp.CreateFile().Path.Parent / "gone.json", null!, TestContext.Current.CancellationToken);

        source.Should().BeNull();
    }
}
