using FluentAssertions;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using System.Text;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Ini;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Wabbajack.Install;
using SilverModManager.Wabbajack.Models;

namespace SilverModManager.Wabbajack.Tests.Install;

/// <summary>
/// Installing a list again after an interrupted run does only what the earlier run did not finish: a directive whose
/// file is already in the loadout is neither located in its archive nor written.
/// </summary>
public class InstallResumeTests(ITestOutputHelper helper) : InstallTestBase<InstallResumeTests>(helper)
{
    private static readonly InstallOptions Options = new(false, new HashSet<RelativePath>());

    private static readonly byte[] Base = Enumerable.Range(0, 400).Select(i => (byte)(i % 251)).ToArray();

    private static byte[] Edited(int n) => Base.Select(b => (byte)(b ^ n)).ToArray();

    /// <summary>Four plain copies, two patches and two inline files, spread over two mods.</summary>
    private async Task<WabbajackModlistLibraryFile.ReadOnly> MixedList()
    {
        var files = Enumerable.Range(0, 4).ToDictionary(i => $"m{i}.bin", i => (byte[])[(byte)i, 7, 7, (byte)(i * 5)]);
        files["base.bin"] = Base;
        var archive = await AddZip("a.zip", files.ToDictionary(f => f.Key, f => f.Value));
        var list = new ListBuilder();
        list.Archives.Add(archive);
        for (var i = 0; i < 4; i++) list.Directives.Add(list.Member($"mods/{(i < 2 ? "A" : "B")}/m{i}.bin", archive, $"m{i}.bin", files[$"m{i}.bin"]));
        for (var i = 0; i < 2; i++) list.Directives.Add(list.Patched($"mods/B/p{i}.bin", archive, "base.bin", Base, Edited(i + 1)));
        list.Directives.Add(list.Inline("mods/A/i0.txt", "inline zero"));
        list.Directives.Add(list.Inline("mods/B/i1.txt", "inline one"));
        list.Directives.Add(list.Inline("profiles/Default/modlist.txt", "+A\n+B\n"));
        return await AddModlist(list.ToList(), list.Blobs);
    }

    private sealed record Phases(int Resolved, int Written);

    /// <summary>Runs the job and reports how many directives its resolve and write phases had to do.</summary>
    private async Task<(Phases Phases, WabbajackModlistLoadoutGroup.ReadOnly Group)> Resume(Loadout.ReadOnly loadout, WabbajackModlistLibraryFile.ReadOnly modlist)
    {
        var job = NewJob(loadout, modlist, Options);
        var totals = new Dictionary<InstallPhase, int>();
        using var _ = job.Progress.Changes.Subscribe(s => totals[s.Phase] = s.TotalItems);
        var group = await Run(job);
        return (new Phases(totals[InstallPhase.Resolve], totals[InstallPhase.Write]), group);
    }

    /// <summary>Deletes the rows of the files at these paths, as if the run that wrote them stopped before its commit.</summary>
    private async Task Forget(WabbajackModlistLoadoutGroup.ReadOnly group, params string[] fileNames)
    {
        using var tx = Connection.BeginTransaction();
        foreach (var file in LoadoutFile.All(Connection.Db).Where(f => fileNames.Contains(f.AsLoadoutItemWithTargetPath().AsLoadoutItem().Name)))
            tx.Delete(file.Id, recursive: false);
        await tx.Commit();
    }

    [Fact]
    public async Task OnlyTheDirectivesWhoseFilesAreMissingAreResolvedAndWritten()
    {
        var loadout = await CreateLoadout();
        var modlist = await MixedList();
        var (_, first) = await Install(loadout, modlist, Options);
        var complete = TreeOf(first);

        await Forget(first, "m1.bin", "p0.bin", "i0.txt");
        var (phases, resumed) = await Resume(loadout, modlist);

        phases.Should().Be(new Phases(3, 3));
        TreeOf(resumed).Should().BeEquivalentTo(complete, "a resumed install ends where a whole one does");
        SkipsOf(resumed).Should().BeEmpty();
    }

