using System.Collections.Immutable;
using System.Runtime.InteropServices;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Platform.Proton;
using NexusMods.Platform.Proton.Launch;
using NexusMods.Platform.Proton.Steam;
using NexusMods.Platform.Proton.Verbs;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using NexusMods.Sdk.NexusModsApi;
using NexusMods.Sdk.ProxyConsole;
using NSubstitute;
using Loadout = NexusMods.Sdk.Loadouts.Loadout;

namespace NexusMods.CLI.Tests;

public class SteamLaunchOptionVerbTests(IConnection connection) : IDisposable
{
    private const string AppId = "1091500";

    private readonly string _root = Directory.CreateTempSubdirectory("silvermod-steam-option-").FullName;
    private readonly string _unique = Guid.NewGuid().ToString("N")[..8];
    private readonly IProcessTracker _tracker = Substitute.For<IProcessTracker>();
    private GameInstallation? _installation;
    private EntityId _metadataId;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string LoadoutName => $"Main {_unique}";

    private string DataHome => Path.Combine(_root, "data");

    private string ConfigFile => Path.Combine(_root, "localconfig.vdf");

    private string ShimFile => Path.Combine(DataHome, "silvermod", "bin", "silvermod");

    private AbsolutePath Absolute(string path) => FileSystem.Shared.FromUnsanitizedFullPath(path);

    private void WriteConfig(string? launchOptions = null)
    {
        var options = launchOptions is null ? "" : $"\t\t\t\t\t\"LaunchOptions\"\t\t\"{launchOptions}\"\n";
        File.WriteAllText(ConfigFile,
            "\"UserLocalConfigStore\"\n{\n\t\"Software\"\n\t{\n\t\t\"Valve\"\n\t\t{\n\t\t\t\"Steam\"\n\t\t\t{\n"
            + $"\t\t\t\t\"apps\"\n\t\t\t\t{{\n\t\t\t\t\t\"{AppId}\"\n\t\t\t\t\t{{\n\t\t\t\t\t\t\"LastPlayed\"\t\t\"1\"\n{options}\t\t\t\t\t}}\n\t\t\t\t}}\n"
            + "\t\t\t}\n\t\t}\n\t}\n}\n");
    }

    private void WriteShim() => new LauncherShim(Absolute(DataHome)).Write(Absolute("/opt/silvermod/SilverModManager"));

    private async Task Install(GameStore? store = null, bool withProvider = true)
    {
        var gameData = Substitute.For<IGameData>();
        gameData.DisplayName.Returns($"Alpha {_unique}");
        var folder = Absolute(Path.Combine(_root, "game"));

        var provider = Substitute.For<ILinuxCompatabilityDataProvider>();
        provider.SteamLocalConfigPath.Returns(Absolute(ConfigFile));

        var result = new GameLocatorResult
        {
            Game = gameData,
            Path = folder,
            Store = store ?? GameStore.Steam,
            StoreIdentifier = AppId,
            LocatorIds = ImmutableArray<LocatorId>.Empty,
            Locator = Substitute.For<IGameLocator>(),
            Platform = OSPlatform.Windows,
            LinuxCompatabilityDataProvider = withProvider ? provider : null,
        };
        _installation = new GameInstallation(result, GameLocations.Create(ImmutableDictionary<LocationId, AbsolutePath>.Empty.Add(LocationId.Game, folder)));

        using var tx = connection.BeginTransaction();
        var metadata = new GameInstallMetadata.New(tx)
        {
            Path = folder.GetFullPath(),
            Name = $"Alpha {_unique}",
            Store = result.Store,
            GameId = NexusModsGameId.From(1),
        };
        _ = new Loadout.New(tx)
        {
            Name = LoadoutName,
            ShortName = "A",
            InstallationId = metadata,
            LoadoutKind = LoadoutKind.Default,
            Revision = 0,
            GameVersion = VanityVersion.From("Unknown"),
        };
        var committed = await tx.Commit();
        _metadataId = committed.Remap(metadata).Id;
    }

    private SteamLaunchOptionCommand Command()
    {
        var registry = Substitute.For<IGameRegistry>();
        registry.LocateGameInstallations().Returns(_ => _installation is null ? ImmutableArray<GameInstallation>.Empty : [_installation]);
        registry.TryGetMetadata(Arg.Any<GameInstallation>(), out Arg.Any<GameInstallMetadata.ReadOnly>())
            .Returns(call =>
            {
                call[1] = GameInstallMetadata.Load(connection.Db, _metadataId);
                return true;
            });

        var environment = Substitute.For<IEnvironmentReader>();
        environment.Get("XDG_DATA_HOME").Returns(DataHome);
        return new SteamLaunchOptionCommand(new LoadoutResolver(connection, registry, environment), new LocalConfigVdfWriter(_tracker), environment, FileSystem.Shared);
    }

    private static string[] Lines(LoggingRenderer renderer)
        => string.Concat(renderer.Logs.OfType<Text>().Select(t => t.Template)).Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static string Output(LoggingRenderer renderer) => string.Concat(renderer.Logs.OfType<Text>().Select(t => t.Template));

