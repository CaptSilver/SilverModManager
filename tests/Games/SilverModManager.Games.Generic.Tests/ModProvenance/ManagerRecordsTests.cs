using FluentAssertions;
using SilverModManager.Games.Generic.ModProvenance;

namespace SilverModManager.Games.Generic.Tests.ModProvenance;

public class ManagerRecordsTests : IDisposable
{
    private readonly Tree _tree = new();

    public void Dispose() => _tree.Dispose();

    private GameIdentity Game => new(_tree.GameFolder, ["Elden Ring"]);

    private ManagerRecordResult Read(params string[] extraRoots)
    {
        Directory.CreateDirectory(_tree.GameFolder);
        return ManagerRecords.Read(new ManagerSearch(_tree.Home, extraRoots, []), Game);
    }

    private const string Fluorine = "home/.var/app/com.fluorine.manager/data/instances/Elden";

    [Fact]
    public void AFluorineInstanceForThisGameGivesItsModsExactIds()
    {
        _tree.Write($"{Fluorine}/ModOrganizer.ini", Tree.Mo2Ini("Elden Ring", _tree.GameFolder));
        _tree.Write($"{Fluorine}/mods/NoWeight/meta.ini", Tree.MetaIni("1234", "5678", "2.1", "NoWeight-1234-2-1-1700000000.zip", "Nexus"));

        var result = Read();

        result.Claims.Should().ContainSingle().Which.Should().BeEquivalentTo(new ModClaim(
            "NoWeight", ClaimMethod.ManagerRecords, "Fluorine", ClaimSource.Nexus, ModId: 1234, FileId: 5678, Version: "2.1", Archive: "NoWeight-1234-2-1-1700000000.zip"));
    }

    [Fact]
    public void APlainMo2InstanceIsFoundUnderAnExtraRootAndNamedAsMo2()
    {
        var prefix = _tree.Path_("prefix");
        _tree.Write("prefix/drive_c/Modding/MO2/ModOrganizer.ini", Tree.Mo2Ini("Elden Ring", _tree.GameFolder));
        _tree.Write("prefix/drive_c/Modding/MO2/mods/Faster/meta.ini", Tree.MetaIni("9", "10"));

        var result = Read(prefix);

        result.Claims.Should().ContainSingle().Which.Origin.Should().Be("Mod Organizer 2");
    }

    [Fact]
    public void AnInstanceForAnotherGameIsIgnored()
    {
        _tree.Write($"{Fluorine}/ModOrganizer.ini", Tree.Mo2Ini("Skyrim Special Edition", _tree.Path_("Skyrim")));
        _tree.Write($"{Fluorine}/mods/Skyui/meta.ini", Tree.MetaIni("3863", "1"));

        Read().Claims.Should().BeEmpty();
    }

    [Fact]
    public void AGamePathThatResolvesElsewhereRulesOutAnInstanceEvenWithTheSameGameName()
    {
        _tree.Write($"{Fluorine}/ModOrganizer.ini", Tree.Mo2Ini("Elden Ring", _tree.Path_("OtherInstall")));
        _tree.Write($"{Fluorine}/mods/A/meta.ini", Tree.MetaIni("1", "2"));

        Read().Claims.Should().BeEmpty();
    }

    [Fact]
    public void AGamePathAtTheDriveRootDoesNotClaimEveryGame()
    {
        _tree.Write($"{Fluorine}/ModOrganizer.ini", Tree.Mo2Ini("Elden Ring", "/"));
        _tree.Write($"{Fluorine}/mods/A/meta.ini", Tree.MetaIni("1", "2"));

        Read().Claims.Should().BeEmpty();
    }

    [Fact]
    public void AGamePathTwoFoldersAboveTheGameFolderIsAnotherPlace()
    {
        _tree.Write($"{Fluorine}/ModOrganizer.ini", Tree.Mo2Ini("Elden Ring", Path.GetDirectoryName(_tree.Root)!));
        _tree.Write($"{Fluorine}/mods/A/meta.ini", Tree.MetaIni("1", "2"));

        Read().Claims.Should().BeEmpty();
    }