    [Fact]
    public async Task ResumingACompleteInstallResolvesAndWritesNothingAndKeepsEveryFile()
    {
        var loadout = await CreateLoadout();
        var modlist = await MixedList();
        var (_, first) = await Install(loadout, modlist, Options);
        var complete = TreeOf(first);
        var rows = LoadoutFile.All(Connection.Db).Select(f => f.Id).ToHashSet();

        var (phases, resumed) = await Resume(loadout, modlist);

        phases.Should().Be(new Phases(0, 0));
        TreeOf(resumed).Should().BeEquivalentTo(complete);
        LoadoutFile.All(Connection.Db).Select(f => f.Id).ToHashSet().Should().BeEquivalentTo(rows, "the kept files are the same rows, not rewritten copies");
    }

    [Fact]
    public async Task ASkippedDirectiveIsTriedAgainBecauseItHasNoFileRow()
    {
        var archive = await AddZip("a.zip", new() { ["a.bin"] = [1, 2, 3] });
        var list = new ListBuilder();
        list.Archives.Add(archive);
        list.Directives.Add(list.Member("mods/A/a.bin", archive, "a.bin", [1, 2, 3]));
        list.Directives.Add(UnbuildableArchive("mods/A/x.bsa"));
        list.Directives.Add(list.Inline("profiles/Default/modlist.txt", "+A\n"));
        var loadout = await CreateLoadout();
        var modlist = await AddModlist(list.ToList(), list.Blobs);
        var (_, first) = await Install(loadout, modlist, Options);
        SkipsOf(first).Should().ContainSingle(s => s.Kind == SkipKind.Transform);

        var (phases, resumed) = await Resume(loadout, modlist);

        phases.Should().Be(new Phases(1, 1), "the archive that could not be built is retried; the copy is in place");
        SkipsOf(resumed).Should().ContainSingle(s => s.Kind == SkipKind.Transform, "it is skipped again, and the earlier row was replaced");
        TreeOf(resumed)["A"].Keys.Should().BeEquivalentTo("Game/Data/a.bin");
    }

    [Fact]
    public async Task AFileWrittenBeforeTheListHashWasRecordedIsKeptWhenItIsAPlainCopyOfALibraryFile()
    {
        var loadout = await CreateLoadout();
        var modlist = await MixedList();
        var (_, first) = await Install(loadout, modlist, Options);
        var complete = TreeOf(first);
        using (var tx = Connection.BeginTransaction())
        {
            foreach (var file in LoadoutFile.All(Connection.Db).Where(f => WabbajackInstalledFile.ListHash.TryGetValue(f, out _)))
                tx.Retract(file.Id, WabbajackInstalledFile.ListHash, WabbajackInstalledFile.ListHash.Get(file));
            await tx.Commit();
        }

        var (phases, resumed) = await Resume(loadout, modlist);

        // The four copies are recognised through the library's own record of the member; the patches and inline files are not.
        phases.Should().Be(new Phases(4, 4));
        TreeOf(resumed).Should().BeEquivalentTo(complete);
    }

    [Fact]
    public async Task AMergeReadsTheFilesAKeptDirectiveWroteEarlier()
    {
        var one = "one"u8.ToArray();
        var two = "two"u8.ToArray();
        var merged = "onetwo!"u8.ToArray();
        var list = new ListBuilder();
        list.Directives.Add(list.Inline("mods/A/one.txt", one));
        list.Directives.Add(list.Inline("mods/A/two.txt", two));
        list.Directives.Add(list.Merged("mods/A/all.txt", merged, ("mods/A/one.txt", one), ("mods/A/two.txt", two)));
        list.Directives.Add(list.Inline("profiles/Default/modlist.txt", "+A\n"));
        var loadout = await CreateLoadout();
        var modlist = await AddModlist(list.ToList(), list.Blobs);
        var (_, first) = await Install(loadout, modlist, Options);
        var complete = TreeOf(first);

        await Forget(first, "all.txt");
        var (phases, resumed) = await Resume(loadout, modlist);

        phases.Should().Be(new Phases(1, 1));
        SkipsOf(resumed).Should().BeEmpty("the merge found its sources among the kept files");
        TreeOf(resumed).Should().BeEquivalentTo(complete);
    }

