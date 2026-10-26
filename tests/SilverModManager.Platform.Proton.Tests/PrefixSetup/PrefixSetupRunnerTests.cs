using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Games.TestFramework;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Platform.Proton.PrefixSetup;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Platform.Proton.Tests.PrefixSetup;

/// <summary>
/// The setup runner against a fake prefix in an in-memory file system, with fakes for the tool runner, the
/// inspector, the downloader and the process table.
/// </summary>
public class PrefixSetupRunnerTests : IDisposable
{
    private static readonly byte[] InstallerX86 = Encoding.ASCII.GetBytes("x86 installer");
    private static readonly byte[] InstallerX64 = Encoding.ASCII.GetBytes("x64 installer");
    private static readonly byte[] DllX86 = Encoding.ASCII.GetBytes("32-bit dll");
    private static readonly byte[] DllX64 = Encoding.ASCII.GetBytes("64-bit dll");

    private readonly InMemoryFileSystem _fs = new(OSInformation.FakeUnix);
    private readonly string _lockDir = Directory.CreateTempSubdirectory("silvermod-setup-").FullName;
    private readonly AbsolutePath _prefixPath;
    private readonly ResolvedPrefix _prefix;
    private readonly GameInstallation _installation;
    private readonly FakeToolRunner _tools = new();
    private readonly FakeInspector _inspector = new();
    private readonly FakeFetcher _fetcher = new();
    private readonly FakeProcessTracker _processes = new();
    private readonly IPrefixResolver _resolver = Substitute.For<IPrefixResolver>();
    private readonly IInstallationLockFactory _locks = Substitute.For<IInstallationLockFactory>();
    private readonly TimeProvider _time = new FixedTime(new DateTimeOffset(2026, 10, 26, 12, 30, 0, TimeSpan.Zero));

    private PrefixComponent _vc = null!;
    private PrefixComponent _d3d = null!;

    public PrefixSetupRunnerTests()
    {
        _prefixPath = _fs.GetKnownPath(KnownPath.HomeDirectory).Combine("games/compatdata/42/pfx");
        BuildPrefix(_prefixPath);
        _prefix = new ResolvedPrefix(_prefixPath, CompatibilityLayerKind.SteamProton, ProtonAppId: 42);
        _installation = Installation();

        _vc = Component("vcrun2022", ["vcrun2019"], [("VC_redist.x86.exe", InstallerX86), ("VC_redist.x64.exe", InstallerX64)],
            [
                new PrefixInstallStep(PrefixInstallStepKind.RunInstaller, "VC_redist.x86.exe", PrefixArchitecture.X86, ["/install", "/quiet"]),
                new PrefixInstallStep(PrefixInstallStepKind.RunInstaller, "VC_redist.x64.exe", PrefixArchitecture.X64, ["/install", "/quiet"]),
            ],
            ImmutableDictionary<string, string>.Empty.Add("WINEDLLOVERRIDES", "mshtml=d"));
        _d3d = Component("d3dcompiler_47", [], [("d3dcompiler_47_32.dll", DllX86), ("d3dcompiler_47.dll", DllX64)],
            [
                new PrefixInstallStep(PrefixInstallStepKind.CopyDll, "d3dcompiler_47_32.dll", PrefixArchitecture.X86, DestinationName: "d3dcompiler_47.dll"),
                new PrefixInstallStep(PrefixInstallStepKind.CopyDll, "d3dcompiler_47.dll", PrefixArchitecture.X64, DestinationName: "d3dcompiler_47.dll"),
            ],
            ImmutableDictionary<string, string>.Empty);

        _resolver.Resolve(Arg.Any<GameInstallation>()).Returns(_ => PrefixResolution.Resolved(_prefix));
        _locks.Create(Arg.Any<string>(), Arg.Any<string>()).Returns(_ => new InstallationLock(Path.Combine(_lockDir, "launch.lock")));
        _inspector.Installed.Clear();
        _tools.OnRun = command => _inspector.Installed.Add(_vc.Id);
    }

    public void Dispose() => Directory.Delete(_lockDir, recursive: true);