    [Fact]
    public void AGamePathAtTheInstallFolderAboveTheGameFolderMatches()
    {
        _tree.Write($"{Fluorine}/ModOrganizer.ini", Tree.Mo2Ini("Other Name", _tree.Root));
        _tree.Write($"{Fluorine}/mods/A/meta.ini", Tree.MetaIni("1", "2"));

        Read().Claims.Should().ContainSingle();
    }

    [Fact]
    public void AGamePathInsideAPrefixTheReaderCannotResolveFallsBackToTheGameName()
    {
        _tree.Write($"{Fluorine}/ModOrganizer.ini", "[General]\ngameName=Elden Ring\ngamePath=@ByteArray(D:\\\\Games\\\\EldenRing)\n");
        _tree.Write($"{Fluorine}/mods/A/meta.ini", Tree.MetaIni("1", "2"));

        Read().Claims.Should().ContainSingle();
    }

    [Fact]
    public void ThePathsOfTheInstanceAndTheGameAreComparedByWhereTheyPoint()
    {
        var real = _tree.Path_("real/Game");
        Directory.CreateDirectory(real);
        Directory.CreateSymbolicLink(_tree.GameFolder, real);
        _tree.Write($"{Fluorine}/ModOrganizer.ini", Tree.Mo2Ini("Other Name", real));
        _tree.Write($"{Fluorine}/mods/A/meta.ini", Tree.MetaIni("1", "2"));

        ManagerRecords.Read(new ManagerSearch(_tree.Home, [], []), Game).Claims.Should().ContainSingle();
    }

    [Fact]
    public void ConfiguredModAndDownloadFoldersAreFollowed()
    {
        var settings = $"base_directory=@ByteArray({Tree.WinPath(_tree.Path_("data")).Replace("\\", "\\\\")})\n"
                       + "mod_directory=%BASE_DIR%/my-mods\ndownload_directory=%BASE_DIR%/dl";
        _tree.Write($"{Fluorine}/ModOrganizer.ini", Tree.Mo2Ini("Elden Ring", _tree.GameFolder, settings));
        _tree.Write("data/my-mods/A/meta.ini", Tree.MetaIni("1", "2"));
        Directory.CreateDirectory(_tree.Path_("data/dl"));

        var result = Read();

        result.Claims.Should().ContainSingle();
        result.ArchiveFolders.Should().Contain(Path.GetFullPath(_tree.Path_("data/dl")));
    }

    [Fact]
    public void AModFromAnotherRepositoryIsNotClaimedAsNexus()
    {
        _tree.Write($"{Fluorine}/ModOrganizer.ini", Tree.Mo2Ini("Elden Ring", _tree.GameFolder));
        _tree.Write($"{Fluorine}/mods/A/meta.ini", Tree.MetaIni("1", "2", repository: "GitHub"));

        Read().Claims.Should().BeEmpty();
    }

    [Fact]
    public void AModWithOnlyAModIdIsClaimedWithoutAFileId()
    {
        _tree.Write($"{Fluorine}/ModOrganizer.ini", Tree.Mo2Ini("Elden Ring", _tree.GameFolder));
        _tree.Write($"{Fluorine}/mods/A/meta.ini", Tree.MetaIni("77", null, "3.0"));

        var claim = Read().Claims.Should().ContainSingle().Subject;
        claim.ModId.Should().Be(77);
        claim.FileId.Should().BeNull();
    }

    [Fact]
    public void AModInstalledByHandHasNoClaim()
    {
        _tree.Write($"{Fluorine}/ModOrganizer.ini", Tree.Mo2Ini("Elden Ring", _tree.GameFolder));
        _tree.Write($"{Fluorine}/mods/Manual/meta.ini", Tree.MetaIni("0", null));
        Directory.CreateDirectory(_tree.Path_($"{Fluorine}/mods/NoMeta"));

        Read().Claims.Should().BeEmpty();
    }

    [Fact]
    public void AGarbledMetaFileGivesNoClaimAndNoException()
    {
        _tree.Write($"{Fluorine}/ModOrganizer.ini", Tree.Mo2Ini("Elden Ring", _tree.GameFolder));
        _tree.WriteBytes($"{Fluorine}/mods/Junk/meta.ini", [0, 1, 2, 255, 254, 61, 61, 91, 93, 10, 0]);
        _tree.Write($"{Fluorine}/mods/Good/meta.ini", Tree.MetaIni("5", "6"));
        _tree.Write($"{Fluorine}/mods/Huge/meta.ini", "[General]\nmodid=99999999999999999999\n[installedFiles]\n1\\fileid=abc\n");

        Read().Claims.Select(c => c.Mod).Should().Equal("Good");
    }

