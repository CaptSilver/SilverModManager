using System.Security.Cryptography;
using FluentAssertions;
using SilverModManager.Games.Generic.ModProvenance;

namespace SilverModManager.Games.Generic.Tests.ModProvenance;

public class ModIdentifierTests : IDisposable
{
    private readonly Tree _tree = new();

    public void Dispose() => _tree.Dispose();

    private const string Fluorine = "home/.var/app/com.fluorine.manager/data/instances/Elden";

    private GameIdentity Game => new(_tree.GameFolder, ["Elden Ring"], 3333);

    private static ModClaim LockClaim(string mod, uint? modId, uint? fileId)
        => new(mod, ClaimMethod.LockFile, "erm", ClaimSource.Nexus, modId, fileId);

    private void MoManages(string mod, string modId, string fileId)
    {
        Directory.CreateDirectory(_tree.GameFolder);
        _tree.Write($"{Fluorine}/ModOrganizer.ini", Tree.Mo2Ini("Elden Ring", _tree.GameFolder));
        _tree.Write($"{Fluorine}/mods/{mod}/meta.ini", Tree.MetaIni(modId, fileId));
    }

    private Task<IdentifyResult> Identify(string[] mods, IReadOnlyList<ModClaim>? lockClaims = null, IArchiveCatalog? catalog = null, string[]? extraArchiveFolders = null)
        => new ModIdentifier().IdentifyAsync(new IdentifyRequest(Game, mods, new ManagerSearch(_tree.Home, [], extraArchiveFolders ?? []), lockClaims ?? []), catalog, default);

    [Fact]
    public async Task AManagersRecordsBeatTheLockFileAndTheDisagreementIsNamed()
    {
        MoManages("NoWeight", "10", "20");

        var result = await Identify(["NoWeight"], [LockClaim("noweight", 10, 99)]);

        var mod = result.Mods.Should().ContainSingle().Subject;
        mod.Chosen!.Method.Should().Be(ClaimMethod.ManagerRecords);
        mod.Chosen.FileId.Should().Be(20u);
        mod.Disagreements.Should().ContainSingle().Which.Should().Contain("Fluorine").And.Contain("erm").And.Contain("99");
    }

    [Fact]
    public async Task ClaimsThatAgreeRecordNoDisagreement()
    {
        MoManages("NoWeight", "10", "20");

        var mod = (await Identify(["NoWeight"], [LockClaim("NoWeight", 10, 20)])).Mods.Single();

        mod.Chosen!.Method.Should().Be(ClaimMethod.ManagerRecords);
        mod.Disagreements.Should().BeEmpty();
    }

    [Fact]
    public async Task TheLockFileIsUsedWhenNoManagerKnowsTheMod()
    {
        Directory.CreateDirectory(_tree.GameFolder);

        var mod = (await Identify(["adjust-the-fov"], [LockClaim("adjust-the-fov", 325, 5087)])).Mods.Single();

        mod.Chosen!.Method.Should().Be(ClaimMethod.LockFile);
    }

    [Fact]
    public async Task AModNobodyKnowsHasNoChoice()
    {
        Directory.CreateDirectory(_tree.GameFolder);

        var result = await Identify(["Mystery"]);

        result.Mods.Single().Chosen.Should().BeNull();
    }

    [Fact]
    public async Task AnArchiveInADownloadFolderIdentifiesTheModByItsNamePart()
    {
        Directory.CreateDirectory(_tree.GameFolder);
        var bytes = new byte[] { 5, 5, 5 };
        var md5 = Convert.ToHexStringLower(MD5.HashData(bytes));
        _tree.WriteBytes("dl/SkipTheIntro-421-4-0-1732388874.zip", bytes);
        var catalog = new FakeArchiveCatalog();
        catalog.Hits[md5] = new ArchiveHit(md5, "SkipTheIntro-421-4-0-1732388874.zip", 3333, 421, 32781);

        var mod = (await Identify(["Skip The Intro", "Other"], catalog: catalog, extraArchiveFolders: [_tree.Path_("dl")])).Mods.First();

        mod.Chosen!.Method.Should().Be(ClaimMethod.Archive);
        mod.Chosen.FileId.Should().Be(32781u);
        mod.Chosen.ModId.Should().Be(421u);
    }

    [Fact]
    public async Task ARecordNamingTheModButNoFileTakesTheFileIdFromItsArchive()
    {
        Directory.CreateDirectory(_tree.GameFolder);
        _tree.Write($"{Fluorine}/ModOrganizer.ini", Tree.Mo2Ini("Elden Ring", _tree.GameFolder));
        _tree.Write($"{Fluorine}/mods/SkipTheIntro/meta.ini", Tree.MetaIni("421", null));
        var bytes = new byte[] { 5, 5, 5 };
        var md5 = Convert.ToHexStringLower(MD5.HashData(bytes));
        _tree.WriteBytes("dl/SkipTheIntro-421-4-0-1732388874.zip", bytes);
        var catalog = new FakeArchiveCatalog();
        catalog.Hits[md5] = new ArchiveHit(md5, "SkipTheIntro-421-4-0-1732388874.zip", 3333, 421, 32781);

        var mod = (await Identify(["SkipTheIntro"], catalog: catalog, extraArchiveFolders: [_tree.Path_("dl")])).Mods.Single();

        mod.Chosen!.Method.Should().Be(ClaimMethod.ManagerRecords);
        mod.Chosen.FileId.Should().Be(32781u);
        mod.Disagreements.Should().BeEmpty();
    }