    private static void BuildPrefix(AbsolutePath prefix)
    {
        prefix.Combine("drive_c/windows/system32").CreateDirectory();
        prefix.Combine("drive_c/windows/syswow64").CreateDirectory();
        prefix.Combine("system.reg").WriteAllTextAsync("system registry").GetAwaiter().GetResult();
        prefix.Combine("user.reg").WriteAllTextAsync("user registry").GetAwaiter().GetResult();
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private PrefixComponent Component(string id, string[] aliases, (string Name, byte[] Bytes)[] files, PrefixInstallStep[] steps, ImmutableDictionary<string, string> environment)
    {
        var downloads = files.Select(f =>
        {
            _fetcher.Files["https://example.invalid/" + f.Name] = f.Bytes;
            return new PrefixDownload("https://example.invalid/" + f.Name, Sha256(f.Bytes), f.Name);
        });

        return new PrefixComponent
        {
            Id = new PrefixComponentId(id),
            Aliases = [.. aliases],
            DisplayName = id + " display",
            Downloads = [.. downloads],
            InstallSteps = [.. steps],
            Environment = environment,
            NativeFiles = [],
        };
    }

    private GameInstallation Installation()
    {
        var game = Substitute.For<IGameData>();
        game.DisplayName.Returns("Test Game");
        var gameFolder = _fs.GetKnownPath(KnownPath.HomeDirectory).Combine("games/common/Test Game");
        var result = new GameLocatorResult
        {
            Game = game,
            Path = gameFolder,
            Store = GameStore.Steam,
            StoreIdentifier = "42",
            LocatorIds = ImmutableArray<LocatorId>.Empty,
            Locator = Substitute.For<IGameLocator>(),
            Platform = OSPlatform.Windows,
        };
        return new GameInstallation(result, GameLocations.Create(ImmutableDictionary<LocationId, AbsolutePath>.Empty.Add(LocationId.Game, gameFolder)));
    }

    private PrefixSetupRunner Runner() => new(_resolver, _processes, _locks, _tools, _inspector, _fetcher, _fs, _time, id => id.Value switch
    {
        "vcrun2022" or "vcrun2019" => _vc,
        "d3dcompiler_47" => _d3d,
        _ => PrefixComponents.Get(id),
    });

    private PrefixSetupRequest Write(params string[] components) => new()
    {
        Components = components.Select(c => new PrefixComponentId(c)).ToList(),
        Write = true,
        ConfirmedPrefixPath = _prefixPath.ToString(),
        Attempts = 2,
    };

    private PrefixSetupRequest DryRun(params string[] components) => new() { Components = components.Select(c => new PrefixComponentId(c)).ToList() };

    private AbsolutePath DataRoot => _fs.GetKnownPath(KnownPath.XDG_DATA_HOME).Combine(ApplicationConstants.DataDirectoryName);
    private AbsolutePath CacheRoot => _fs.GetKnownPath(KnownPath.XDG_CACHE_HOME).Combine(ApplicationConstants.DataDirectoryName);
    private AbsolutePath System32 => _prefixPath.Combine("drive_c/windows/system32");
    private AbsolutePath SysWow64 => _prefixPath.Combine("drive_c/windows/syswow64");
    private AbsolutePath WinetricksLog => _prefixPath.Combine("winetricks.log");

    private string[] LoggedVerbs() => WinetricksLog.FileExists
        ? WinetricksLog.ReadAllTextAsync().GetAwaiter().GetResult().Split('\n', StringSplitOptions.RemoveEmptyEntries)
        : [];

    private string TreeHash(AbsolutePath root)
    {
        var builder = new StringBuilder();
        foreach (var file in root.EnumerateFiles().OrderBy(f => f.ToString(), StringComparer.Ordinal))
            builder.Append(file).Append(':').Append(Sha256(file.ReadAllBytesAsync().GetAwaiter().GetResult())).Append('\n');
        foreach (var dir in root.EnumerateDirectories().OrderBy(d => d.ToString(), StringComparer.Ordinal))
            builder.Append("dir ").Append(dir).Append('\n');
        return Sha256(Encoding.UTF8.GetBytes(builder.ToString()));
    }

    // ---- dry run ----

    [Fact]
    public async Task DryRun_ReturnsThePlanAndWritesNothing()
    {
        var rootsBefore = TreeHash(_fs.FromUnsanitizedFullPath("/"));

        var result = await Runner().Run(_installation, DryRun("vcrun2022", "d3dcompiler_47"), CancellationToken.None);

        result.Written.Should().BeFalse();
        result.Steps.Should().BeEmpty();
        result.Plan.Prefix.Should().Be(_prefix);
        result.Plan.Steps.Select(s => s.Id.Value).Should().Equal("vcrun2022", "d3dcompiler_47");
        result.Plan.Steps[0].Downloads.Select(d => (d.Url, d.Sha256)).Should().Equal(_vc.Downloads.Select(d => (d.Url, d.Sha256)));
        result.Plan.Steps[0].Actions.Should().Contain("run VC_redist.x86.exe /install /quiet");
        result.Plan.Steps[1].Actions.Should().Contain("copy d3dcompiler_47_32.dll to drive_c/windows/syswow64/d3dcompiler_47.dll");
        result.Plan.SharedNote.Should().Contain("Every Proton use");

        _tools.Commands.Should().BeEmpty();
        _fetcher.Fetched.Should().BeEmpty();
        TreeHash(_fs.FromUnsanitizedFullPath("/")).Should().Be(rootsBefore);
        DataRoot.DirectoryExists().Should().BeFalse();
        CacheRoot.DirectoryExists().Should().BeFalse();
    }

    [Fact]
    public async Task DryRun_ShowsTheBackupFolderUnderTheAppDataNotBesideThePrefix()
    {
        var plan = (await Runner().Run(_installation, DryRun("vcrun2022"), CancellationToken.None)).Plan;

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_prefixPath.ToString()))).ToLowerInvariant()[..16];
        plan.BackupDirectory.Should().Be(DataRoot.Combine($"prefix-backups/{hash}/20261026T123000Z"));
        plan.BackupDirectory!.Value.ToString().Should().NotStartWith(_prefixPath.Parent.ToString());
    }

    [Fact]
    public async Task DryRun_HasNoBackupWhenNoStepTouchesTheRegistry()
    {
        var plan = (await Runner().Run(_installation, DryRun("d3dcompiler_47"), CancellationToken.None)).Plan;

        plan.BackupDirectory.Should().BeNull();
    }

    [Fact]
    public async Task DryRun_MarksAnAlreadyInstalledComponentAsSkipped()
    {
        _inspector.Installed.Add(_vc.Id);

        var plan = (await Runner().Run(_installation, DryRun("vcrun2022", "d3dcompiler_47"), CancellationToken.None)).Plan;

        plan.Steps[0].WillInstall.Should().BeFalse();
        plan.Steps[0].Detected.State.Should().Be(PrefixComponentState.Installed);
        plan.Steps[1].WillInstall.Should().BeTrue();
        plan.BackupDirectory.Should().BeNull("the only step that touches the registry is skipped");
    }

    [Fact]
    public async Task DryRun_ReportsTheRealPinnedTable()
    {
        var runner = new PrefixSetupRunner(_resolver, _processes, _locks, _tools, _inspector, _fetcher, _fs, _time);

        var plan = (await runner.Run(_installation, DryRun("vcrun2022", "d3dcompiler_47"), CancellationToken.None)).Plan;

        plan.Steps.SelectMany(s => s.Downloads).Should().OnlyContain(d => d.Url.StartsWith("https://") && d.Sha256.Length == 64);
        plan.Steps.SelectMany(s => s.Downloads).Select(d => d.Url).Should().BeEquivalentTo(PrefixComponents.All.SelectMany(c => c.Downloads).Select(d => d.Url));
    }

    // ---- refusals ----

    [Fact]
    public async Task Write_IsRefusedWithoutAConfirmation()
    {
        var request = Write("vcrun2022") with { ConfirmedPrefixPath = null };

        var act = () => Runner().Run(_installation, request, CancellationToken.None);

        (await act.Should().ThrowAsync<PrefixSetupRefusedException>()).Which.Message.Should().Contain(_prefixPath.ToString());
        _tools.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task Write_IsRefusedWhenTheConfirmationNamesAnotherPath()
    {
        var request = Write("vcrun2022") with { ConfirmedPrefixPath = _prefixPath.Parent.Combine("43/pfx").ToString() };

        var act = () => Runner().Run(_installation, request, CancellationToken.None);

        await act.Should().ThrowAsync<PrefixSetupRefusedException>();
        _tools.Commands.Should().BeEmpty();
        _fetcher.Fetched.Should().BeEmpty();
    }

    [Fact]
    public async Task Write_IsRefusedForASteamLibraryPrefixUnlessItIsNamed()
    {
        var steamPrefix = _fs.GetKnownPath(KnownPath.HomeDirectory).Combine(".local/share/Steam/steamapps/compatdata/42/pfx");
        BuildPrefix(steamPrefix);
        _resolver.Resolve(Arg.Any<GameInstallation>()).Returns(_ => PrefixResolution.Resolved(_prefix with { Path = steamPrefix }));

        var unnamed = () => Runner().Run(_installation, Write("vcrun2022") with { ConfirmedPrefixPath = null }, CancellationToken.None);
        (await unnamed.Should().ThrowAsync<PrefixSetupRefusedException>()).Which.Message.Should().Contain("Steam");

        var named = await Runner().Run(_installation, Write("vcrun2022") with { ConfirmedPrefixPath = steamPrefix.ToString() }, CancellationToken.None);
        named.Failure.Should().BeNull();
    }

    [Fact]
    public async Task Write_IsRefusedForAnMntSteamPrefixUnlessItIsNamed()
    {
        var mnt = _fs.FromUnsanitizedFullPath("/mnt/steam/steamapps/compatdata/42/pfx");
        BuildPrefix(mnt);
        _resolver.Resolve(Arg.Any<GameInstallation>()).Returns(_ => PrefixResolution.Resolved(_prefix with { Path = mnt }));

        var unnamed = () => Runner().Run(_installation, Write("vcrun2022") with { ConfirmedPrefixPath = "" }, CancellationToken.None);

        (await unnamed.Should().ThrowAsync<PrefixSetupRefusedException>()).Which.Message.Should().Contain("Steam");
    }

    [Fact]
    public async Task Refuses_AnUnreadyPrefixAndNeverCreatesOne()
    {
        var unready = _fs.GetKnownPath(KnownPath.HomeDirectory).Combine("games/compatdata/77/pfx");
        _resolver.Resolve(Arg.Any<GameInstallation>()).Returns(_ => PrefixResolution.Resolved(_prefix with { Path = unready }));

        var act = () => Runner().Run(_installation, Write("vcrun2022") with { ConfirmedPrefixPath = unready.ToString() }, CancellationToken.None);

        await act.Should().ThrowAsync<PrefixSetupRefusedException>();
        unready.DirectoryExists().Should().BeFalse();
        _tools.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task DryRun_IsAlsoRefusedForAnUnreadyPrefix()
    {
        var unready = _fs.GetKnownPath(KnownPath.HomeDirectory).Combine("games/compatdata/77/pfx");
        _resolver.Resolve(Arg.Any<GameInstallation>()).Returns(_ => PrefixResolution.Resolved(_prefix with { Path = unready }));

        var act = () => Runner().Run(_installation, DryRun("vcrun2022"), CancellationToken.None);

        await act.Should().ThrowAsync<PrefixSetupRefusedException>();
    }

    [Fact]
    public async Task Refuses_WhenThePrefixIsAmbiguousOrMissing()
    {
        _resolver.Resolve(Arg.Any<GameInstallation>()).Returns(_ => PrefixResolution.Failed("Two different prefixes were found."));

        var act = () => Runner().Run(_installation, Write("vcrun2022"), CancellationToken.None);

        (await act.Should().ThrowAsync<PrefixSetupRefusedException>()).Which.Message.Should().Contain("Two different prefixes");
    }

    [Fact]
    public async Task Refuses_ANativeGame()
    {
        _resolver.Resolve(Arg.Any<GameInstallation>()).Returns(_ => PrefixResolution.Native("Runs natively."));

        var act = () => Runner().Run(_installation, Write("vcrun2022"), CancellationToken.None);

        (await act.Should().ThrowAsync<PrefixSetupRefusedException>()).Which.Message.Should().Contain("Runs natively");
    }

    [Fact]
    public async Task Refuses_WhenNoRunnerCanRunProgramsInThePrefix()
    {
        _tools.CannotRun = "Lutris prefix without a Wine binary.";

        var act = () => Runner().Run(_installation, Write("vcrun2022"), CancellationToken.None);

        (await act.Should().ThrowAsync<PrefixSetupRefusedException>()).Which.Message.Should().Contain("Lutris prefix without a Wine binary");
    }

    [Fact]
    public async Task Write_IsRefusedWhileTheGameRuns()
    {
        _processes.ForInstallation = _ => [new TrackedProcess(900, "game.exe")];

        var act = () => Runner().Run(_installation, Write("vcrun2022"), CancellationToken.None);

        (await act.Should().ThrowAsync<PrefixSetupRefusedException>()).Which.Message.Should().Contain("game.exe");
        _fetcher.Fetched.Should().BeEmpty();
    }

    [Fact]
    public async Task Write_IsRefusedWhileAProcessIsBoundToThePrefix()
    {
        AbsolutePath? asked = null;
        _processes.ByPrefix = prefix => { asked = prefix; return [new TrackedProcess(901, "wineserver")]; };

        var act = () => Runner().Run(_installation, Write("vcrun2022"), CancellationToken.None);

        (await act.Should().ThrowAsync<PrefixSetupRefusedException>()).Which.Message.Should().Contain("wineserver");
        asked.Should().Be(_prefixPath);
        _tools.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task Write_IsRefusedWhileAnotherHolderHasTheInstallationLock()
    {
        using var holder = new InstallationLock(Path.Combine(_lockDir, "launch.lock"));
        holder.TryAcquire().Should().BeTrue();

        var act = () => Runner().Run(_installation, Write("vcrun2022"), CancellationToken.None);

        await act.Should().ThrowAsync<PrefixSetupRefusedException>();
        _tools.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task Write_ReleasesTheInstallationLockWhenDone()
    {
        await Runner().Run(_installation, Write("vcrun2022"), CancellationToken.None);

        using var next = new InstallationLock(Path.Combine(_lockDir, "launch.lock"));
        next.TryAcquire(TimeSpan.Zero).Should().BeTrue();
    }

    // ---- write ----

    [Fact]
    public async Task Write_InstallsInOrderThroughTheToolRunnerWithTheComponentEnvironment()
    {
        _tools.OnRun = _ => _inspector.Installed.Add(_vc.Id);

        var result = await Runner().Run(_installation, Write("vcrun2022"), CancellationToken.None);

        result.Written.Should().BeTrue();
        result.Steps.Should().ContainSingle().Which.Status.Should().Be(PrefixSetupStepStatus.Installed);
        _tools.Commands.Select(c => Path.GetFileName(c.Program)).Should().Equal("VC_redist.x86.exe", "VC_redist.x64.exe");
        _tools.Commands.Should().OnlyContain(c => c.Args.SequenceEqual(new[] { "/install", "/quiet" }));
        _tools.Commands.Should().OnlyContain(c => c.Env!["WINEDLLOVERRIDES"] == "mshtml=d");
        _tools.Installations.Should().OnlyContain(i => i == _installation);
        _tools.Commands[0].Exe!.Value.Parent.Parent.Should().Be(CacheRoot.Combine("prefix-components"));
    }

    [Fact]
    public async Task Write_BacksUpTheRegistryFilesIntoTheAppDataBeforeAnInstallerRuns()
    {
        string? systemAtRun = null;
        var plan = (await Runner().Run(_installation, DryRun("vcrun2022"), CancellationToken.None)).Plan;
        _tools.OnRun = _ =>
        {
            systemAtRun = plan.BackupDirectory!.Value.Combine("system.reg").FileExists
                ? plan.BackupDirectory!.Value.Combine("system.reg").ReadAllTextAsync().GetAwaiter().GetResult()
                : null;
            _inspector.Installed.Add(_vc.Id);
        };

        await Runner().Run(_installation, Write("vcrun2022"), CancellationToken.None);

        var backup = plan.BackupDirectory!.Value;
        systemAtRun.Should().Be("system registry");
        (await backup.Combine("user.reg").ReadAllTextAsync()).Should().Be("user registry");
        _prefixPath.Parent.EnumerateFiles().Select(f => f.FileName).Should().NotContain(n => n.Contains("backup"));
    }

    [Fact]
    public async Task Write_DoesNotBackUpWhenOnlyADllIsCopied()
    {
        _inspector.DetectAs = id => id == _d3d.Id && SysWow64.Combine("d3dcompiler_47.dll").FileExists && System32.Combine("d3dcompiler_47.dll").FileExists;

        var result = await Runner().Run(_installation, Write("d3dcompiler_47"), CancellationToken.None);

        result.Failure.Should().BeNull();
        DataRoot.Combine("prefix-backups").DirectoryExists().Should().BeFalse();
    }

    [Fact]
    public async Task Write_CopiesDllsIntoTheMatchingSystemFolders()
    {
        _inspector.DetectAs = id => SysWow64.Combine("d3dcompiler_47.dll").FileExists && System32.Combine("d3dcompiler_47.dll").FileExists;

        var result = await Runner().Run(_installation, Write("d3dcompiler_47"), CancellationToken.None);

        result.Steps.Should().ContainSingle().Which.Status.Should().Be(PrefixSetupStepStatus.Installed);
        (await SysWow64.Combine("d3dcompiler_47.dll").ReadAllBytesAsync()).Should().Equal(DllX86);
        (await System32.Combine("d3dcompiler_47.dll").ReadAllBytesAsync()).Should().Equal(DllX64);
        _tools.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task Write_DownloadsOnlyOnceAndReusesAVerifiedCacheFile()
    {
        await Runner().Run(_installation, Write("vcrun2022"), CancellationToken.None);
        _inspector.Installed.Clear();
        _fetcher.Fetched.Clear();

        await Runner().Run(_installation, Write("vcrun2022"), CancellationToken.None);

        _fetcher.Fetched.Should().BeEmpty();
    }

    [Fact]
    public async Task Write_RefetchesACachedFileWhoseHashNoLongerMatches()
    {
        await Runner().Run(_installation, Write("vcrun2022"), CancellationToken.None);
        _inspector.Installed.Clear();
        _fetcher.Fetched.Clear();
        await CacheRoot.Combine("prefix-components").EnumerateFiles().First().WriteAllBytesAsync([1, 2, 3]);

        var result = await Runner().Run(_installation, Write("vcrun2022"), CancellationToken.None);

        result.Failure.Should().BeNull();
        _fetcher.Fetched.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Write_AHashMismatchFailsTheStepAndNeverRunsIt()
    {
        _fetcher.Files["https://example.invalid/VC_redist.x64.exe"] = [9, 9, 9];

        var result = await Runner().Run(_installation, Write("vcrun2022"), CancellationToken.None);

        var failure = result.Failure.Should().NotBeNull().And.Subject.As<PrefixSetupStepResult>();
        failure.Error.Should().Contain("VC_redist.x64.exe").And.Contain("SHA-256");
        _tools.Commands.Should().BeEmpty();
        LoggedVerbs().Should().BeEmpty();
        CacheRoot.Combine("prefix-components").EnumerateFiles().Should().NotContain(f => f.FileName.Contains("x64"));
    }

    [Fact]
    public async Task Write_LogsTheVerbOnlyAfterDetectionReportsInstalled()
    {
        var result = await Runner().Run(_installation, Write("vcrun2022"), CancellationToken.None);

        result.Failure.Should().BeNull();
        LoggedVerbs().Should().Equal("vcrun2022");
    }

    [Fact]
    public async Task Write_KeepsExistingLogLinesAndAppendsOnANewLine()
    {
        await WinetricksLog.WriteAllTextAsync("corefonts");

        await Runner().Run(_installation, Write("vcrun2022"), CancellationToken.None);

        LoggedVerbs().Should().Equal("corefonts", "vcrun2022");
    }

    [Fact]
    public async Task Write_AnInstallerThatLeavesNothingBehindFailsAndIsNotLogged()
    {
        _tools.OnRun = _ => { };

        var result = await Runner().Run(_installation, Write("vcrun2022"), CancellationToken.None);

        result.Failure!.Error.Should().Contain("does not show");
        LoggedVerbs().Should().BeEmpty();
    }

    [Fact]
    public async Task Write_ANonSuccessExitCodeFailsTheStep()
    {
        _tools.ExitCode = _ => 5;

        var result = await Runner().Run(_installation, Write("vcrun2022"), CancellationToken.None);

        result.Failure!.Error.Should().Contain("exit code 5");
        LoggedVerbs().Should().BeEmpty();
    }

    [Theory]
    [InlineData(102)]
    [InlineData(105)]
    [InlineData(194)]
    public async Task Write_TreatsMicrosoftsRebootAndAlreadyNewerCodesAsSuccess(int exitCode)
    {
        _tools.ExitCode = _ => exitCode;

        var result = await Runner().Run(_installation, Write("vcrun2022"), CancellationToken.None);

        result.Failure.Should().BeNull();
    }

    [Fact]
    public async Task Write_RetriesAFailedStep()
    {
        var calls = 0;
        _tools.ExitCode = _ => ++calls <= 1 ? 1 : 0;

        var result = await Runner().Run(_installation, Write("vcrun2022"), CancellationToken.None);

        result.Steps.Single().Status.Should().Be(PrefixSetupStepStatus.Installed);
        calls.Should().BeGreaterThan(1);
    }

    [Fact]
    public async Task Write_ReportsEachStepAsItFinishesIncludingTheOnesNeverRun()
    {
        _tools.ExitCode = _ => 1;
        var reported = new List<(PrefixSetupStepStatus Status, int CommandsRun)>();

        await Runner().Run(_installation, Write("vcrun2022", "d3dcompiler_47"), CancellationToken.None, step =>
        {
            reported.Add((step.Status, _tools.Commands.Count));
            return ValueTask.CompletedTask;
        });

        reported.Select(r => r.Status).Should().Equal(PrefixSetupStepStatus.Failed, PrefixSetupStepStatus.NotRun);
        reported[0].CommandsRun.Should().Be(2, "the failure is reported once its attempts are spent, not before");
    }

    [Fact]
    public async Task DryRun_ReportsNoSteps()
    {
        var reported = new List<PrefixSetupStepResult>();

        await Runner().Run(_installation, DryRun("vcrun2022"), CancellationToken.None, step =>
        {
            reported.Add(step);
            return ValueTask.CompletedTask;
        });

        reported.Should().BeEmpty();
    }

    [Fact]
    public async Task Write_StopsAtAStepThatKeepsFailingAndLeavesLaterStepsUnrun()
    {
        _tools.ExitCode = _ => 1;

        var result = await Runner().Run(_installation, Write("vcrun2022", "d3dcompiler_47"), CancellationToken.None);

        result.Steps.Select(s => s.Status).Should().Equal(PrefixSetupStepStatus.Failed, PrefixSetupStepStatus.NotRun);
        _tools.Commands.Should().HaveCount(2, "the first installer is tried once per attempt and the second never starts");
        SysWow64.Combine("d3dcompiler_47.dll").FileExists.Should().BeFalse();
    }

    [Fact]
    public async Task Write_SkipsAComponentThatIsAlreadyInstalled()
    {
        _inspector.Installed.Add(_vc.Id);

        var result = await Runner().Run(_installation, Write("vcrun2022"), CancellationToken.None);

        result.Steps.Single().Status.Should().Be(PrefixSetupStepStatus.AlreadyInstalled);
        _tools.Commands.Should().BeEmpty();
        _fetcher.Fetched.Should().BeEmpty();
        LoggedVerbs().Should().BeEmpty();
        DataRoot.Combine("prefix-backups").DirectoryExists().Should().BeFalse();
    }

    [Fact]
    public async Task Write_CancellationStopsBetweenStepsAndKeepsTheFirstStepsLog()
    {
        using var cts = new CancellationTokenSource();
        _inspector.DetectAs = id => id == _vc.Id ? _inspector.Installed.Contains(id) : false;
        _tools.OnRun = _ => { _inspector.Installed.Add(_vc.Id); if (_tools.Commands.Count == 2) cts.Cancel(); };

        var act = () => Runner().Run(_installation, Write("vcrun2022", "d3dcompiler_47"), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        LoggedVerbs().Should().Equal("vcrun2022");
        SysWow64.Combine("d3dcompiler_47.dll").FileExists.Should().BeFalse();
    }

    [Fact]
    public async Task Write_CancellationReachesTheRunningInstaller()
    {
        using var cts = new CancellationTokenSource();
        _tools.OnRunToken = token => { cts.Cancel(); token.ThrowIfCancellationRequested(); };

        var act = () => Runner().Run(_installation, Write("vcrun2022"), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        LoggedVerbs().Should().BeEmpty();
    }

    [Fact]
    public async Task Write_AnAliasLogsTheCanonicalVerb()
    {
        await Runner().Run(_installation, Write("vcrun2019"), CancellationToken.None);

        LoggedVerbs().Should().Equal("vcrun2022");
    }

    [Fact]
    public async Task ThePlanIsTheSameForAWriteAsForTheDryRunBeforeIt()
    {
        var dry = await Runner().Run(_installation, DryRun("vcrun2022", "d3dcompiler_47"), CancellationToken.None);
        var written = await Runner().Run(_installation, Write("vcrun2022", "d3dcompiler_47"), CancellationToken.None);

        written.Plan.Steps.Select(s => (s.Id, s.Downloads, s.Actions)).Should().BeEquivalentTo(dry.Plan.Steps.Select(s => (s.Id, s.Downloads, s.Actions)));
    }

    // ---- fakes ----

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeInspector : IPrefixInspector
    {
        public HashSet<PrefixComponentId> Installed { get; } = [];
        public Func<PrefixComponentId, bool>? DetectAs { get; set; }

        public IReadOnlyList<PrefixComponentStatus> Inspect(ResolvedPrefix prefix, IReadOnlyList<PrefixComponentId> components)
        {
            return components.Select(id =>
            {
                var installed = DetectAs is null ? Installed.Contains(id) : DetectAs(id);
                return new PrefixComponentStatus(id, installed ? PrefixComponentState.Installed : PrefixComponentState.Missing, installed ? "fake native file" : "fake: nothing");
            }).ToList();
        }
    }

    private sealed class FakeFetcher : IPrefixFileFetcher
    {
        public Dictionary<string, byte[]> Files { get; } = [];
        public List<string> Fetched { get; } = [];

        public async Task Fetch(string url, AbsolutePath destination, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Fetched.Add(url);
            destination.Parent.CreateDirectory();
            await destination.WriteAllBytesAsync(Files[url]);
        }
    }

    private sealed class FakeToolRunner : IToolRunner
    {
        public List<PrefixCommand> Commands { get; } = [];
        public List<GameInstallation> Installations { get; } = [];
        public Action<PrefixCommand>? OnRun { get; set; }
        public Action<CancellationToken>? OnRunToken { get; set; }
        public Func<PrefixCommand, int> ExitCode { get; set; } = _ => 0;
        public string? CannotRun { get; set; }

        public Task<PrefixRunResult> RunInPrefix(GameInstallation installation, PrefixCommand command, CancellationToken cancellationToken)
        {
            OnRunToken?.Invoke(cancellationToken);
            Commands.Add(command);
            Installations.Add(installation);
            var code = ExitCode(command);
            if (code == 0 || code is 102 or 105 or 194) OnRun?.Invoke(command);
            return Task.FromResult(new PrefixRunResult(code, "", ""));
        }

        public bool CanRun(GameInstallation installation, [NotNullWhen(false)] out string? diagnostic)
        {
            diagnostic = CannotRun;
            return CannotRun is null;
        }

        public ToolRunPlan Plan(GameInstallation installation, AbsolutePath exe, IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? env, ToolRunVia via = ToolRunVia.Auto)
            => throw new NotSupportedException("The setup runner goes through RunInPrefix.");

        public Task<ToolRunResult> RunWindowsTool(GameInstallation installation, AbsolutePath exe, IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? env, bool logOutput, CancellationToken cancellationToken, ToolRunVia via = ToolRunVia.Auto)
            => throw new NotSupportedException("The setup runner goes through RunInPrefix.");
    }
}