    [Fact]
    public async Task PrintsTheLineThatRunsTheLoadoutAndLeavesSteamAlone()
    {
        await Install();
        WriteShim();
        WriteConfig("-novid");
        var before = File.ReadAllText(ConfigFile);
        var renderer = new LoggingRenderer();

        var exitCode = await Command().RunAsync(renderer, LoadoutName, null, write: false);

        exitCode.Should().Be(0);
        Lines(renderer)[0].Should().Be($"'{ShimFile}' run '{LoadoutName}' -- %command%");
        Output(renderer).Should().Contain("current deploy");
        File.ReadAllText(ConfigFile).Should().Be(before);
    }

    [Fact]
    public async Task ResolvesTheLoadoutNameTheWayRunDoes()
    {
        await Install();
        WriteShim();
        var renderer = new LoggingRenderer();

        var exitCode = await Command().RunAsync(renderer, $"  {LoadoutName.ToUpperInvariant()} ", AppId, write: false);

        exitCode.Should().Be(0);
        Lines(renderer)[0].Should().Contain($"run '{LoadoutName}'", "the line names the loadout as it is stored");
    }

    [Fact]
    public async Task AnUnknownLoadoutFailsWith125AndNamesTheChoices()
    {
        await Install();
        var renderer = new LoggingRenderer();

        var exitCode = await Command().RunAsync(renderer, "Nope", null, write: false);

        exitCode.Should().Be(LaunchSession.FailureExitCode);
        Output(renderer).Should().Contain("No loadout named 'Nope'").And.Contain(LoadoutName);
    }

    [Fact]
    public async Task WarnsWhenTheLauncherScriptDoesNotExistYet()
    {
        await Install();
        var renderer = new LoggingRenderer();

        var exitCode = await Command().RunAsync(renderer, LoadoutName, null, write: false);

        exitCode.Should().Be(0);
        Output(renderer).Should().Contain(ShimFile).And.Contain("does not exist");
    }

    [Fact]
    public async Task WriteStoresTheLineInLocalConfig()
    {
        await Install();
        WriteShim();
        WriteConfig("-novid %command%");
        var renderer = new LoggingRenderer();

        var exitCode = await Command().RunAsync(renderer, LoadoutName, null, write: true);

        exitCode.Should().Be(0, Output(renderer));
        var stored = LocalConfigLaunchOptions.Read(Absolute(ConfigFile), uint.Parse(AppId)).Line;
        stored.Should().Be($"-novid '{ShimFile}' run '{LoadoutName}' -- %command%");
        File.Exists(ConfigFile + ".silvermod-bak").Should().BeTrue();
        Output(renderer).Should().Contain(ConfigFile);
    }

    [Fact]
    public async Task WriteIsRefusedWhileSteamRuns()
    {
        await Install();
        WriteShim();
        WriteConfig("-novid");
        var before = File.ReadAllText(ConfigFile);
        _tracker.IsSteamRunning().Returns(true);
        var renderer = new LoggingRenderer();

        var exitCode = await Command().RunAsync(renderer, LoadoutName, null, write: true);

        exitCode.Should().Be(LaunchSession.FailureExitCode);
        Output(renderer).Should().Contain("Close Steam");
        File.ReadAllText(ConfigFile).Should().Be(before);
    }

    [Fact]
    public async Task WriteIsRefusedWhenTheLauncherScriptIsMissing()
    {
        await Install();
        WriteConfig("-novid");
        var before = File.ReadAllText(ConfigFile);
        var renderer = new LoggingRenderer();

        var exitCode = await Command().RunAsync(renderer, LoadoutName, null, write: true);

        exitCode.Should().Be(LaunchSession.FailureExitCode);
        Output(renderer).Should().Contain(ShimFile);
        File.ReadAllText(ConfigFile).Should().Be(before);
    }

    [Fact]
    public async Task WriteIsRefusedForAGameThatIsNotInSteam()
    {
        await Install(store: GameStore.GOG, withProvider: false);
        WriteShim();
        var renderer = new LoggingRenderer();

        var exitCode = await Command().RunAsync(renderer, LoadoutName, null, write: true);

        exitCode.Should().Be(LaunchSession.FailureExitCode);
        Output(renderer).Should().Contain("Steam");
        File.Exists(ConfigFile).Should().BeFalse();
    }

    [Fact]
    public void TheHeadlessLaunchServicesRegisterTheVerb()
    {
        var services = new ServiceCollection();
        services.AddHeadlessLaunch();

        using var provider = services.BuildServiceProvider();
        var verb = provider.GetServices<VerbDefinition>().Single(v => v.Name == "steam-launch-option");

        verb.Options.Where(o => !o.IsInjected).Select(o => o.LongName).Should().BeEquivalentTo("loadout", "game", "write");
        verb.Options.Where(o => !o.IsInjected && o.LongName != "loadout").Should().OnlyContain(o => o.IsOptional);
    }
}
