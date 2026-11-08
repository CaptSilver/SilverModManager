using FluentAssertions;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Wabbajack.Acquire;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Library;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk.Library;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Wabbajack.Install;
using SilverModManager.Wabbajack.Models;
using SilverModManager.Wabbajack.Tests.Install;
using SilverModManager.Wabbajack.Update;

namespace SilverModManager.Wabbajack.Tests.Update;

/// <summary>The read-only look at moving an installed list to a newer version of it.</summary>
public class ModlistUpdatePreviewTests(ITestOutputHelper helper) : InstallTestBase<ModlistUpdatePreviewTests>(helper)
{
    private static readonly InstallOptions Options = new(false, new HashSet<RelativePath>());

    private static byte[] Bytes(int seed, int length = 40) => Enumerable.Range(0, length).Select(i => (byte)(seed * 7 + i)).ToArray();

    private static string Mod(string folder, string file) => $"mods/{folder}/{file}";

    private ModlistUpdatePreview NewPreview() => new(ServiceProvider);

    private Task<ModlistUpdatePreviewResult> Preview(WabbajackModlistLoadoutGroup.ReadOnly group, WabbajackModlistLibraryFile.ReadOnly newFile)
        => NewPreview().Preview(group, newFile, CancellationToken.None);

    /// <summary>Mod A holds five files, B two, C one.</summary>
    private static Dictionary<string, byte[]> VersionOneFiles()
    {
        var files = Enumerable.Range(0, 5).ToDictionary(i => $"a{i}.bin", i => Bytes(i));
        files["b0.bin"] = Bytes(10);
        files["b1.bin"] = Bytes(11);
        files["c0.bin"] = Bytes(12);
        files["b1v2.bin"] = Bytes(13);
        files["d0.bin"] = Bytes(14);
        return files;
    }

    private async Task<(Loadout.ReadOnly Loadout, WabbajackModlistLoadoutGroup.ReadOnly Group, WabbajackModlistLibraryFile.ReadOnly NewFile)> InstalledVersionOneWithVersionTwoInTheLibrary()
    {
        var files = VersionOneFiles();
        var archive = await AddZip("a.zip", files);

        var one = new ListBuilder();
        one.Archives.Add(archive);
        for (var i = 0; i < 5; i++) one.Directives.Add(one.Member(Mod("A", $"a{i}.bin"), archive, $"a{i}.bin", files[$"a{i}.bin"]));
        one.Directives.Add(one.Member(Mod("B", "b0.bin"), archive, "b0.bin", files["b0.bin"]));
        one.Directives.Add(one.Member(Mod("B", "b1.bin"), archive, "b1.bin", files["b1.bin"]));
        one.Directives.Add(one.Member(Mod("C", "c0.bin"), archive, "c0.bin", files["c0.bin"]));
        one.Directives.Add(one.Inline("profiles/Default/modlist.txt", "+A\n+B\n+C\n"));

        // Version two renames A, changes a file of B, drops C and adds D.
        var two = new ListBuilder();
        two.Archives.Add(archive);
        for (var i = 0; i < 5; i++) two.Directives.Add(two.Member(Mod("A SE", $"a{i}.bin"), archive, $"a{i}.bin", files[$"a{i}.bin"]));
        two.Directives.Add(two.Member(Mod("B", "b0.bin"), archive, "b0.bin", files["b0.bin"]));
        two.Directives.Add(two.Member(Mod("B", "b1.bin"), archive, "b1v2.bin", files["b1v2.bin"]));
        two.Directives.Add(two.Member(Mod("D", "d0.bin"), archive, "d0.bin", files["d0.bin"]));
        two.Directives.Add(two.Inline("profiles/Default/modlist.txt", "+A SE\n+B\n+D\n"));

        var installed = await AddModlist(one.ToList("Test list"), one.Blobs);
        var newFile = await AddModlist(two.ToList("Test list"), two.Blobs);
        var loadout = await CreateLoadout();
        var (_, group) = await Install(loadout, installed, Options);
        return (loadout, group, newFile);
    }