    [Fact]
    public async Task AnArchiveOfAnotherModPageGivesARecordNoFileId()
    {
        Directory.CreateDirectory(_tree.GameFolder);
        _tree.Write($"{Fluorine}/ModOrganizer.ini", Tree.Mo2Ini("Elden Ring", _tree.GameFolder));
        _tree.Write($"{Fluorine}/mods/SkipTheIntro/meta.ini", Tree.MetaIni("999", null));
        var bytes = new byte[] { 5, 5, 5 };
        var md5 = Convert.ToHexStringLower(MD5.HashData(bytes));
        _tree.WriteBytes("dl/SkipTheIntro-421-4-0-1732388874.zip", bytes);
        var catalog = new FakeArchiveCatalog();
        catalog.Hits[md5] = new ArchiveHit(md5, "SkipTheIntro-421-4-0-1732388874.zip", 3333, 421, 32781);

        var mod = (await Identify(["SkipTheIntro"], catalog: catalog, extraArchiveFolders: [_tree.Path_("dl")])).Mods.Single();

        mod.Chosen!.FileId.Should().BeNull();
        mod.Disagreements.Should().ContainSingle();
    }

    [Fact]
    public async Task AnArchiveForAnotherNameIsNotAttachedToTheMod()
    {
        Directory.CreateDirectory(_tree.GameFolder);
        var bytes = new byte[] { 5, 5, 5 };
        var md5 = Convert.ToHexStringLower(MD5.HashData(bytes));
        _tree.WriteBytes("dl/NoWeightPlus-421-4-0-1732388874.zip", bytes);
        var catalog = new FakeArchiveCatalog();
        catalog.Hits[md5] = new ArchiveHit(md5, "NoWeightPlus-421-4-0-1732388874.zip", 3333, 421, 32781);

        var mod = (await Identify(["NoWeight"], catalog: catalog, extraArchiveFolders: [_tree.Path_("dl")])).Mods.Single();

        mod.Chosen.Should().BeNull();
    }

    [Fact]
    public async Task NothingIsHashedWhenEveryModAlreadyHasAFileId()
    {
        MoManages("NoWeight", "10", "20");
        _tree.WriteBytes("dl/a.zip", [1]);
        var catalog = new FakeArchiveCatalog();

        await Identify(["NoWeight"], catalog: catalog, extraArchiveFolders: [_tree.Path_("dl")]);

        catalog.Looked.Should().BeEmpty();
    }

    [Fact]
    public async Task AFailingLookupLeavesTheOtherSourcesAndReportsAProblem()
    {
        Directory.CreateDirectory(_tree.GameFolder);
        _tree.WriteBytes("dl/a.zip", [1]);
        var catalog = new FakeArchiveCatalog { Fail = true };

        var result = await Identify(["adjust-the-fov"], [LockClaim("adjust-the-fov", 325, null)], catalog, [_tree.Path_("dl")]);

        result.Mods.Single().Chosen!.Method.Should().Be(ClaimMethod.LockFile);
        result.Problems.Should().ContainSingle().Which.Should().Contain("boom");
    }

    [Fact]
    public async Task AThunderstorePackageIsMatchedByItsFullOrShortName()
    {
        Directory.CreateDirectory(_tree.GameFolder);
        _tree.Write("home/.config/r2modmanPlus-local/Elden Ring/profiles/Default/mods.yml", "- name: Owner-CoolMod\n  versionNumber: 1.0.0\n");

        var result = await Identify(["CoolMod", "Owner-CoolMod"]);

        result.Mods.Select(m => m.Chosen?.Package).Should().Equal("Owner-CoolMod", "Owner-CoolMod");
    }

    [Fact]
    public async Task ARecordIsMatchedByItsArchiveNameToo()
    {
        Directory.CreateDirectory(_tree.GameFolder);
        _tree.Write($"{Fluorine}/ModOrganizer.ini", Tree.Mo2Ini("Elden Ring", _tree.GameFolder));
        _tree.Write($"{Fluorine}/mods/Renamed by user/meta.ini", Tree.MetaIni("421", "32781", "4.0", "SkipTheIntro-421-4-0-1732388874.zip"));

        var mod = (await Identify(["SkipTheIntro"])).Mods.Single();

        mod.Chosen!.FileId.Should().Be(32781u);
    }
}
