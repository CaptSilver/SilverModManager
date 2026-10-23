using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Abstractions.Games.FileHashes;
using NexusMods.Games.FromSoft.Build;
using NexusMods.Games.FromSoft.Heal;
using NexusMods.Games.FromSoft.Merge;
using NexusMods.Games.FromSoft.Tests.Fixtures;
using NexusMods.Hashing.xxHash3;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.NexusModsApi;
using NexusMods.Paths;
using NSubstitute;

namespace NexusMods.Games.FromSoft.Tests.Merge;

public class VanillaDumpsTests(ITestOutputHelper helper) : AVanillaDumpTest<VanillaDumpsTests>(helper)
{
    private VanillaDumps Dumps => ServiceProvider.GetRequiredService<VanillaDumps>();

    private static readonly byte[] Msg = "msg"u8.ToArray();
    private static readonly byte[] Talk = "talk"u8.ToArray();

    private Task<NexusMods.Sdk.Library.LibraryArchive.ReadOnly> AddDump(string name, byte[] regulation)
        => AddArchive(name,
            ("Vanilla/regulation.bin", regulation),
            ("Vanilla/msg/x.msgbnd.dcx", Msg),
            ("Vanilla/m00.talkesdbnd.dcx", Talk));

    [Fact]
    public async Task AFolderHoldingARegulationTheBaselinesVouchForIsADumpWithItsSiblingsAsMembers()
    {
        var regulation = Regulation(OlderVersion, 0xB0);
        KeepBaseline(OlderVersion, regulation);
        await AddDump("Dump", regulation);

        var dump = Dumps.Find(Connection.Db, GameInstallation).Should().ContainSingle().Subject;

        dump.Root.Should().Be("Vanilla");
        dump.Version.Should().Be(OlderVersion);
        dump.ArchiveName.Should().Be("Dump.zip");
        dump.Members.Keys.Should().BeEquivalentTo("regulation.bin", "msg/x.msgbnd.dcx", "m00.talkesdbnd.dcx");
    }

    [Fact]
    public async Task AMemberIsReachableByItsPathAndOtherwiseByAFileNameUniqueInTheDump()
    {
        var regulation = Regulation(OlderVersion, 0xB0);
        KeepBaseline(OlderVersion, regulation);
        await AddDump("Dump", regulation);
        var dump = Dumps.Find(Connection.Db, GameInstallation).Single();

        Dumps.Member(dump, "msg/x.msgbnd.dcx").Should().Equal(Msg);
        Dumps.Member(dump, "script/talk/m00.talkesdbnd.dcx").Should().Equal(Talk, "erm's own declaration reaches the dump's flat copy by name");
        var act = () => Dumps.Member(dump, "script/talk/other.talkesdbnd.dcx");
        act.Should().Throw<MergeException>().Which.Message.Should().Contain("Dump.zip").And.Contain("other.talkesdbnd.dcx");
    }

    [Fact]
    public async Task APathsAnswerNamesTheWantedPathsSomeDumpOffers()
    {
        var regulation = Regulation(OlderVersion, 0xB0);
        KeepBaseline(OlderVersion, regulation);
        await AddDump("Dump", regulation);

        var offered = Dumps.Paths(Connection.Db, GameInstallation, ["msg/x.msgbnd.dcx", "script/talk/m00.talkesdbnd.dcx", "map/other.mapbnd.dcx"]);

        offered.Should().BeEquivalentTo("msg/x.msgbnd.dcx", "script/talk/m00.talkesdbnd.dcx");
    }

    [Fact]
    public async Task ADumpNothingVouchesForIsNotRecognisedAndTheReportNamesIt()
    {
        await AddDump("Dump", Regulation(OlderVersion, 0xB0));

        Dumps.Find(Connection.Db, GameInstallation).Should().BeEmpty();
        Dumps.Paths(Connection.Db, GameInstallation, ["msg/x.msgbnd.dcx"]).Should().BeEmpty();
        Dumps.Unconfirmed(Connection.Db, GameInstallation).Should().ContainSingle().Which.Describe()
            .Should().Be($"archive Dump.zip holds a regulation.bin of version {OlderVersion} the app cannot confirm as vanilla");
    }

    [Fact]
    public async Task AModsOwnRegulationAtTheTopOfItsArchiveIsNotADump()
    {
        KeepBaseline(OlderVersion, Regulation(OlderVersion, 0xB0));
        await AddArchive("Mod", ("regulation.bin", Regulation(OlderVersion, 7)), ("msg/x.msgbnd.dcx", Msg));

        Dumps.Find(Connection.Db, GameInstallation).Should().BeEmpty();
        Dumps.Paths(Connection.Db, GameInstallation, ["msg/x.msgbnd.dcx"]).Should().BeEmpty();
        Dumps.Unconfirmed(Connection.Db, GameInstallation).Should().BeEmpty("every mod ships a regulation, and only a folder called Vanilla is taken for a dump");
    }

