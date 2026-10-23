using System.Collections.Immutable;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using FluentAssertions;
using NexusMods.Abstractions.Games;
using NexusMods.CrossPlatform;
using NexusMods.Paths;
using NexusMods.Platform.Proton.Doctor;
using NexusMods.Platform.Proton.Launch;
using NexusMods.Platform.Proton.Steam;
using NexusMods.Platform.Proton.Support;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Settings;
using NSubstitute;

namespace NexusMods.Platform.Proton.Tests.Support;

public class SupportBundleWriterTests : IDisposable
{
    private const string AppId = "42";
    private const string Secret = "hunter2-token";

    private readonly InMemoryFileSystem _fs = new(OSInformation.FakeUnix);
    private readonly string _runtimeDir = Directory.CreateTempSubdirectory("silvermod-bundle-").FullName;
    private readonly AbsolutePath _steam;
    private readonly AbsolutePath _localConfig;
    private readonly LoggingSettings _logging = LoggingSettings.CreateDefault();

    public SupportBundleWriterTests()
    {
        _steam = _fs.FromUnsanitizedFullPath("/home/u/.steam/steam");
        _localConfig = _steam.Combine("userdata/1/config/localconfig.vdf");
    }

    public void Dispose() => Directory.Delete(_runtimeDir, recursive: true);

    private AbsolutePath LogPath(ConfigurablePath path) => path.ToPath(_fs);

    private void WriteLog(ConfigurablePath path, string text)
    {
        var file = LogPath(path);
        _fs.CreateDirectory(file.Parent);
        _fs.WriteAllText(file, text);
    }

    private void WriteLocalConfig(string launchOptions)
    {
        _fs.CreateDirectory(_localConfig.Parent);
        _fs.WriteAllText(_localConfig, $$"""
            "UserLocalConfigStore"
            {
                "Software" { "Valve" { "Steam" { "apps" { "{{AppId}}" { "LaunchOptions" "{{launchOptions.Replace("\"", "\\\"")}}" } } } } }
            }
            """);
    }

    private GameInstallation SteamGame()
    {
        var game = Substitute.For<IGameData>();
        game.DisplayName.Returns("Test Game");
        var prefix = _steam.Combine("steamapps/compatdata/42/pfx");

        var provider = Substitute.For<ILinuxCompatabilityDataProvider>();
        provider.WinePrefixDirectoryPath.Returns(prefix);
        provider.SteamInstallPath.Returns(_steam);
        provider.SteamLocalConfigPath.Returns(_localConfig);

        var result = new GameLocatorResult
        {
            Game = game,
            Path = _steam.Combine("steamapps/common/Test Game"),
            Store = GameStore.Steam,
            StoreIdentifier = AppId,
            LocatorIds = ImmutableArray<LocatorId>.Empty,
            Locator = Substitute.For<IGameLocator>(),
            Platform = OSPlatform.Windows,
            LinuxCompatabilityDataProvider = provider,
            Compatibility = new CompatibilityLayer(CompatibilityLayerKind.SteamProton, prefix),
        };
        return new GameInstallation(result, GameLocations.Create(ImmutableDictionary<LocationId, AbsolutePath>.Empty.Add(LocationId.Game, result.Path)));
    }

    private SupportBundleWriter Writer(params ManagedInstallation[] installations)
    {
        var prefixes = Substitute.For<IPrefixResolver>();
        prefixes.Resolve(Arg.Any<GameInstallation>()).Returns(PrefixResolution.Resolved(new ResolvedPrefix(_steam.Combine("steamapps/compatdata/42/pfx"), CompatibilityLayerKind.SteamProton, ProtonAppId: 42)));
        var proton = Substitute.For<IProtonResolver>();
        proton.ListBuilds(Arg.Any<AbsolutePath>()).Returns([]);
        proton.ResolveForApp(Arg.Any<AbsolutePath>(), Arg.Any<uint>()).Returns(ProtonResolution.Failed("no Proton in the fixture"));
        var reporter = new DoctorReporter(prefixes, proton, Substitute.For<IToolRunner>(), new InstallationLockFactory(new FakeEnvironment(_runtimeDir)));

        var settings = Substitute.For<ISettingsManager>();
        settings.Get<LoggingSettings>().Returns(_logging);

        var source = Substitute.For<IManagedInstallations>();
        source.List().Returns(installations);

        return new SupportBundleWriter(_fs, settings, new FakeEnvironment(_runtimeDir), reporter, source);
    }