    [Fact]
    public async Task AFileWhoseDirectiveChangedInTheListIsWrittenAgainAndTheOthersAreKept()
    {
        var loadout = await CreateLoadout();
        var first = new ListBuilder();
        first.Directives.Add(first.Inline("mods/A/a.txt", "one"));
        first.Directives.Add(first.Inline("mods/A/b.txt", "bee"));
        first.Directives.Add(first.Inline("mods/A/gone.txt", "gone"));
        first.Directives.Add(first.Inline("profiles/Default/modlist.txt", "+A\n"));
        var (_, installed) = await Install(loadout, await AddModlist(first.ToList(), first.Blobs), Options);

        var second = new ListBuilder();
        second.Directives.Add(second.Inline("mods/A/a.txt", "two"));
        second.Directives.Add(second.Inline("mods/A/b.txt", "bee"));
        second.Directives.Add(second.Inline("profiles/Default/modlist.txt", "+A\n"));
        var updated = await AddModlist(second.ToList(), second.Blobs);
        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(installed.Id, WabbajackModlistLoadoutGroup.Modlist, updated.Id);
            await tx.Commit();
        }

        var (phases, again) = await Resume(loadout, updated);

        phases.Should().Be(new Phases(1, 1));
        TreeOf(again)["A"].Keys.Should().BeEquivalentTo("Game/Data/a.txt", "Game/Data/b.txt");
        (await InstalledBytes(again, "A", "Game/Data/a.txt")).Should().Equal("two"u8.ToArray());
    }

    /// <summary>A plain copy a resume keeps, next to the directive under test, so the phase totals show only that directive running.</summary>
    private async Task<(Phases Phases, WabbajackModlistLoadoutGroup.ReadOnly Group)> ResumeAfterInstalling(Func<ListBuilder, ArchiveEntry, Directive> directiveUnderTest)
    {
        var archive = await AddZip("a.zip", new() { ["a.bin"] = [1, 2, 3], ["tex.dds"] = Bc1Dds(8) });
        var list = new ListBuilder();
        list.Archives.Add(archive);
        list.Directives.Add(list.Member("mods/A/a.bin", archive, "a.bin", [1, 2, 3]));
        list.Directives.Add(directiveUnderTest(list, archive));
        list.Directives.Add(list.Inline("profiles/Default/modlist.txt", "+A\n"));
        var loadout = await CreateLoadout();
        var modlist = await AddModlist(list.ToList(), list.Blobs);
        await Install(loadout, modlist, Options);
        return await Resume(loadout, modlist);
    }

    [Fact]
    public async Task AKeptOriginalNoteIsWrittenAgainAndItsDirectiveCountsInTheResume()
    {
        var (phases, resumed) = await ResumeAfterInstalling((_, archive) => UnwritableTexture("mods/A/kept.dds", archive, "tex.dds"));

        phases.Should().Be(new Phases(1, 1), "the resume deletes the note, so the directive that left it has to run again");
        SkipsOf(resumed).Should().ContainSingle().Which.Kind.Should().Be(SkipKind.KeptOriginal);
    }

    [Fact]
    public async Task AFileWithPathsRemappedToThisMachineIsWrittenAgain()
    {
        var (phases, _) = await ResumeAfterInstalling((list, _) => list.Remapped("mods/A/remapped.ini", "path=%GAME_PATH%"));

        phases.Should().Be(new Phases(1, 1));
    }

    [Fact]
    public async Task AFileTheListDoesNotHashIsWrittenAgain()
    {
        var (phases, _) = await ResumeAfterInstalling((list, _) => list.Inline("mods/A/SkyrimPrefs.ini", "[Display]\niSize W=1920\n"));

        phases.Should().Be(new Phases(1, 1));
    }

    [Fact]
    public async Task AGameIniTheLoadoutOwnsIsWrittenAgain()
    {
        var (phases, _) = await ResumeAfterInstalling((list, _) => list.Inline("profiles/Default/skyrim.ini", "[General]\nsLanguage=ENGLISH\n"));

        phases.Should().Be(new Phases(1, 1), "the game's INI leaves a baseline and no file row to keep");
    }

    private const string InitTweaks = "[Display]\nfMaxTime=0.1\n";
    private const string UserTweak = "[Display]\nfMaxTime=0.5\n";
    private static readonly GamePath MainIni = new(LocationId.Preferences, "Skyrim.ini");

    private static FileIdentity IdentityOf(string text) => FileIdentity.XxHash3(Encoding.UTF8.GetBytes(text).xxHash3());

    [Fact]
    public async Task AUsersLoadoutTweakSurvivesAResume()
    {
        var archive = await AddZip("a.zip", new() { ["m0.bin"] = [1, 2, 3], ["m1.bin"] = [4, 5, 6] });
        var list = new ListBuilder();
        list.Archives.Add(archive);
        list.Directives.Add(list.Member("mods/A/m0.bin", archive, "m0.bin", [1, 2, 3]));
        list.Directives.Add(list.Member("mods/A/m1.bin", archive, "m1.bin", [4, 5, 6]));
        list.Directives.Add(list.Inline("profiles/Default/modlist.txt", "+A\n"));
        list.Directives.Add(list.Inline("profiles/Default/initweaks.ini", InitTweaks));
        var modlist = await AddModlist(list.ToList(), list.Blobs);
        var loadout = await CreateLoadout();
        var (_, first) = await Install(loadout, modlist, Options);
        await IniEdits.SetLoadoutTweak(Connection, FileStore, Loadout.Load(Connection.Db, loadout.LoadoutId), MainIni, Encoding.UTF8.GetBytes(UserTweak), IniTweakLayers.UserTweak(Loadout.Load(Connection.Db, loadout.LoadoutId), MainIni)?.Identity);
        await Forget(first, "m1.bin");

        var (_, resumed) = await Resume(loadout, modlist);

        var layers = IniTweakLayers.For(Loadout.Load(Connection.Db, loadout.LoadoutId), MainIni);
        layers.Select(l => l.Identity).Should().Equal(IdentityOf(InitTweaks), IdentityOf(UserTweak));
        layers[^1].Name.Should().Be(IniTweakLayers.LoadoutTweakName);
        TreeOf(resumed)["A"].Keys.Should().BeEquivalentTo("Game/Data/m0.bin", "Game/Data/m1.bin");
    }

    private LoadoutItemGroup.ReadOnly[] ModGroups(WabbajackModlistLoadoutGroup.ReadOnly group)
        => LoadoutItem.FindByParent(Connection.Db, group.Id).OfTypeLoadoutItemGroup().Where(g => !g.IsGameSettingsGroup).ToArray();

    [Fact]
    public async Task AListModTheUserRenamedIsFoundByItsFolderOnAResume()
    {
        var loadout = await CreateLoadout();
        var modlist = await MixedList();
        var (_, first) = await Install(loadout, modlist, Options);
        var original = ModGroups(first).Single(g => g.AsLoadoutItem().Name == "A");
        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(original.Id, LoadoutItem.Name, "My favourite mod");
            await tx.Commit();
        }
        await Forget(first, "m0.bin");

        var (_, resumed) = await Resume(loadout, modlist);

        var groups = ModGroups(resumed);
        groups.Should().HaveSameCount(ModGroups(first), "the renamed group is the one the resume continues in, not a second copy");
        var renamed = groups.Single(g => g.AsLoadoutItem().Name == "My favourite mod");
        renamed.Id.Should().Be(original.Id);
        LoadoutItem.FindByParent(Connection.Db, renamed.Id).OfTypeLoadoutItemWithTargetPath().Select(f => f.AsLoadoutItem().Name)
            .Should().Contain("m0.bin", "the file the resume restored went into the renamed group");
    }

    [Fact]
    public async Task AModGroupMadeBeforeFoldersWereRecordedGetsItsFolderFromItsNameOnAResume()
    {
        var loadout = await CreateLoadout();
        var modlist = await MixedList();
        var (_, first) = await Install(loadout, modlist, Options);
        using (var tx = Connection.BeginTransaction())
        {
            foreach (var group in ModGroups(first)) tx.Retract(group.Id, WabbajackModGroup.Folder, WabbajackModGroup.Folder.Get(group));
            await tx.Commit();
        }

        var (_, resumed) = await Resume(loadout, modlist);

        ModGroups(resumed).Select(g => (g.AsLoadoutItem().Name, WabbajackModGroup.Folder.Get(g))).Should().OnlyContain(pair => pair.Name == pair.Item2);
        ModGroups(resumed).Should().HaveSameCount(ModGroups(first));
    }

    [Fact]
    public async Task AGameFileTheUserKeptIsStillKeptOnAResume()
    {
        var enb = await AddZip("enb.zip", new() { ["d3d11.dll"] = [9, 9, 9] });
        var list = new ListBuilder();
        list.Archives.AddRange([enb, GameExeArchive()]);
        list.Directives.Add(new FromArchive(P("Stock Game/SkyrimSE.exe"), Hash64(StubExe), StubExe.Length, new ArchiveHashPath(Hash64(StubExe), [])));
        list.Directives.Add(list.Member("Stock Game/d3d11.dll", enb, "d3d11.dll", [9, 9, 9]));
        list.Directives.Add(list.Inline("profiles/Default/modlist.txt", ""));
        var modlist = await AddModlist(list.ToList(), list.Blobs);
        var loadout = await CreateLoadout();
        var (_, first) = await Install(loadout, modlist, new InstallOptions(false, new HashSet<RelativePath> { P("d3d11.dll") }));
        TreeOf(first).GetValueOrDefault("Stock Game", []).Keys.Should().NotContain("Game/d3d11.dll");

        var (_, resumed) = await Resume(loadout, modlist);

        TreeOf(resumed).GetValueOrDefault("Stock Game", []).Keys.Should().NotContain("Game/d3d11.dll", "the user chose to keep the game's own file");
    }

    [Fact]
    public async Task AVanillaGameFileThatMatchedIsComparedAgainOnAResume()
    {
        var list = new ListBuilder();
        list.Archives.Add(GameExeArchive());
        list.Directives.Add(new FromArchive(P("Stock Game/SkyrimSE.exe"), Hash64(StubExe), StubExe.Length, new ArchiveHashPath(Hash64(StubExe), [])));
        list.Directives.Add(list.Inline("profiles/Default/modlist.txt", ""));
        var modlist = await AddModlist(list.ToList(), list.Blobs);
        var loadout = await CreateLoadout();
        var (_, first) = await Install(loadout, modlist, new InstallOptions(false, new HashSet<RelativePath>()));
        SkipsOf(first).Should().BeEmpty("the game's file is the list's copy");

        // A game update changes the executable after the install finished, and no backup of the original is held, so the live file is what the list is compared with.
        using (var tx = Connection.BeginTransaction())
        {
            foreach (var backup in GameBackedUpFile.All(Connection.Db)) tx.Delete(backup.Id, recursive: false);
            await tx.Commit();
        }
        await loadout.InstallationInstance.Locations.ToAbsolutePath(new GamePath(LocationId.Game, P("SkyrimSE.exe"))).WriteAllBytesAsync("an updated executable"u8.ToArray());
        var (_, resumed) = await Resume(loadout, modlist);

        SkipsOf(resumed).Should().ContainSingle().Which.Kind.Should().Be(SkipKind.GameFileOwnVersion);
    }
}
