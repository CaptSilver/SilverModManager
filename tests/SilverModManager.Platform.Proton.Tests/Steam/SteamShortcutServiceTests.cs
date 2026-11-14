using FluentAssertions;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Platform.Proton.Steam;
using SilverModManager.Platform.Proton.Steam.BinaryKeyValues;
using SilverModManager.Platform.Proton.Steam.Shortcuts;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Platform.Proton.Tests.Steam;

public class SteamShortcutServiceTests : IDisposable
{
    private const ulong Base = 76561197960265728;
    private const uint Account = 65369667;

    private readonly string _root = Directory.CreateTempSubdirectory("silvermod-shortcut-service-").FullName;
    private readonly IProcessTracker _tracker = Substitute.For<IProcessTracker>();
    private readonly IGameModeDetector _gameMode = Substitute.For<IGameModeDetector>();
    private readonly IEnvironmentReader _environment = Substitute.For<IEnvironmentReader>();
    private readonly List<ISteamShortcutRoute> _extraRoutes = [];
    private string _steamRoot;

    public SteamShortcutServiceTests()
    {
        _steamRoot = Path("steam");
        _environment.Get("XDG_DATA_HOME").Returns(Path("data"));
        WriteLoginUsers((Account, "alice", "Alice", true));
        Directory.CreateDirectory(Config(Account));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Path(string relative) => System.IO.Path.Combine(_root, relative);
    private string Config(uint account) => System.IO.Path.Combine(_steamRoot, "userdata", account.ToString(), "config");
    private string ShortcutsFile(uint account = Account) => System.IO.Path.Combine(Config(account), "shortcuts.vdf");
    private AbsolutePath Abs(string path) => FileSystem.Shared.FromUnsanitizedFullPath(path);
    private LauncherShim Shim => new(Abs(Path("data")));
    private AbsolutePath RecordPath => SteamShortcutRecord.PathIn(Abs(Path("data")));

    private void WriteLoginUsers(params (uint Id, string Name, string Persona, bool MostRecent)[] users)
    {
        Directory.CreateDirectory(System.IO.Path.Combine(_steamRoot, "config"));
        File.WriteAllText(System.IO.Path.Combine(_steamRoot, "config", "loginusers.vdf"),
            "\"users\"\n{\n" + string.Concat(users.Select(u =>
                $"\t\"{Base + u.Id}\"\n\t{{\n\t\t\"AccountName\"\t\t\"{u.Name}\"\n\t\t\"PersonaName\"\t\t\"{u.Persona}\"\n\t\t\"MostRecent\"\t\t\"{(u.MostRecent ? 1 : 0)}\"\n\t}}\n")) + "}\n");
    }

    private SteamShortcutService Service(Func<AbsolutePath?>? steamRoot = null, params ISteamShortcutRoute[] routes)
    {
        var writer = new ShortcutsVdfWriter(new HandWrittenBinaryKeyValues(), new SteamConfigFileEditor(_tracker));
        var allRoutes = routes.Length > 0 ? routes : [new ShortcutsFileRoute(writer), .. _extraRoutes];
        // A shim that already targets an installed binary, so the service does not depend on the test host's path.
        Shim.Write(Abs("/opt/apps/SilverModManager.AppImage"));
        return new SteamShortcutService(_tracker, _gameMode, allRoutes, writer, Shim, FileSystem.Shared, _environment,
            steamRoot ?? (() => Abs(_steamRoot)), () => Abs("/opt/apps/SilverModManager.AppImage"), Abs(Path("registry.vdf")));
    }

    private string QuotedShim => $"\"{Shim.ShimPath}\"";

    [Fact]
    public async Task AddWritesTheRecordBeforeTheVdfSoACrashLeavesSomethingToRemove()
    {
        var crashing = Substitute.For<ISteamShortcutRoute>();
        crashing.Name.Returns("file");
        crashing.AddAsync(Arg.Any<SteamShortcutRequest>(), Arg.Any<CancellationToken>()).Returns<Task<SteamShortcutWrite>>(_ => throw new InvalidOperationException("crash"));

        var act = () => Service(null, crashing).AddAsync("file", null, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        var record = SteamShortcutRecord.Load(RecordPath)!;
        record.AppId.Should().Be(SteamShortcutId.AppId(QuotedShim, "SilverModManager"));
        record.Confirmed.Should().BeFalse();
        record.AccountId.Should().Be(Account);
    }

    [Fact]
    public async Task AddOnTheFileRouteWritesTheEntryTheGridFilesAndTheConfirmedRecord()
    {
        var service = Service();

        await service.AddAsync("file", null, CancellationToken.None);

        var appId = SteamShortcutId.AppId(QuotedShim, "SilverModManager");
        var entries = new ShortcutsVdfWriter(new HandWrittenBinaryKeyValues(), new SteamConfigFileEditor(_tracker)).Read(Abs(ShortcutsFile()));
        entries.Should().ContainSingle().Which.Should().Match<SteamShortcutEntry>(e => e.AppId == appId && e.Exe == QuotedShim && e.Icon.EndsWith("icons/silvermodmanager.png"));
        File.Exists(System.IO.Path.Combine(Config(Account), "grid", $"{appId}p.png")).Should().BeTrue();
        var record = SteamShortcutRecord.Load(RecordPath)!;
        record.Confirmed.Should().BeTrue();
        record.Written.Should().Contain(System.IO.Path.Combine(Config(Account), "grid", $"{appId}_hero.png"));
        service.Status().State.Should().Be(SteamShortcutState.InSteam);
    }

    [Fact]
    public async Task AddWhileSteamRunsOnTheFileRouteRefuses()
    {
        _tracker.IsSteamRunning().Returns(true);

        var act = () => Service().AddAsync("file", null, CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<SteamShortcutRefusedException>()).Which;
        thrown.Message.Should().StartWith("Steam is running.");
        thrown.Title.Should().Be("Not added to Steam");
        thrown.CommandLineHint.Should().Be("silvermodmanager steam-shortcut add|remove|status [--account <id>]");
        File.Exists(ShortcutsFile()).Should().BeFalse();
    }

    [Fact]
    public async Task TheUrlRouteWhileSteamIsClosedRefuses()
    {
        var url = Substitute.For<ISteamShortcutRoute>();
        url.Name.Returns("steam-url");

        var act = () => Service(null, url).AddAsync("steam-url", null, CancellationToken.None);

        (await act.Should().ThrowAsync<SteamShortcutRefusedException>()).Which.Message.Should().StartWith("Steam is not running.");
        await url.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
    }

    [Fact]
    public async Task TheUrlRouteRecordsTheAppIdSteamStored()
    {
        _tracker.IsSteamRunning().Returns(true);
        var url = Substitute.For<ISteamShortcutRoute>();
        url.Name.Returns("steam-url");
        url.AddAsync(Arg.Any<SteamShortcutRequest>(), Arg.Any<CancellationToken>()).Returns(new SteamShortcutWrite(2147483650u, true, []));

        await Service(null, url).AddAsync("steam-url", null, CancellationToken.None);

        var record = SteamShortcutRecord.Load(RecordPath)!;
        record.AppId.Should().Be(2147483650u);
        record.Confirmed.Should().BeTrue();
        await url.Received(1).AddAsync(Arg.Is<SteamShortcutRequest>(r => r.ShimPath == Shim.ShimPath && r.Account.AccountId == Account), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AFlatpakSteamRefuses()
    {
        var flatpak = Abs(Path(".var/app/com.valvesoftware.Steam/.local/share/Steam"));

        var act = () => Service(() => flatpak).AddAsync("file", null, CancellationToken.None);

        (await act.Should().ThrowAsync<SteamShortcutRefusedException>()).Which.Message
            .Should().StartWith("Steam is installed as a Flatpak, which cannot reach the launcher script under ~/.local/share.");
        Service(() => flatpak).Status().State.Should().Be(SteamShortcutState.NoSteam);
    }

    [Fact]
    public async Task NoSteamInstallRefuses()
    {
        var act = () => Service(() => null).AddAsync("file", null, CancellationToken.None);

        (await act.Should().ThrowAsync<SteamShortcutRefusedException>()).Which.Message.Should().StartWith("No native Steam install was found.");
        Service(() => null).Status().State.Should().Be(SteamShortcutState.NoSteam);
    }

    [Fact]
    public async Task AnAmbiguousAccountRefusesNamingBoth()
    {
        WriteLoginUsers((1, "alice", "Alice", false), (2, "bob", "Bob", false));
        Directory.CreateDirectory(Config(1));
        Directory.CreateDirectory(Config(2));

        var act = () => Service().AddAsync("file", null, CancellationToken.None);

        (await act.Should().ThrowAsync<SteamShortcutRefusedException>()).Which.Message
            .Should().StartWith("More than one Steam account could own the shortcut: Alice, Bob. Pick one.");
    }

    [Fact]
    public async Task AnAccountIdPicksBetweenTwoAccounts()
    {
        WriteLoginUsers((1, "alice", "Alice", false), (2, "bob", "Bob", false));
        Directory.CreateDirectory(Config(1));
        Directory.CreateDirectory(Config(2));

        await Service().AddAsync("file", 2, CancellationToken.None);

        File.Exists(ShortcutsFile(2)).Should().BeTrue();
        File.Exists(ShortcutsFile(1)).Should().BeFalse();
    }

    [Fact]
    public void StatusWithTwoCandidateAccountsIsAccountUndecided()
    {
        WriteLoginUsers((1, "alice", "Alice", false), (2, "bob", "Bob", false));
        Directory.CreateDirectory(Config(1));
        Directory.CreateDirectory(Config(2));

        var status = Service().Status();

        status.State.Should().Be(SteamShortcutState.AccountUndecided);
        status.Candidates.Select(c => c.PersonaName).Should().Equal("Alice", "Bob");
    }

    [Fact]
    public void StatusMatchesByRecordedAppIdThenByExe()
    {
        var writer = new ShortcutsVdfWriter(new HandWrittenBinaryKeyValues(), new SteamConfigFileEditor(_tracker));
        var service = Service();

        // By exe: no record, an entry that starts the current AppImage.
        File.WriteAllBytes(ShortcutsFile(), ShortcutsVdfFixture.Bytes(("0", 5, "Some name", "\"/opt/apps/SilverModManager.AppImage\"")));
        service.Status().Should().Match<SteamShortcutStatus>(s => s.State == SteamShortcutState.InSteam && s.AppId == 5u && s.Account!.AccountId == Account);

        // By recorded id: the exe no longer matches anything we know.
        File.WriteAllBytes(ShortcutsFile(), ShortcutsVdfFixture.Bytes(("0", 77, "Renamed shim", "\"/somewhere/else\"")));
        service.Status().State.Should().Be(SteamShortcutState.NotInSteam);
        new SteamShortcutRecord(Account, 77, "\"/somewhere/else\"", true, []).Save(RecordPath);
        service.Status().Should().Match<SteamShortcutStatus>(s => s.State == SteamShortcutState.InSteam && s.AppId == 77u);
        writer.Should().NotBeNull();
    }

    [Fact]
    public void StatusAfterAnUnconfirmedUrlAddSaysAddedUnconfirmed()
    {
        new SteamShortcutRecord(Account, null, QuotedShim, false, []).Save(RecordPath);

        Service().Status().State.Should().Be(SteamShortcutState.AddedUnconfirmed);
    }

    [Fact]
    public void StatusWithNoEntryAndNoRecordIsNotInSteam()
    {
        Service().Status().State.Should().Be(SteamShortcutState.NotInSteam);
    }

    [Fact]
    public async Task RemoveInGameModeRefuses()
    {
        _gameMode.IsGameMode.Returns(true);

        var act = () => Service().RemoveAsync(null, CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<SteamShortcutRefusedException>()).Which;
        thrown.Message.Should().StartWith("Remove the shortcut from Desktop Mode.");
        thrown.Title.Should().Be("Not removed from Steam");
    }

    [Fact]
    public async Task RemoveDeletesTheEntryTheGridFilesAndTheRecord()
    {
        var service = Service();
        await service.AddAsync("file", null, CancellationToken.None);
        var written = SteamShortcutRecord.Load(RecordPath)!.Written;
        written.Should().NotBeEmpty();
        // An unrelated file in the grid folder is not ours to delete.
        var other = System.IO.Path.Combine(Config(Account), "grid", "12345p.png");
        File.WriteAllText(other, "x");

        await service.RemoveAsync(null, CancellationToken.None);

        new ShortcutsVdfWriter(new HandWrittenBinaryKeyValues(), new SteamConfigFileEditor(_tracker)).Read(Abs(ShortcutsFile())).Should().BeEmpty();
        written.Should().OnlyContain(f => !File.Exists(f));
        File.Exists(other).Should().BeTrue();
        File.Exists(RecordPath.ToString()).Should().BeFalse();
        service.Status().State.Should().Be(SteamShortcutState.NotInSteam);
    }

    [Fact]
    public async Task RemoveWhileSteamRunsRefuses()
    {
        var service = Service();
        await service.AddAsync("file", null, CancellationToken.None);
        _tracker.IsSteamRunning().Returns(true);

        var act = () => service.RemoveAsync(null, CancellationToken.None);

        (await act.Should().ThrowAsync<SteamShortcutRefusedException>()).Which.Message.Should().StartWith("Steam is running.");
        File.Exists(RecordPath.ToString()).Should().BeTrue();
    }

    [Fact]
    public async Task RemoveWithNothingToRemoveSaysSo()
    {
        var act = () => Service().RemoveAsync(null, CancellationToken.None);

        (await act.Should().ThrowAsync<SteamShortcutRefusedException>()).Which.Message.Should().Contain("not in Steam");
    }

    [Fact]
    public async Task AnUnparseableFileRefusesWithThePlainSentence()
    {
        File.WriteAllBytes(ShortcutsFile(), [0x07, 0x41, 0x00]);

        var act = () => Service().AddAsync("file", null, CancellationToken.None);

        (await act.Should().ThrowAsync<SteamShortcutRefusedException>()).Which.Message.Should().StartWith("Steam's shortcuts.vdf could not be parsed.");
    }

    [Fact]
    public async Task SteamStartingDuringTheWriteIsReportedAfterTheRecordIsUpdated()
    {
        _tracker.IsSteamRunning().Returns(false, false, true);

        var act = () => Service().AddAsync("file", null, CancellationToken.None);

        (await act.Should().ThrowAsync<SteamShortcutRefusedException>()).Which.Message.Should().StartWith("Steam started while the file was being written, so the change may be lost.");
        SteamShortcutRecord.Load(RecordPath)!.Confirmed.Should().BeTrue();
    }

    [Fact]
    public async Task AnUnknownRouteRefuses()
    {
        var act = () => Service().AddAsync("carrier-pigeon", null, CancellationToken.None);

        (await act.Should().ThrowAsync<SteamShortcutRefusedException>()).Which.Message.Should().Contain("carrier-pigeon");
    }
}
