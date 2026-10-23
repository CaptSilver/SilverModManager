using System.Collections.Immutable;
using FluentAssertions;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.Larian.BaldursGate3;
using SilverModManager.Games.Larian.BaldursGate3.GameInstalled;
using SilverModManager.Games.TestFramework;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using NSubstitute;

namespace SilverModManager.Games.Larian.Tests.BaldursGate3.GameInstalled;

public sealed class Bg3InGameModRecognizerTests : IDisposable
{
    private static readonly GamePath ModsettingsPath = new(Bg3Constants.PlayerProfilesLocationId, "modsettings.lsx");

    private readonly TemporaryPath _root;
    private readonly AbsolutePath _mods;
    private readonly AbsolutePath _profiles;
    private readonly GameInstallation _installation;
    private readonly Bg3InGameModRecognizer _recognizer = new();

    public Bg3InGameModRecognizerTests(TemporaryFileManager temporaryFileManager)
    {
        _root = temporaryFileManager.CreateFolder("bg3-in-game-mods");
        _mods = _root.Path.Combine("Mods");
        _profiles = _root.Path.Combine("PlayerProfiles");
        _mods.CreateDirectory();
        _profiles.CreateDirectory();

        var game = Substitute.For<IGameData>();
        var result = new GameLocatorResult
        {
            Game = game,
            Path = _root.Path.Combine("Game"),
            Store = GameStore.Steam,
            StoreIdentifier = "1",
            LocatorIds = ImmutableArray<LocatorId>.Empty,
            Locator = Substitute.For<IGameLocator>(),
        };
        var locations = ImmutableDictionary<LocationId, AbsolutePath>.Empty
            .Add(Bg3Constants.ModsLocationId, _mods)
            .Add(Bg3Constants.PlayerProfilesLocationId, _profiles);
        _installation = new GameInstallation(result, GameLocations.Create(locations));
    }

    public void Dispose() => _root.Dispose();

    private void WritePak(string fileName, byte[] bytes) => _mods.Combine(fileName).WriteAllBytesAsync(bytes).GetAwaiter().GetResult();

    private void WriteModsettings(string xml) => _profiles.Combine("modsettings.lsx").WriteAllTextAsync(xml).GetAwaiter().GetResult();

