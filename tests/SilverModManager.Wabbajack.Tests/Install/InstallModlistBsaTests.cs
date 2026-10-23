using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Games.Bethesda.Archives;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Sdk.Library;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Wabbajack.Install;
using SilverModManager.Wabbajack.Models;

namespace SilverModManager.Wabbajack.Tests.Install;

public class InstallModlistBsaTests(ITestOutputHelper helper) : InstallTestBase<InstallModlistBsaTests>(helper)
{
    private static readonly InstallOptions Defaults = new(false, new HashSet<RelativePath>());

    private static async Task<Dictionary<string, byte[]>> MembersOf(byte[] archive)
    {
        await using var reader = await BethesdaArchive.Open(new MemoryStream(archive), CancellationToken.None);
        var members = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in reader.Entries)
        {
            using var copy = new MemoryStream();
            await entry.CopyToAsync(copy, CancellationToken.None);
            members[entry.Path.ToString().Replace('\\', '/')] = copy.ToArray();
        }
        return members;
    }

    [Fact]
    public async Task ABuiltArchiveHoldsEveryInputAndTheInputsLeaveNoLoadoutRows()
    {
        var list = new ListBuilder();
        list.Directives.Add(list.Inline("TEMP_BSA_FILES/pack/meshes/one.nif", "one"));
        list.Directives.Add(list.Inline("TEMP_BSA_FILES/pack/textures/two.dds", "two"));
        list.Directives.Add(list.Bsa("mods/A/pack.bsa", "pack", "meshes/one.nif", "textures/two.dds"));
        var loadout = await CreateLoadout();
        var modlist = await AddModlist(list.ToList(), list.Blobs);

        var (job, group) = await Install(loadout, modlist, Defaults);

        SkipsOf(group).Should().BeEmpty();
        var members = await MembersOf(await InstalledBytes(group, "A", "Game/Data/pack.bsa"));
        members.Should().HaveCount(2);
        members["meshes/one.nif"].Should().Equal("one"u8.ToArray());
        members["textures/two.dds"].Should().Equal("two"u8.ToArray());
        TreeOf(group)["A"].Keys.Should().BeEquivalentTo(["Game/Data/pack.bsa"], "inputs of an archive are never loadout files");
        job.Progress.DroppedCount.Should().Be(0);
    }

    [Fact]
    public async Task AnArchiveListedBeforeItsInputsIsStillBuiltFromThem()
    {
        var list = new ListBuilder();
        list.Directives.Add(list.Bsa("mods/A/pack.bsa", "pack", "meshes/one.nif"));
        list.Directives.Add(list.Inline("TEMP_BSA_FILES/pack/meshes/one.nif", "one"));
        var loadout = await CreateLoadout();
        var modlist = await AddModlist(list.ToList(), list.Blobs);

        var (_, group) = await Install(loadout, modlist, Defaults);

        SkipsOf(group).Should().BeEmpty();
        (await MembersOf(await InstalledBytes(group, "A", "Game/Data/pack.bsa")))["meshes/one.nif"].Should().Equal("one"u8.ToArray());
    }

    [Fact]
    public async Task AnArchiveWithARecompressedTextureInputBuildsWithoutAMismatchRow()
    {
        var archive = await AddZip("a.zip", new() { ["t.dds"] = Bc1Dds(64) });
        var list = new ListBuilder();
        list.Archives.Add(archive);
        // The recorded hash is not what the re-encode writes; the archive has to be checked against what was written.
        list.Directives.Add(new TransformedTexture(P("TEMP_BSA_FILES/pack/textures/t.dds"), NexusMods.Hashing.xxHash3.Hash.From(7), 1,
            new ArchiveHashPath(archive.XxHash64, [P("t.dds")]), new ImageState(32, 32, 3, "BC7_UNORM")));
        list.Directives.Add(list.Bsa("mods/A/pack.bsa", "pack", "textures/t.dds"));
        var loadout = await CreateLoadout();
        var modlist = await AddModlist(list.ToList(), list.Blobs);

        var (_, group) = await Install(loadout, modlist, Defaults);

        SkipsOf(group).Should().BeEmpty();
        var members = await MembersOf(await InstalledBytes(group, "A", "Game/Data/pack.bsa"));
        DdsHeader.Read(members["textures/t.dds"]).Should().Be(new DdsHeader(32, 32, 3, Bc7: true));
    }

    [Fact]
    public async Task AnArchiveWhoseStateCannotBeReadIsASkipRowAndItsInputsAreCountedAsDropped()
    {
        var list = new ListBuilder();
        list.Directives.Add(list.Inline("TEMP_BSA_FILES/pack/one.txt", "1"));
        list.Directives.Add(list.Inline("TEMP_BSA_FILES/pack/two.txt", "2"));
        list.Directives.Add(new CreateBsa(P("mods/A/pack.bsa"), NexusMods.Hashing.xxHash3.Hash.From(1), 2, P("pack"),
            System.Text.Json.JsonDocument.Parse("{\"$type\":\"NotAnArchive\"}").RootElement, []));
        var loadout = await CreateLoadout();
        var modlist = await AddModlist(list.ToList(), list.Blobs);

        var (job, group) = await Install(loadout, modlist, Defaults);

        var skip = SkipsOf(group).Should().ContainSingle().Subject;
        skip.Kind.Should().Be(SkipKind.Transform);
        skip.Reason.Should().Contain("mods/A/pack.bsa").And.Contain("NotAnArchive");
        job.Progress.DroppedCount.Should().Be(2);
    }

    [Fact]
    public async Task AnArchiveThatNeedsAFileNoDirectiveWritesIsASkipRowNamingIt()
    {
        var list = new ListBuilder();
        list.Directives.Add(list.Bsa("mods/A/pack.bsa", "pack", "meshes/absent.nif"));
        var loadout = await CreateLoadout();
        var modlist = await AddModlist(list.ToList(), list.Blobs);

        var (_, group) = await Install(loadout, modlist, Defaults);

        var skip = SkipsOf(group).Should().ContainSingle().Subject;
        skip.Kind.Should().Be(SkipKind.MemberNotFound);
        skip.Reason.Should().Contain("meshes/absent.nif");
    }

    [Fact]
    public async Task AFileFromAMemberOfABsaInAnArchiveIsCopiedOut()
    {
        var bsa = await BuildBsa();
        var archive = await AddZip("a.zip", new() { ["data/pack.bsa"] = bsa });
        var list = new ListBuilder();
        list.Archives.Add(archive);
        // The member is spelt in a different case than the archive stores it.
        list.Directives.Add(new FromArchive(P("mods/A/first.nif"), Hash64(TestArchives.FirstMemberData), TestArchives.FirstMemberData.Length,
            new ArchiveHashPath(archive.XxHash64, [P("data/pack.bsa"), P("Meshes/FIRST.nif")])));
        var loadout = await CreateLoadout();
        var modlist = await AddModlist(list.ToList(), list.Blobs);

        var (_, group) = await Install(loadout, modlist, Defaults);

        SkipsOf(group).Should().BeEmpty();
        (await InstalledBytes(group, "A", "Game/Data/first.nif")).Should().Equal(TestArchives.FirstMemberData);
    }

    [Fact]
    public async Task AMemberThatIsNotInTheBsaIsASkipRowNamingIt()
    {
        var bsa = await BuildBsa();
        var archive = await AddZip("a.zip", new() { ["pack.bsa"] = bsa });
        var list = new ListBuilder();
        list.Archives.Add(archive);
        list.Directives.Add(new FromArchive(P("mods/A/missing.nif"), Hash.From(1), 1, new ArchiveHashPath(archive.XxHash64, [P("pack.bsa"), P("meshes/missing.nif")])));
        var loadout = await CreateLoadout();
        var modlist = await AddModlist(list.ToList(), list.Blobs);

        var (_, group) = await Install(loadout, modlist, Defaults);

        var skip = SkipsOf(group).Should().ContainSingle().Subject;
        skip.Kind.Should().Be(SkipKind.MemberNotFound);
        skip.Reason.Should().Contain("meshes/missing.nif");
    }

    [Fact]
    public async Task AListWithEveryKindOfDirectiveInstallsWithoutSkipRows()
    {
        var archive = await AddZip("a.zip", new() { ["plain.bin"] = [1, 2], ["patched.bin"] = [3, 4], ["t.dds"] = Bc1Dds(64), ["pack.bsa"] = await BuildBsa() });
        var list = new ListBuilder();
        list.Archives.Add(archive);
        list.Directives.Add(list.Member("mods/A/plain.bin", archive, "plain.bin", [1, 2]));
        list.Directives.Add(list.Patched("mods/A/patched.bin", archive, "patched.bin", [3, 4], [3, 4, 5, 6]));
        list.Directives.Add(new TransformedTexture(P("mods/A/t.dds"), Hash.From(7), 1, new ArchiveHashPath(archive.XxHash64, [P("t.dds")]), new ImageState(32, 32, 3, "BC7_UNORM")));
        list.Directives.Add(new FromArchive(P("mods/A/first.nif"), Hash64(TestArchives.FirstMemberData), TestArchives.FirstMemberData.Length,
            new ArchiveHashPath(archive.XxHash64, [P("pack.bsa"), TestArchives.FirstMemberPath])));
        list.Directives.Add(list.Inline("mods/A/inline.txt", "inline"));
        list.Directives.Add(list.Remapped("mods/A/remapped.ini", "path=%GAME_PATH%"));
        list.Directives.Add(list.Meta("downloads/a.zip.meta", "[General]"));
        list.Directives.Add(list.Merged("mods/M/merged.bin", [1, 2, 3, 4, 9], ("mods/A/plain.bin", [1, 2]), ("mods/A/patched.bin", [3, 4, 5, 6])));
        list.Directives.Add(list.Inline("TEMP_BSA_FILES/pack/meshes/one.nif", "one"));
        list.Directives.Add(list.Bsa("mods/A/pack.bsa", "pack", "meshes/one.nif"));
        var loadout = await CreateLoadout();
        var modlist = await AddModlist(list.ToList(), list.Blobs);

        var (_, group) = await Install(loadout, modlist, Defaults);

        SkipsOf(group).Should().BeEmpty();
    }

    private async Task<byte[]> BuildBsa() => await TestArchives.Tes5Archive(TemporaryFileManager);
}