    [Fact]
    public async Task ThePreviewCountsAddedChangedRenamedAndRemovedFolders()
    {
        var (_, group, newFile) = await InstalledVersionOneWithVersionTwoInTheLibrary();

        var preview = await Preview(group, newFile);

        preview.Added.Should().Equal("D");
        preview.Changed.Should().Equal("B");
        preview.Renamed.Should().Equal([("A", "A SE")]);
        preview.Removed.Should().Equal("C");
        preview.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task ThePreviewListsOnlyArchivesTheLibraryDoesNotHold()
    {
        var files = VersionOneFiles();
        var archive = await AddZip("a.zip", files);
        var absent = new ArchiveEntry(Xx64.From(99), 1234, "new.zip", "", new HttpState(new Uri("https://example.invalid/new.zip"), []));

        var one = new ListBuilder();
        one.Archives.Add(archive);
        one.Directives.Add(one.Member(Mod("A", "a0.bin"), archive, "a0.bin", files["a0.bin"]));
        one.Directives.Add(one.Inline("profiles/Default/modlist.txt", "+A\n"));
        var two = new ListBuilder();
        two.Archives.AddRange([archive, absent]);
        two.Directives.Add(two.Member(Mod("A", "a0.bin"), archive, "a0.bin", files["a0.bin"]));
        two.Directives.Add(two.Member(Mod("N", "n0.bin"), absent, "n0.bin", Bytes(50)));
        two.Directives.Add(two.Inline("profiles/Default/modlist.txt", "+A\n+N\n"));

        var installed = await AddModlist(one.ToList("Test list"), one.Blobs);
        var newFile = await AddModlist(two.ToList("Test list"), two.Blobs);
        var (_, group) = await Install(await CreateLoadout(), installed, Options);

        var preview = await Preview(group, newFile);

        preview.Downloads.Select(row => row.Archive.Name).Should().Equal("new.zip");
        preview.DownloadSize.Value.Should().Be(1234UL);
        preview.Added.Should().Equal("N");
    }

    [Fact]
    public async Task ThePreviewWritesNothing()
    {
        var (loadout, group, newFile) = await InstalledVersionOneWithVersionTwoInTheLibrary();
        // Matching the list's game files may record digests the first time; only the preview itself has to leave everything alone.
        newFile.AsLibraryFile().TryGetAsLibraryArchive(out var newArchive).Should().BeTrue();
        var fileStore = ServiceProvider.GetRequiredService<IFileStore>();
        var list = await ModlistArchive.ReadModlist(fileStore, newArchive, CancellationToken.None);
        var inventory = ServiceProvider.GetRequiredService<ArchiveInventory>();
        await inventory.Build(list, Connection.Db, inventory.GameFilesFor(loadout));

        var basis = Connection.Db.BasisTxId;
        var archives = fileStore.ArchivesVersion;
        var skips = SkipsOf(group).Select(s => s.Id).ToArray();
        var installedAt = WabbajackModlistLoadoutGroup.Load(Connection.Db, group.Id).InstalledAt;
        var tree = TreeOf(group);

        await Preview(group, newFile);

        Connection.Db.BasisTxId.Should().Be(basis, "no transaction was committed");
        fileStore.ArchivesVersion.Should().Be(archives, "the file store gained no archive");
        SkipsOf(group).Select(s => s.Id).Should().Equal(skips);
        WabbajackModlistLoadoutGroup.Load(Connection.Db, group.Id).InstalledAt.Should().Be(installedAt);
        TreeOf(group).Should().BeEquivalentTo(tree);
    }

    [Fact]
    public async Task ThePreviewIsNotAnInstallRun()
    {
        var (loadout, group, newFile) = await InstalledVersionOneWithVersionTwoInTheLibrary();
        var runs = ServiceProvider.GetRequiredService<ModlistInstallRuns>();

        var pending = Preview(group, newFile);
        runs.FindInto(loadout.LoadoutId).Should().BeNull("a preview registers no job");
        await pending;

        runs.FindInto(loadout.LoadoutId).Should().BeNull();
        runs.IsInstallingInto(loadout.LoadoutId).Should().BeFalse();
    }

    [Fact]
    public async Task AKeptOriginalTextureThatWillBeRetriedIsListedUnderRetried()
    {
        var archive = await AddZip("a.zip", new() { ["tex.dds"] = Bc1Dds(8) });
        var one = new ListBuilder();
        one.Archives.Add(archive);
        one.Directives.Add(UnwritableTexture(Mod("A", "kept.dds"), archive, "tex.dds"));
        one.Directives.Add(one.Inline("profiles/Default/modlist.txt", "+A\n"));
        var two = new ListBuilder();
        two.Archives.Add(archive);
        // The author changed the texture's recipe, so the original the first run kept no longer matches the list.
        two.Directives.Add(UnwritableTexture(Mod("A", "kept.dds"), archive, "tex.dds") with { XxHash64 = Xx64.From(2) });
        two.Directives.Add(two.Inline("profiles/Default/modlist.txt", "+A\n"));
        var installed = await AddModlist(one.ToList("Test list"), one.Blobs);
        var newFile = await AddModlist(two.ToList("Test list"), two.Blobs);
        var (_, group) = await Install(await CreateLoadout(), installed, Options);
        SkipsOf(group).Should().ContainSingle().Which.Kind.Should().Be(SkipKind.KeptOriginal);

        var preview = await Preview(group, newFile);

        preview.RetriedTextures.Should().Equal(Mod("A", "kept.dds"));
    }

    [Fact]
    public async Task AKeptOriginalTextureTheNewListLeavesUnchangedIsNotRetried()
    {
        var archive = await AddZip("a.zip", new() { ["tex.dds"] = Bc1Dds(8), ["x.bin"] = Bytes(3) });
        var one = new ListBuilder();
        one.Archives.Add(archive);
        one.Directives.Add(UnwritableTexture(Mod("A", "kept.dds"), archive, "tex.dds"));
        one.Directives.Add(one.Inline("profiles/Default/modlist.txt", "+A\n"));
        var two = new ListBuilder();
        two.Archives.Add(archive);
        two.Directives.Add(UnwritableTexture(Mod("A", "kept.dds"), archive, "tex.dds"));
        two.Directives.Add(two.Member(Mod("A", "x.bin"), archive, "x.bin", Bytes(3)));
        two.Directives.Add(two.Inline("profiles/Default/modlist.txt", "+A\n"));
        var installed = await AddModlist(one.ToList("Test list"), one.Blobs);
        var newFile = await AddModlist(two.ToList("Test list"), two.Blobs);
        var (_, group) = await Install(await CreateLoadout(), installed, Options);

        var preview = await Preview(group, newFile);

        preview.RetriedTextures.Should().BeEmpty("the row at the path still matches the list's directive");
        preview.Changed.Should().Equal("A");
    }

    [Fact]
    public async Task ThePreviewWarnsWhenTheInstalledVersionsFileIsGone()
    {
        var (_, group, newFile) = await InstalledVersionOneWithVersionTwoInTheLibrary();
        // The library file is still there, but the members the list was read from have been removed.
        var installedFile = WabbajackModlistLibraryFile.Load(Connection.Db, WabbajackModlistLoadoutGroup.Load(Connection.Db, group.Id).ModlistId.Value);
        installedFile.AsLibraryFile().TryGetAsLibraryArchive(out var archive).Should().BeTrue();
        using (var tx = Connection.BeginTransaction())
        {
            foreach (var child in archive.Children) tx.Delete(child.Id, recursive: false);
            await tx.Commit();
        }

        var preview = await Preview(WabbajackModlistLoadoutGroup.Load(Connection.Db, group.Id), newFile);

        preview.Warnings.Should().Contain("the installed version's files are gone from the library; the list's order and flags are applied in full");
    }

    [Fact]
    public async Task OnlyAGameFileThePreviousVersionPlacedAndTheUserKeptIsListedAsKept()
    {
        var enb = await AddZip("enb.zip", new() { ["d3d11.dll"] = [9, 9, 9], ["new.dll"] = [8, 8, 8] });
        FromArchive Exe() => new(P("Stock Game/SkyrimSE.exe"), Hash64(StubExe), StubExe.Length, new ArchiveHashPath(Hash64(StubExe), []));

        var one = new ListBuilder();
        one.Archives.AddRange([enb, GameExeArchive()]);
        one.Directives.Add(Exe());
        one.Directives.Add(one.Member("Stock Game/d3d11.dll", enb, "d3d11.dll", [9, 9, 9]));
        one.Directives.Add(one.Inline("profiles/Default/modlist.txt", ""));
        var two = new ListBuilder();
        two.Archives.AddRange([enb, GameExeArchive()]);
        two.Directives.Add(Exe());
        two.Directives.Add(two.Member("Stock Game/d3d11.dll", enb, "d3d11.dll", [9, 9, 9]));
        two.Directives.Add(two.Member("Stock Game/new.dll", enb, "new.dll", [8, 8, 8]));
        two.Directives.Add(two.Inline("profiles/Default/modlist.txt", ""));
        var installed = await AddModlist(one.ToList("Test list"), one.Blobs);
        var newFile = await AddModlist(two.ToList("Test list"), two.Blobs);
        var (_, group) = await Install(await CreateLoadout(), installed, new InstallOptions(false, new HashSet<RelativePath> { P("d3d11.dll") }));

        var preview = await Preview(group, newFile);

        preview.GameFilesKept.Should().Equal(P("d3d11.dll"));
    }

    [Fact]
    public async Task AGameFileThePreviousVersionCopiedFromTheGameIsNotListedAsKeptWhenTheNewVersionReplacesIt()
    {
        var patched = await AddZip("patched.zip", new() { ["SkyrimSE.exe"] = [5, 5, 5, 5] });
        var one = new ListBuilder();
        one.Archives.Add(GameExeArchive());
        one.Directives.Add(new FromArchive(P("Stock Game/SkyrimSE.exe"), Hash64(StubExe), StubExe.Length, new ArchiveHashPath(Hash64(StubExe), [])));
        one.Directives.Add(one.Inline("profiles/Default/modlist.txt", ""));
        var two = new ListBuilder();
        two.Archives.Add(patched);
        two.Directives.Add(two.Member("Stock Game/SkyrimSE.exe", patched, "SkyrimSE.exe", [5, 5, 5, 5]));
        two.Directives.Add(two.Inline("profiles/Default/modlist.txt", ""));
        var installed = await AddModlist(one.ToList("Test list"), one.Blobs);
        var newFile = await AddModlist(two.ToList("Test list"), two.Blobs);
        var (_, group) = await Install(await CreateLoadout(), installed, Options);
        SkipsOf(group).Should().BeEmpty("the vanilla copy matched, so the run left neither a row nor a note");

        var preview = await Preview(group, newFile);

        preview.GameFilesKept.Should().BeEmpty("the user never chose to keep the game's exe; the previous version left it alone because it was the list's own");
    }

    [Fact]
    public async Task AnArchiveHoldingAKeptOriginalTextureThatChangesInTheNewVersionIsListedUnderRetried()
    {
        var archive = await AddZip("a.zip", new() { ["t.dds"] = Bc1Dds(8) });
        var one = new ListBuilder();
        one.Archives.Add(archive);
        one.Directives.Add(UnwritableTexture("TEMP_BSA_FILES/pack/textures/t.dds", archive, "t.dds"));
        one.Directives.Add(one.Bsa("mods/A/pack.bsa", "pack", "textures/t.dds"));
        var two = new ListBuilder();
        two.Archives.Add(archive);
        two.Directives.Add(UnwritableTexture("TEMP_BSA_FILES/pack/textures/t.dds", archive, "t.dds"));
        two.Directives.Add(two.Bsa("mods/A/pack.bsa", "pack", "textures/t.dds") with { XxHash64 = Xx64.From(2) });
        var installed = await AddModlist(one.ToList("Test list"), one.Blobs);
        var newFile = await AddModlist(two.ToList("Test list"), two.Blobs);
        var (_, group) = await Install(await CreateLoadout(), installed, Options);
        SkipsOf(group).Should().ContainSingle().Which.To.Should().Be("mods/A/pack.bsa");

        var preview = await Preview(group, newFile);

        preview.RetriedTextures.Should().Equal("mods/A/pack.bsa");
    }
}