    private static string Modsettings(params (string Folder, string Name, string Handle, ulong Version)[] entries)
    {
        var nodes = string.Concat(entries.Select(e => $"""
                <node id="ModuleShortDesc">
                    <attribute id="Folder" type="LSString" value="{e.Folder}"/>
                    <attribute id="MD5" type="LSString" value=""/>
                    <attribute id="Name" type="LSString" value="{e.Name}"/>
                    <attribute id="PublishHandle" type="uint64" value="{e.Handle}"/>
                    <attribute id="UUID" type="guid" value="11111111-2222-3333-4444-555555555555"/>
                    <attribute id="Version64" type="int64" value="{e.Version}"/>
                </node>
            """));
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <save>
                <version major="4" minor="8" revision="0" build="400"/>
                <region id="ModuleSettings"><node id="root"><children><node id="Mods"><children>
            {nodes}
                </children></node></children></node></region>
            </save>
            """;
    }

    private IReadOnlyDictionary<GamePath, SyncNodePart> DiskState()
    {
        var state = new Dictionary<GamePath, SyncNodePart>();
        foreach (var file in _mods.EnumerateFiles())
            state[new GamePath(Bg3Constants.ModsLocationId, file.FileName)] = Part(file);
        var modsettings = _profiles.Combine("modsettings.lsx");
        if (modsettings.FileExists) state[ModsettingsPath] = Part(modsettings);
        return state;
    }

    private static SyncNodePart Part(AbsolutePath file) => new()
    {
        Hash = Hash.Zero,
        Size = file.FileInfo.Size,
        LastModifiedTicks = file.FileInfo.LastWriteTimeUtc.Ticks,
    };

    private async Task<GameInstalledRecognition> Recognize()
        => await _recognizer.Recognize(_installation, DiskState(), CancellationToken.None);

    private static GamePath ModPath(string fileName) => new(Bg3Constants.ModsLocationId, fileName);

    [Fact]
    public void RecordPaths_IsEmpty() => _recognizer.RecordPaths.Should().BeEmpty();

    [Fact]
    public async Task RecordMatch_CarriesTheEntryAsExtra()
    {
        WriteModsettings(Modsettings(
            ("GustavX", "GustavX", "0", 36028797018963968),
            ("Cool_Mod_9f8e7d6c", "Cool Mod", "1234567", SyntheticPak.Version(1, 2, 3))));
        // The pak has no meta.lsx worth reading: the record alone is enough.
        WritePak("cool_mod_9f8e7d6c-ab12.pak", SyntheticPak.Build(("readme.txt", "x")));

        var result = await Recognize();

        result.RecordsReadable.Should().BeTrue();
        var item = result.Items.Should().ContainSingle().Subject;
        item.Source.Should().Be("Bg3InGameMod");
        item.SourceId.Should().Be("cool_mod_9f8e7d6c-ab12.pak");
        item.Title.Should().Be("Cool Mod");
        item.Version.Should().Be("1.2.3");
        item.Confidence.Should().Be(GameInstalledConfidence.GameRecord);
        item.CanBeClaimed.Should().BeTrue();
        item.Files.Should().Equal(ModPath("cool_mod_9f8e7d6c-ab12.pak"));
        item.Extra.Should().Be(new ModsettingsDocument.ModuleEntry(
            "Cool_Mod_9f8e7d6c", "Cool Mod", "1234567", "11111111-2222-3333-4444-555555555555",
            SyntheticPak.Version(1, 2, 3).ToString(), ""));
        _recognizer.MetaReads.Should().Be(0);
    }

    [Fact]
    public async Task RecordMatch_MatchesATruncatedFolder()
    {
        WriteModsettings(Modsettings(("AZA_SHART_HalfPlate_edec7b9d-0-eofu_long", "Half Plate", "42", 1)));
        WritePak("aza_shart_halfplate_edec7b9d-0-ab12.pak", SyntheticPak.Build(("readme.txt", "x")));

        var item = (await Recognize()).Items.Should().ContainSingle().Subject;
        item.Confidence.Should().Be(GameInstalledConfidence.GameRecord);
        item.Title.Should().Be("Half Plate");
    }

    [Fact]
    public async Task RecordMatch_VersionIsNullWhenVersion64IsNotNumeric()
    {
        var xml = Modsettings(("mymod", "My Mod", "42", 1)).Replace("value=\"1\"/>", "value=\"not-a-number\"/>");
        WriteModsettings(xml);
        WritePak("mymod-ab12.pak", SyntheticPak.Build(("readme.txt", "x")));

        var item = (await Recognize()).Items.Should().ContainSingle().Subject;
        item.Version.Should().BeNull();
    }

    [Fact]
    public async Task RecordMatch_AnEntryNamingTheFolderBeatsOneThatOnlyStartsWithTheHead()
    {
        WriteModsettings(Modsettings(
            ("mymod_extended", "Extended", "2", 1),
            ("MyMod", "My Mod", "1", 1)));
        WritePak("mymod-ab12.pak", SyntheticPak.Build(("readme.txt", "x")));

        var item = (await Recognize()).Items.Should().ContainSingle().Subject;
        item.Title.Should().Be("My Mod");
    }

    [Fact]
    public async Task RecordMatch_TwoEntriesThatBothOnlyStartWithTheHeadIsNoRecordMatch()
    {
        WriteModsettings(Modsettings(
            ("mymod_extended", "Extended", "2", 1),
            ("mymod_deluxe", "Deluxe", "3", 1)));
        // Not a published module either, so nothing is left to recognise it by.
        WritePak("mymod-ab12.pak", SyntheticPak.ForModule("mymod", "My Mod", "uuid", 1));

        (await Recognize()).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task RecordMatch_IgnoresEntriesWithoutAPublishHandle()
    {
        WriteModsettings(Modsettings(("mymod", "My Mod", "0", 1)));
        WritePak("mymod-ab12.pak", SyntheticPak.ForModule("mymod", "My Mod", "uuid", 1));

        (await Recognize()).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task RecordMatch_IgnoresEntriesWithNoPublishHandleAttribute()
    {
        // Older files and other managers leave the attribute out; that is not a mod.io handle either.
        var xml = Modsettings(("mymod", "My Mod", "REMOVE", 1))
            .Replace("""<attribute id="PublishHandle" type="uint64" value="REMOVE"/>""", "");
        WriteModsettings(xml);
        WritePak("mymod-ab12.pak", SyntheticPak.ForModule("mymod", "My Mod", "uuid", 1));

        (await Recognize()).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Pattern_PakWithAPublishHandleIsRecognisedWithoutARecord()
    {
        WriteModsettings(Modsettings(("GustavX", "GustavX", "0", 1)));
        WritePak("mymod-ab12.pak", SyntheticPak.ForPublishedModule("MyMod", "My Mod", "uuid", SyntheticPak.Version(1), "777"));

        var result = await Recognize();

        var item = result.Items.Should().ContainSingle().Subject;
        item.SourceId.Should().Be("mymod-ab12.pak");
        item.Title.Should().Be("My Mod");
        item.Confidence.Should().Be(GameInstalledConfidence.FileNamePattern);
        item.Extra.Should().BeNull();
        item.CanBeClaimed.Should().BeTrue();
        item.Version.Should().BeNull();
        item.Files.Should().Equal(ModPath("mymod-ab12.pak"));
    }

    [Fact]
    public async Task Pattern_MissingModsettingsStillRecognisesByPattern()
    {
        WritePak("mymod-ab12.pak", SyntheticPak.ForPublishedModule("MyMod", "My Mod", "uuid", 1, "777"));

        var result = await Recognize();

        result.RecordsReadable.Should().BeTrue();
        result.Items.Should().ContainSingle().Which.Confidence.Should().Be(GameInstalledConfidence.FileNamePattern);
    }

    [Fact]
    public async Task Pattern_SameNameWithoutAHandleIsNotAnItem()
    {
        WritePak("mymod-ab12.pak", SyntheticPak.ForModule("MyMod", "My Mod", "uuid", 1));

        (await Recognize()).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task NeitherPatternNorRecord_IsNotAnItem()
    {
        WriteModsettings(Modsettings(("Other", "Other", "5", 1)));
        // A handle in its meta.lsx is not enough without the game's naming.
        WritePak("My Cool Mod.pak", SyntheticPak.ForPublishedModule("MyCoolMod", "My Cool Mod", "uuid", 1, "777"));

        (await Recognize()).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task UnreadablePakWithTheGamesNaming_IsNotAnItem()
    {
        WritePak("mymod-ab12.pak", [1, 2, 3, 4]);

        (await Recognize()).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task OnlyPaksDirectlyInTheModsFolderAreCandidates()
    {
        _mods.Combine("sub").CreateDirectory();
        await _mods.Combine("sub/mymod-ab12.pak").WriteAllBytesAsync(SyntheticPak.ForPublishedModule("MyMod", "My Mod", "uuid", 1, "777"));
        WritePak("mymod-cd34.txt", SyntheticPak.ForPublishedModule("MyMod", "My Mod", "uuid", 1, "777"));
        var state = DiskState().ToDictionary();
        state[new GamePath(Bg3Constants.ModsLocationId, "sub/mymod-ab12.pak")] = Part(_mods.Combine("sub/mymod-ab12.pak"));

        var result = await _recognizer.Recognize(_installation, state, CancellationToken.None);

        result.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task UnreadableModsettings_MeansRecordsAreNotReadable()
    {
        WriteModsettings("<save><version");
        WritePak("mymod-ab12.pak", SyntheticPak.ForPublishedModule("MyMod", "My Mod", "uuid", 1, "777"));

        var result = await Recognize();

        result.RecordsReadable.Should().BeFalse();
        result.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task MetaParseIsCachedBySizeAndWriteTime()
    {
        var pak = _mods.Combine("mymod-ab12.pak");
        WritePak("mymod-ab12.pak", SyntheticPak.ForPublishedModule("MyMod", "My Mod", "uuid", 1, "777"));

        await Recognize();
        await Recognize();
        _recognizer.MetaReads.Should().Be(1);

        // Same size, new write time: the pak is read again.
        File.SetLastWriteTimeUtc(pak.ToString(), DateTime.UtcNow.AddMinutes(5));
        await Recognize();
        _recognizer.MetaReads.Should().Be(2);
    }

    [Fact]
    public async Task MetaParseOfAPakWithoutAHandleIsCachedToo()
    {
        WritePak("mymod-ab12.pak", SyntheticPak.ForModule("MyMod", "My Mod", "uuid", 1));

        await Recognize();
        await Recognize();

        _recognizer.MetaReads.Should().Be(1);
    }
}