public class InstallModlistGameBsaTests(ITestOutputHelper helper) : InstallTestBase<InstallModlistGameBsaTests>(helper)
{
    private static readonly byte[] VanillaBsa = BuildVanillaBsa();

    private static byte[] BuildVanillaBsa()
    {
        using var temporary = new TemporaryFileManager(
            NexusMods.Paths.FileSystem.Shared,
            NexusMods.Paths.FileSystem.Shared.GetKnownPath(KnownPath.EntryDirectory).Combine($"Wabbajack.Tests-{Guid.NewGuid()}"));
        return TestArchives.Tes5Archive(temporary).GetAwaiter().GetResult();
    }

    protected override Dictionary<RelativePath, byte[]> StubGameFiles => new()
    {
        [GameBsa] = VanillaBsa,
        [RelativePath.FromUnsanitizedInput("SkyrimSE.exe")] = StubExe,
    };

    [Fact]
    public async Task AFileFromAMemberOfAVanillaBsaInTheGameFolderIsCopiedOut()
    {
        var hash = Hash64(VanillaBsa);
        var vanilla = new ArchiveEntry(hash, VanillaBsa.Length, "Skyrim - Test.bsa", "", new GameFileSourceState("SkyrimSpecialEdition", GameBsa, hash, GameVersion));
        var list = new ListBuilder();
        list.Archives.Add(vanilla);
        list.Directives.Add(new FromArchive(P("mods/A/second.dds"), Hash64(TestArchives.SecondMemberData), TestArchives.SecondMemberData.Length,
            new ArchiveHashPath(hash, [TestArchives.SecondMemberPath])));
        var loadout = await CreateLoadout();
        var modlist = await AddModlist(list.ToList(), list.Blobs);

        var (_, group) = await Install(loadout, modlist, new InstallOptions(false, new HashSet<RelativePath>()));

        SkipsOf(group).Should().BeEmpty();
        (await InstalledBytes(group, "A", "Game/Data/second.dds")).Should().Equal(TestArchives.SecondMemberData);
    }
}

