using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Hashing.xxHash3;
using SilverModManager.Sdk;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.Settings;
using NSubstitute;

namespace SilverModManager.Deployment.Vfs.Tests;

/// <summary>
/// Sessions come out of the factory with the staging location and size cap from <see cref="VfsSettings"/>
/// and the loadout the caller names; nothing is read from anywhere else.
/// </summary>
public class VfsSessionFactoryTests(IServiceProvider serviceProvider) : IDisposable
{
    private const long MiB = 1024 * 1024;
    private static readonly LoadoutId Target = LoadoutId.From(0x77);
    private static readonly GameInstallMetadataId Installation = GameInstallMetadataId.From(0xABC);

    private static readonly string Name = $"vfs-session-factory-tests-{Guid.NewGuid():N}";

    private readonly AbsolutePath _directory = NexusMods.Paths.FileSystem.Shared
        .GetKnownPath(KnownPath.TempDirectory)
        .Combine(Name);

    public void Dispose()
    {
        if (_directory.DirectoryExists()) _directory.DeleteDirectory(recursive: true);
    }

    private VfsSessionFactory NewFactory(RecordingSink sink)
    {
        var settings = Substitute.For<ISettingsManager>();
        settings.Get<VfsSettings>(Arg.Any<string?>()).Returns(new VfsSettings
        {
            StagingLocation = new ConfigurablePath(KnownPath.TempDirectory, Name),
            PromotionSizeCapBytes = 1 * MiB,
        });
        var promoters = new StagingPromoterFactory(sink, new SizeHasher(), settings, NullLoggerFactory.Instance, NexusMods.Paths.FileSystem.Shared);
        return new VfsSessionFactory(promoters, settings, NexusMods.Paths.FileSystem.Shared);
    }

    [Fact]
    public async Task CreateUsesTheSettingsStagingLocationAndCap()
    {
        var sink = new RecordingSink();
        var factory = NewFactory(sink);
        var tree = VfsTree.Build(_directory.Combine("game"), [], [], VfsCachePolicy.Default);

        var session = factory.Create(Installation, Target, tree);

        session.Tree.Should().BeSameAs(tree);
        session.Staging.Installation.Should().Be(Installation);
        session.Staging.Root.Parent.Should().Be(_directory, "the staging directory sits under the configured location");

        var file = session.Staging.PathFor(RelativePath.FromUnsanitizedInput("Data/big.bin"));
        file.Parent.CreateDirectory();
        File.WriteAllBytes(file.ToString(), new byte[2 * MiB]);

        var result = await session.FinishSession();

        result.Status.Should().Be(PromotionStatus.OverCap);
        sink.Calls.Should().BeEmpty("an over-cap session promotes nothing");
        file.FileExists.Should().BeTrue();
    }

    [Fact]
    public async Task CreatePromotesIntoTheLoadoutItWasGiven()
    {
        var sink = new RecordingSink();
        var session = NewFactory(sink).Create(Installation, Target, VfsTree.Build(_directory.Combine("game"), [], [], VfsCachePolicy.Default));
        var file = session.Staging.PathFor(RelativePath.FromUnsanitizedInput("Data/small.txt"));
        file.Parent.CreateDirectory();
        File.WriteAllText(file.ToString(), "small");

        var result = await session.FinishSession();

        result.Status.Should().Be(PromotionStatus.Promoted);
        sink.Loadouts.Should().Equal(Target);
    }

    [Fact]
    public void ServicesRegisterTheFactory()
    {
        new ServiceCollection().AddVfsCore().Should().ContainSingle(d => d.ServiceType == typeof(VfsSessionFactory));

        serviceProvider.GetRequiredService<VfsSessionFactory>().Should().BeSameAs(serviceProvider.GetRequiredService<VfsSessionFactory>());
    }

    private sealed class SizeHasher : IFileHasher
    {
        public ValueTask<Hash> Hash(GameInstallMetadataId installation, AbsolutePath file, CancellationToken token = default)
            => ValueTask.FromResult(NexusMods.Hashing.xxHash3.Hash.From((ulong)new FileInfo(file.ToString()).Length));
    }

    private sealed class RecordingSink : IPromotionSink
    {
        public List<string> Calls { get; } = [];
        public List<LoadoutId> Loadouts { get; } = [];

        public Task Backup(IReadOnlyList<(Hash Hash, Size Size, IStreamFactory Stream)> files, CancellationToken token = default)
        {
            Calls.Add("backup");
            return Task.CompletedTask;
        }

        public Task Record(LoadoutId loadout, IReadOnlyList<PromotedFile> files, IReadOnlyList<RelativePath> deleted, CancellationToken token = default)
        {
            Calls.Add("record");
            Loadouts.Add(loadout);
            return Task.CompletedTask;
        }
    }
}
