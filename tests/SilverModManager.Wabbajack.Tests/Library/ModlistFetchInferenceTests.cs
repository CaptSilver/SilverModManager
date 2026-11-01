using FluentAssertions;
using NexusMods.Paths;
using SilverModManager.Abstractions.Library;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Library;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Wabbajack.Library;
using SilverModManager.Wabbajack.Tests.Acquire;
using SilverModManager.Wabbajack.Tests.Fixtures;

namespace SilverModManager.Wabbajack.Tests.Library;

/// <summary>A file a modlist's plan fetched is for that list's game, and a modlist file is for its own.</summary>
public class ModlistFetchInferenceTests(ITestOutputHelper helper) : AcquireTestBase<ModlistFetchInferenceTests>(helper)
{
    private static readonly GameId SkyrimSe = GameId.From("CreationEngine.SkyrimSE");
    private static readonly GameId SkyrimVr = GameId.From("CreationEngine.SkyrimVR");
    private static readonly GameId Fallout4 = GameId.From("CreationEngine.Fallout4");
    private static readonly Uri Mirror = new("https://example.test/files/");

    private readonly ModlistFetchInference _inference = new();

    private async Task<LibraryFileId> AddModlist(string gameType, string name, params ArchiveEntry[] archives)
    {
        await using var folder = TemporaryFileManager.CreateFolder();
        var path = WabbajackFixture.Write(folder.Path, ListWith(archives) with { Name = name, GameType = gameType });
        return LibraryFileId.From((await LibraryService.AddLocalFile(path, UnassignedAcquisition.Instance)).AsLibraryFile().Id);
    }

    private async Task<LibraryItem.ReadOnly> AddHeldFile(byte[] content)
    {
        await using var file = TemporaryFileManager.CreateFile(new Extension(".txt"));
        await file.Path.WriteAllBytesAsync(content);
        var added = await LibraryService.AddLocalFile(file.Path, UnassignedAcquisition.Instance);
        return LibraryItem.Load(Connection.Db, added.Id);
    }

    private static ArchiveEntry Archive(byte[] content, string name)
        => Entry(new HttpState(new Uri(Mirror, name), []), Hash64(content), content.Length, name);

    private Task MarkFetched(ArchiveEntry archive, LibraryItem.ReadOnly held)
        => FetchedArchives.Mark(Connection, archive.XxHash64, LibraryFile.Load(Connection.Db, held.Id));

    [Fact]
    public void RunsAfterTheLoadoutEvidence() => _inference.Priority.Should().Be(20);

    [Fact]
    public async Task AFetchedArchive_GetsTheModlistsGame()
    {
        var content = "an archive the VR list fetched"u8.ToArray();
        var archive = Archive(content, "fetched.7z");
        await AddModlist("SkyrimVR", "VR list", archive);
        var held = await AddHeldFile(content);
        await MarkFetched(archive, held);

        _inference.Infer(Connection.Db, held).Should().Be(Destination.Of(SkyrimVr), "the list's Nexus domain is Skyrim SE's, but the list is for VR");
    }

    [Fact]
    public async Task AFileFetchedForListsOfTwoGames_IsForBothAndNotShared()
    {
        var content = "an archive two lists name"u8.ToArray();
        var archive = Archive(content, "both.7z");
        await AddModlist("SkyrimVR", "VR list", archive);
        await AddModlist("Fallout4", "FO4 list", archive);
        var held = await AddHeldFile(content);
        await MarkFetched(archive, held);

        var destination = _inference.Infer(Connection.Db, held);

        destination.Games.Should().BeEquivalentTo([SkyrimVr, Fallout4]);
        destination.Shared.Should().BeFalse();
    }

    [Fact]
    public async Task AModlistFile_GetsItsOwnGame()
    {
        var list = await AddModlist("SkyrimSpecialEdition", "An SE list");

        _inference.Infer(Connection.Db, LibraryItem.Load(Connection.Db, list.Value)).Should().Be(Destination.Of(SkyrimSe));
    }

    [Fact]
    public async Task AModlistForAGameTheAppHasNoTitleFor_HasNoAnswer()
    {
        var list = await AddModlist("Witcher3", "Untitled game list");

        _inference.Infer(Connection.Db, LibraryItem.Load(Connection.Db, list.Value)).IsUnknown.Should().BeTrue();
    }

    [Fact]
    public async Task AFileNoListFetched_HasNoAnswer()
    {
        var held = await AddHeldFile("a file the user added"u8.ToArray());

        _inference.Infer(Connection.Db, held).IsUnknown.Should().BeTrue();
    }
}