    [Fact]
    public async Task ADumpOfTheInstalledRegulationIsVouchedForByTheLiveBuild()
    {
        await AddDump("Dump", EldenRingInstall.RegulationFile);

        var dump = Dumps.Find(Connection.Db, GameInstallation).Should().ContainSingle().Subject;

        dump.Version.Should().Be(GameVersion);
    }

    [Fact]
    public async Task EveryFolderHoldingAVouchedRegulationIsItsOwnDump()
    {
        var regulation = Regulation(OlderVersion, 0xB0);
        KeepBaseline(OlderVersion, regulation);
        await AddArchive("Two", ("a/regulation.bin", regulation), ("b/regulation.bin", regulation), ("b/x.dcx", Msg));

        Dumps.Find(Connection.Db, GameInstallation).Select(d => d.Root).Should().Equal("a", "b");
    }

    private (VouchedRegulations Vouched, IFileHashesService Service) Vouching(Action<IFileHashesService> arrange)
    {
        var service = Substitute.For<IFileHashesService>();
        service.Databases.Returns(Array.Empty<NexusMods.MnemonicDB.Abstractions.IDb>());
        arrange(service);
        var baselines = ServiceProvider.GetRequiredService<RegulationBaselines>();
        return (new VouchedRegulations(service, baselines, new InstalledBuilds(), NullLogger<VouchedRegulations>.Instance), service);
    }

    private static GameFileRecord Record(string path, ulong hash) => new()
    {
        Path = new GamePath(LocationId.Game, path),
        Size = Size.From(1),
        MinimalHash = Hash.From(hash),
        Hash = Hash.From(hash),
    };

    private void Knows(IFileHashesService service, params (string Version, GameFileRecord[] Files)[] builds)
    {
        service.GetKnownVanityVersions(GameInstallation.Game.NexusModsGameId.Value).Returns(builds.Select(b => VanityVersion.From(b.Version)).ToArray());
        foreach (var (version, files) in builds)
        {
            var ids = new[] { LocatorId.From("depot-" + version) };
            service.TryGetLocatorIdsForVanityVersion(GameInstallation.LocatorResult.Store, VanityVersion.From(version), out Arg.Any<LocatorId[]>())
                .Returns(call => { call[2] = ids; return true; });
            service.GetGameFiles(new LocatorIdsWithGameStore(GameInstallation.LocatorResult.Store, ids)).Returns(files);
        }
    }

    [Fact]
    public void TheHashDatabaseVouchesForTheRegulationOfEveryBuildItKnowsAndNothingElse()
    {
        var (vouched, _) = Vouching(service =>
        {
            Knows(service,
                ("1.0.0", [Record("Game/regulation.bin", 0xA1), Record("Game/eldenring.exe", 0xA2)]),
                ("1.1.0", [Record("Game/regulation.bin", 0xB1)]));
        });

        var hashes = vouched.Hashes(GameInstallation);

        hashes.Should().Contain([Hash.From(0xA1UL), Hash.From(0xB1UL)]);
        hashes.Should().NotContain(Hash.From(0xA2UL), "only the regulation's path counts");
    }

    [Fact]
    public void ADatabaseThatDoesNotCoverTheGameAddsNothing()
    {
        var (vouched, _) = Vouching(service => service.GetKnownVanityVersions(GameInstallation.Game.NexusModsGameId.Value).Returns(Array.Empty<VanityVersion>()));

        var hashes = vouched.Hashes(GameInstallation);

        hashes.Should().BeEquivalentTo([EldenRingInstall.RegulationFile.xxHash3()], "only the live install vouches");
    }

    [Fact]
    public void TheDatabaseIsWalkedOnceUntilItsSetOfDatabasesChanges()
    {
        var (vouched, service) = Vouching(s => Knows(s, ("1.0.0", [Record("Game/regulation.bin", 0xA1)])));

        vouched.Hashes(GameInstallation);
        vouched.Hashes(GameInstallation);
        service.ReceivedWithAnyArgs(1).GetKnownVanityVersions(default);

        service.Databases.Returns([Connection.Db]);
        vouched.Hashes(GameInstallation);
        service.ReceivedWithAnyArgs(2).GetKnownVanityVersions(default);
    }
}