public class InstallModlistArchiveMismatchTests(ITestOutputHelper helper) : InstallTestBase<InstallModlistArchiveMismatchTests>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services)
        => base.AddServices(services).AddSingleton<IDirectiveTransforms, MismatchingArchiveTransforms>();

    [Fact]
    public async Task AnArchiveThatReadsBackWrongIsATransformSkipRowNamingTheEntry()
    {
        var list = new ListBuilder();
        list.Directives.Add(list.Inline("TEMP_BSA_FILES/pack/meshes/one.nif", "one"));
        list.Directives.Add(list.Bsa("mods/A/pack.bsa", "pack", "meshes/one.nif"));
        var loadout = await CreateLoadout();
        var modlist = await AddModlist(list.ToList(), list.Blobs);

        var (job, group) = await Install(loadout, modlist, new InstallOptions(false, new HashSet<RelativePath>()));

        var skip = SkipsOf(group).Should().ContainSingle().Subject;
        skip.Kind.Should().Be(SkipKind.Transform);
        skip.To.Should().Be("mods/A/pack.bsa");
        skip.Reason.Should().Contain("meshes/one.nif");
        TreeOf(group).Should().NotContainKey("A");
        job.Progress.DroppedCount.Should().Be(1, "the input went into an archive that was not kept");
    }
}