    private void WriteState(string store, string id, string text)
    {
        var path = InstallationLock.PathFor(_runtimeDir, store, id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(Path.ChangeExtension(path, ".state"), text);
    }

    private static Dictionary<string, string> Read(AbsolutePath bundle)
    {
        using var stream = bundle.Read();
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        return zip.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var reader = new StreamReader(e.Open());
            return reader.ReadToEnd();
        });
    }

    [Fact]
    public async Task WriteAsync_ContainsExactlyTheExpectedEntries()
    {
        WriteLog(_logging.MainProcessLogFilePath, "main log\n");
        WriteLog(_logging.RunProcessLogFilePath, "run log\n");
        WriteState("steam", AppId, "pid=123\nloadout=Main\nphase=running\n");
        WriteState("gog", "7", "pid=9\nloadout=Other\nphase=starting\n");

        // Neighbours of the state files that must not be picked up.
        File.WriteAllText(InstallationLock.PathFor(_runtimeDir, "steam", AppId), "");
        File.WriteAllText(Path.Combine(InstallationLock.DirectoryFor(_runtimeDir), "sync"), "sync file contents");

        WriteLocalConfig($"PROTON_LOG=1 API_TOKEN={Secret} {SteamLaunchOptionLine.Compose("/opt/silvermod", "Main", [], [])}");
        var bundle = await Writer(new ManagedInstallation(SteamGame(), ["Main"])).WriteAsync();

        var entries = Read(bundle);
        entries.Keys.Should().BeEquivalentTo(
            "logs/silvermodmanager.main.current.log",
            "logs/silvermodmanager.run.current.log",
            "doctor.json",
            "launch-state/launch-gog-7.state",
            "launch-state/launch-steam-42.state",
            "steam-launch-options.txt",
            "system.txt");

        entries["logs/silvermodmanager.main.current.log"].Should().Be("main log\n");
        entries["logs/silvermodmanager.run.current.log"].Should().Be("run log\n");
        entries["launch-state/launch-steam-42.state"].Should().Be("pid=123\nloadout=Main\nphase=running\n");
        entries["system.txt"].Should().Contain(ApplicationConstants.Version.ToString()).And.Contain(RuntimeInformation.OSDescription);
    }

    [Fact]
    public async Task WriteAsync_RedactsEnvPrefixValuesEverywhereTheLineAppears()
    {
        WriteLocalConfig($"PROTON_LOG=1 API_TOKEN={Secret} {SteamLaunchOptionLine.Compose("/opt/silvermod", "Main", [], [])}");
        var bundle = await Writer(new ManagedInstallation(SteamGame(), ["Main"])).WriteAsync();

        var entries = Read(bundle);
        var expectedLine = $"PROTON_LOG=... API_TOKEN=... {SteamLaunchOptionLine.Compose("/opt/silvermod", "Main", [], [])}";

        entries["steam-launch-options.txt"].Should().Contain("Test Game").And.Contain(AppId).And.Contain(expectedLine);

        var doctor = JsonSerializer.Deserialize<DoctorReport>(entries["doctor.json"], DoctorReport.JsonOptions)!;
        doctor.Installations.Single().LaunchOptions!.Line.Should().Be(expectedLine);
        doctor.Installations.Single().LaunchOptions!.Loadout.Should().Be("Main");

        foreach (var (name, text) in entries) text.Should().NotContain(Secret, because: name);
    }

    [Fact]
    public async Task WriteAsync_SkipsAStateFileThatIsRemovedWhileCollecting()
    {
        WriteState("steam", AppId, "pid=123\nloadout=Main\nphase=running\n");

        // A session releasing its lock deletes the state file; a dangling link is listed but cannot be opened,
        // which is what the writer sees when the game exits between the listing and the read.
        File.CreateSymbolicLink(Path.Combine(InstallationLock.DirectoryFor(_runtimeDir), "launch-gog-7.state"), Path.Combine(_runtimeDir, "gone"));

        var bundle = await Writer().WriteAsync();

        Read(bundle).Keys.Should().BeEquivalentTo("doctor.json", "steam-launch-options.txt", "system.txt", "launch-state/launch-steam-42.state");
    }

    [Fact]
    public async Task WriteAsync_WithNothingToCollect_StillWritesTheReportsAndNoLogs()
    {
        var bundle = await Writer().WriteAsync();

        Read(bundle).Keys.Should().BeEquivalentTo("doctor.json", "steam-launch-options.txt", "system.txt");
    }

    [Fact]
    public async Task WriteAsync_PutsTheZipInTheStateDirectoryAndDoesNotOverwriteEarlierBundles()
    {
        var writer = Writer();
        var first = await writer.WriteAsync();
        var second = await writer.WriteAsync();

        first.Should().NotBe(second);
        first.FileExists.Should().BeTrue();
        second.FileExists.Should().BeTrue();
        first.Extension.Should().Be(new Extension(".zip"));
        first.Parent.Should().Be(LoggingSettings.GetLogBaseFolder(_fs).Parent.Combine("SupportBundles"));
    }

    [Fact]
    public async Task WriteAsync_WithoutARuntimeDirectory_SkipsLaunchState()
    {
        var prefixes = Substitute.For<IPrefixResolver>();
        var reporter = new DoctorReporter(prefixes, Substitute.For<IProtonResolver>(), Substitute.For<IToolRunner>(), new InstallationLockFactory(new FakeEnvironment(null)));
        var settings = Substitute.For<ISettingsManager>();
        settings.Get<LoggingSettings>().Returns(_logging);
        var source = Substitute.For<IManagedInstallations>();
        source.List().Returns([]);

        var bundle = await new SupportBundleWriter(_fs, settings, new FakeEnvironment(null), reporter, source).WriteAsync();

        Read(bundle).Keys.Should().BeEquivalentTo("doctor.json", "steam-launch-options.txt", "system.txt");
    }

    private sealed class FakeEnvironment(string? runtimeDir) : IEnvironmentReader
    {
        public string? Get(string name) => name == "XDG_RUNTIME_DIR" ? runtimeDir : null;
    }
}