    [Fact]
    public void AnInstanceFileThatIsNotReadableIsReportedAndOthersAreStillRead()
    {
        _tree.Write($"{Fluorine}/ModOrganizer.ini", Tree.Mo2Ini("Elden Ring", _tree.GameFolder));
        _tree.Write($"{Fluorine}/mods/Good/meta.ini", Tree.MetaIni("5", "6"));
        _tree.Write("home/.var/app/com.fluorine.manager/data/instances/Bad/ModOrganizer.ini", new string('x', 3_000_000));

        var result = Read();

        result.Claims.Should().ContainSingle();
        result.Problems.Should().ContainSingle().Which.Should().Contain("too large");
    }

    [Fact]
    public void VortexDownloadsAreOfferedAsArchiveFoldersAndTheLimitIsSaid()
    {
        var downloads = _tree.Path_("prefix/drive_c/users/steamuser/AppData/Roaming/Vortex/downloads");
        Directory.CreateDirectory(Path.Combine(downloads, "eldenring"));

        var result = Read(_tree.Path_("prefix"));

        result.ArchiveFolders.Should().Contain(Path.GetFullPath(downloads));
        result.Problems.Should().ContainSingle().Which.Should().Contain("Vortex").And.Contain("downloads");
    }

    [Fact]
    public void ThunderstoreProfilesOfThisGameGivePackageNamesAndVersions()
    {
        _tree.Write("home/.config/r2modmanPlus-local/Elden Ring/profiles/Default/mods.yml", """
            - manifestVersion: 1
              name: Owner-CoolMod
              authorName: Owner
              version:
                major: 1
                minor: 2
                patch: 3
              enabled: true
            - name: Other-Thing
              versionNumber: 0.4.0
              enabled: false
            """);
        _tree.Write("home/.config/r2modmanPlus-local/Valheim/profiles/Default/mods.yml", "- name: Not-Ours\n  versionNumber: 1.0.0\n");

        var claims = Read().Claims;

        claims.Select(c => (c.Mod, c.Source, c.Package, c.Version, c.Origin)).Should().BeEquivalentTo(
        [
            ("Owner-CoolMod", ClaimSource.Thunderstore, "Owner-CoolMod", "1.2.3", "r2modman"),
            ("Other-Thing", ClaimSource.Thunderstore, "Other-Thing", "0.4.0", "r2modman"),
        ]);
    }

    [Fact]
    public void AGaleProfileInJsonIsRead()
    {
        _tree.Write("home/.local/share/com.kesomannen.gale/elden-ring/profiles/Default/mods.json", """
            {"mods":[{"name":"Owner-GaleMod","version":{"major":2,"minor":0,"patch":1}},{"fullName":"A-B","version":"1.0.0"}]}
            """);
        var game = new GameIdentity(_tree.GameFolder, ["Elden Ring"]);

        var claims = ManagerRecords.Read(new ManagerSearch(_tree.Home, [], []), game).Claims;

        claims.Select(c => (c.Package, c.Version, c.Origin)).Should().BeEquivalentTo([("Owner-GaleMod", "2.0.1", "Gale"), ("A-B", "1.0.0", "Gale")]);
    }

    [Fact]
    public void GarbledThunderstoreProfilesAreSkippedAndReported()
    {
        _tree.Write("home/.config/r2modmanPlus-local/Elden Ring/profiles/Default/mods.yml", "{{{ not : [yaml");
        _tree.Write("home/.config/r2modmanPlus-local/Elden Ring/profiles/Two/mods.yml", "- name: Good-One\n  versionNumber: 1.0.0\n");

        var result = Read();

        result.Claims.Select(c => c.Package).Should().Equal("Good-One");
    }

    [Fact]
    public void NothingInstalledGivesAnEmptyResult()
    {
        var result = Read();

        result.Claims.Should().BeEmpty();
        result.Problems.Should().BeEmpty();
    }
}
