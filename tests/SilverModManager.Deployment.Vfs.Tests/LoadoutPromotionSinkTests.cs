using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.TestFramework;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Deployment.Vfs.Tests;

/// <summary>
/// Promotes into a real loadout over a stubbed game: the rows the Overrides group ends up with,
/// and that running the same promotion again adds none.
/// </summary>
public class LoadoutPromotionSinkTests(ITestOutputHelper helper) : AStubbedGameIsolatedTest<LoadoutPromotionSinkTests>(helper), IDisposable
{
    private readonly AbsolutePath _directory = NexusMods.Paths.FileSystem.Shared
        .GetKnownPath(KnownPath.TempDirectory)
        .Combine($"vfs-sink-tests-{Guid.NewGuid():N}");

    protected override async Task GenerateGameFiles()
    {
        var file = GameInstallation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, "Data/image.dds"));
        file.Parent.CreateDirectory();
        await file.WriteAllTextAsync("vanilla image");
    }

    private StagingPromoter NewPromoter(LoadoutId loadoutId, out StagingDirectory staging)
    {
        var installation = Loadout.Load(Connection.Db, loadoutId).InstallationId;
        staging = new StagingDirectory(_directory, installation);
        var sink = new LoadoutPromotionSink(FileStore, Connection);
        return new StagingPromoter(staging, sink, new XxHash3FileHasher(), NullLogger<StagingPromoter>.Instance, Size.From(new VfsSettings().PromotionSizeCapBytes));
    }

    private static void Stage(StagingDirectory staging, string path, string text)
    {
        var file = staging.PathFor(RelativePath.FromUnsanitizedInput(path));
        file.Parent.CreateDirectory();
        File.WriteAllBytes(file.ToString(), Encoding.UTF8.GetBytes(text));
    }

    private static Hash HashOf(string text) => Hash.From(System.IO.Hashing.XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(text)));

    private IReadOnlyList<LoadoutItemWithTargetPath.ReadOnly> OverridesChildren(LoadoutId loadoutId)
    {
        return LoadoutOverridesGroup.FindByOverridesFor(Connection.Db, loadoutId)
            .SelectMany(g => g.AsLoadoutItemGroup().Children.OfTypeLoadoutItemWithTargetPath())
            .ToArray();
    }

    private static readonly GamePath NewFile = new(LocationId.Game, "Data/new.txt");
    private static readonly GamePath Vanilla = new(LocationId.Game, "Data/image.dds");

    [Fact]
    public async Task StagedFilesAndWhiteoutsBecomeOverridesRows()
    {
        await LoadoutManager.ManageInstallation(GameInstallation);
        var loadout = await CreateLoadout();
        var promoter = NewPromoter(loadout.LoadoutId, out var staging);
        Stage(staging, "Data/new.txt", "new bytes");
        staging.WriteWhiteouts([RelativePath.FromUnsanitizedInput("Data/image.dds")]);

        var result = await promoter.Promote(loadout.LoadoutId);

        result.Status.Should().Be(PromotionStatus.Promoted);
        var children = OverridesChildren(loadout.LoadoutId);
        children.Should().HaveCount(2);
        var file = children.Single(c => c.TargetPath == NewFile);
        file.TryGetAsLoadoutFile(out var loadoutFile).Should().BeTrue();
        loadoutFile.Hash.Should().Be(HashOf("new bytes"));
        loadoutFile.Size.Value.Should().Be((ulong)"new bytes".Length);
        children.Single(c => c.TargetPath == Vanilla).TryGetAsDeletedFile(out _).Should().BeTrue();
        (await FileStore.HaveFile(HashOf("new bytes"))).Should().BeTrue();
        (await FileStore.Load(HashOf("new bytes"))).Should().Equal(Encoding.UTF8.GetBytes("new bytes"));
    }

    [Fact]
    public async Task PromotingTheSamePathTwiceLeavesOneRowPerPath()
    {
        await LoadoutManager.ManageInstallation(GameInstallation);
        var loadout = await CreateLoadout();
        var promoter = NewPromoter(loadout.LoadoutId, out var staging);

        for (var i = 0; i < 2; i++)
        {
            Stage(staging, "Data/new.txt", "new bytes");
            staging.WriteWhiteouts([RelativePath.FromUnsanitizedInput("Data/image.dds")]);
            (await promoter.Promote(loadout.LoadoutId)).Status.Should().Be(PromotionStatus.Promoted);
        }

        var children = OverridesChildren(loadout.LoadoutId);
        children.Should().HaveCount(2);
        children.Count(c => c.TargetPath == NewFile).Should().Be(1);
        children.Count(c => c.TargetPath == Vanilla).Should().Be(1);
    }

    [Fact]
    public async Task PromotingNewContentForAPathUpdatesItsRow()
    {
        await LoadoutManager.ManageInstallation(GameInstallation);
        var loadout = await CreateLoadout();
        var promoter = NewPromoter(loadout.LoadoutId, out var staging);

        Stage(staging, "Data/new.txt", "first");
        await promoter.Promote(loadout.LoadoutId);
        Stage(staging, "Data/new.txt", "second, longer");
        await promoter.Promote(loadout.LoadoutId);

        var row = OverridesChildren(loadout.LoadoutId).Should().ContainSingle().Subject;
        row.TryGetAsLoadoutFile(out var file).Should().BeTrue();
        file.Hash.Should().Be(HashOf("second, longer"));
    }

    [Fact]
    public async Task AWhiteoutOverAnOverridesFileRemovesTheFile()
    {
        await LoadoutManager.ManageInstallation(GameInstallation);
        var loadout = await CreateLoadout();
        var promoter = NewPromoter(loadout.LoadoutId, out var staging);
        Stage(staging, "Data/new.txt", "new bytes");
        await promoter.Promote(loadout.LoadoutId);
        OverridesChildren(loadout.LoadoutId).Should().ContainSingle();

        // Same rule as the copy backend's reified delete: the row goes away instead of turning into
        // a DeletedFile, so whatever lies below the Overrides group at that path wins again.
        staging.WriteWhiteouts([RelativePath.FromUnsanitizedInput("Data/new.txt")]);
        var result = await promoter.Promote(loadout.LoadoutId);

        result.Status.Should().Be(PromotionStatus.Promoted);
        OverridesChildren(loadout.LoadoutId).Should().BeEmpty();
    }

    [Fact]
    public async Task RecreatingADeletedPathReplacesTheDelete()
    {
        await LoadoutManager.ManageInstallation(GameInstallation);
        var loadout = await CreateLoadout();
        var promoter = NewPromoter(loadout.LoadoutId, out var staging);
        staging.WriteWhiteouts([RelativePath.FromUnsanitizedInput("Data/image.dds")]);
        await promoter.Promote(loadout.LoadoutId);

        Stage(staging, "Data/image.dds", "replacement");
        await promoter.Promote(loadout.LoadoutId);

        var row = OverridesChildren(loadout.LoadoutId).Should().ContainSingle().Subject;
        row.TryGetAsDeletedFile(out _).Should().BeFalse();
        row.TryGetAsLoadoutFile(out var file).Should().BeTrue();
        file.Hash.Should().Be(HashOf("replacement"));
    }

    public void Dispose()
    {
        if (_directory.DirectoryExists()) _directory.DeleteDirectory(recursive: true);
    }
}
