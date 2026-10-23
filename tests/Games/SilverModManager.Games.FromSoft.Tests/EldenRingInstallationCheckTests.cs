using System.Text.Json;
using CliWrap;
using FluentAssertions;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexusMods.Games.FromSoft.Doctor;
using NexusMods.Games.FromSoft.Me3;
using NexusMods.Games.FromSoft.Overlay;
using NexusMods.Games.FromSoft.Tests.Fixtures;
using NexusMods.Paths;
using NexusMods.Platform.Proton.Doctor;
using NexusMods.Platform.Proton.Launch;
using NexusMods.Platform.Proton.Steam;
using NSubstitute;
using NexusMods.Sdk;
using NexusMods.Sdk.IO;
using NexusMods.Sdk.NexusModsApi;
using DynamicData.Kernel;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Settings;

namespace NexusMods.Games.FromSoft.Tests;

public class EldenRingInstallationCheckTests(ITestOutputHelper helper) : AModdedEldenRingTest<EldenRingInstallationCheckTests>(helper)
{
    private readonly FakeProcessRunner _runner = new();

    // Only the version probe gets the fake: the archive extractors run real processes through the shared runner.
    protected override IServiceCollection AddServices(IServiceCollection services)
        => base.AddServices(services).Replace(ServiceDescriptor.Singleton(new Me3VersionProbe(_runner)));

    private IInstallationCheck Check => ServiceProvider.GetServices<IInstallationCheck>().OfType<FromSoftInstallationCheck>().Single();

    private void WriteInGameFolder(string relative)
    {
        var path = RealGameFolder / relative;
        path.Parent.CreateDirectory();
        File.WriteAllText(path.GetFullPath(), "x");
    }

    [Fact]
    public void AGameFolderWithAProxyDllAndTheLauncherHasOneFailingArtifactsCheck()
    {
        WriteInGameFolder("dinput8.dll");

        var checks = Check.Check(GameInstallation);

        checks.Where(c => c.Level == DoctorCheckLevel.Fail).Should().ContainSingle().Which.Id.Should().Be("artifacts");
    }

    [Fact]
    public void TheJsonCarriesTheChecksWithCamelCaseLevels()
    {
        WriteInGameFolder("dinput8.dll");
        var prefixes = Substitute.For<IPrefixResolver>();
        prefixes.Resolve(Arg.Any<GameInstallation>()).Returns(PrefixResolution.Native("fixture"));
        var proton = Substitute.For<IProtonResolver>();
        proton.ResolveForApp(Arg.Any<AbsolutePath>(), Arg.Any<uint>()).Returns(ProtonResolution.Failed("fixture"));
        var reporter = new DoctorReporter(
            prefixes,
            proton,
            Substitute.For<IToolRunner>(),
            ServiceProvider.GetRequiredService<IInstallationLockFactory>(),
            [Check]);

        var json = JsonDocument.Parse(reporter.Build([new ManagedInstallation(GameInstallation, ["Main"])]).ToJson());

        var checks = json.RootElement.GetProperty("installations")[0].GetProperty("checks").EnumerateArray().ToArray();
        checks.Where(c => c.GetProperty("level").GetString() == "fail").Should().ContainSingle()
            .Which.GetProperty("id").GetString().Should().Be("artifacts");
    }

    [Fact]
    public void AnotherGamesInstallationGetsNoChecks()
    {
        var other = new OtherGame();
        var original = GameInstallation.LocatorResult;
        var installation = new GameInstallation(new GameLocatorResult
        {
            Game = other,
            Path = original.Path,
            Store = original.Store,
            StoreIdentifier = original.StoreIdentifier,
            LocatorIds = ImmutableArray<LocatorId>.Empty,
            Locator = Substitute.For<IGameLocator>(),
            Platform = OSPlatform.Windows,
        }, GameInstallation.Locations);

        Check.Check(installation).Should().BeEmpty();
    }

    [Fact]
    public async Task ADeployedMe3ReportsItsVersion()
    {
        await CreateModdedLoadout();
        _runner.Output = "me3 0.13.0\n";

        var me3 = Check.Check(GameInstallation).Single(c => c.Id == "me3");

        me3.Level.Should().Be(DoctorCheckLevel.Ok);
        me3.Message.Should().Contain("me3 0.13.0");
        _runner.Ran.Should().ContainSingle().Which.Should().EndWith("bin/me3");
    }

    [Fact]
    public async Task AMe3ThatCannotBeStartedWarnsInsteadOfFailingTheReport()
    {
        await CreateModdedLoadout();
        _runner.StartFailure = new System.ComponentModel.Win32Exception(8, "Exec format error");

        var me3 = Check.Check(GameInstallation).Single(c => c.Id == "me3");

        me3.Level.Should().Be(DoctorCheckLevel.Warn);
        me3.Message.Should().Contain("did not report a version");
    }

    [Fact]
    public void WithoutMe3TheCheckWarnsAndRunsNothing()
    {
        var me3 = Check.Check(GameInstallation).Single(c => c.Id == "me3");

        me3.Level.Should().Be(DoctorCheckLevel.Warn);
        _runner.Ran.Should().BeEmpty();
    }

    [Fact]
    public void WithoutBwrapTheOverlayCheckFails()
    {
        Executables.Bwrap = null;

        Check.Check(GameInstallation).Single(c => c.Id == "overlay").Level.Should().Be(DoctorCheckLevel.Fail);
    }

    [Fact]
    public async Task APendingFindingFromAnyLoadoutIsListed()
    {
        var loadout = await CreateModdedLoadout();
        var layout = LayoutOf(loadout);
        await Gate.Prepare(ContextOf(loadout), CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(layout.Upper.GetFullPath(), "stray.txt"), "x");
        await Gate.Finish(ContextOf(loadout));

        var upperdir = Check.Check(GameInstallation).Single(c => c.Id == "upperdir");

        upperdir.Level.Should().Be(DoctorCheckLevel.Warn);
        upperdir.Message.Should().Contain("stray.txt");
    }

    [Fact]
    public void NoFindingsMeansNoUpperdirCheck()
    {
        Check.Check(GameInstallation).Should().NotContain(c => c.Id == "upperdir");
    }

    private sealed class OtherGame : IGameData
    {
        public GameId GameId { get; } = GameId.From("Other.Game");
        public StoreIdentifiers StoreIdentifiers => new(GameId);
        public string DisplayName => "Other Game";
        public Optional<NexusModsGameId> NexusModsGameId => default;
        public IStreamFactory IconImage => throw new NotSupportedException();
        public IStreamFactory TileImage => throw new NotSupportedException();
        public ImmutableDictionary<LocationId, AbsolutePath> GetLocations(IFileSystem fileSystem, GameLocatorResult gameLocatorResult) => throw new NotSupportedException();
        public GamePath GetPrimaryFile(GameInstallation installation) => throw new NotSupportedException();
    }

    private sealed class FakeProcessRunner : IProcessRunner
    {
        public string Output { get; set; } = "";
        public Exception? StartFailure { get; set; }
        public List<string> Ran { get; } = [];

        public void Run(Command command, bool logOutput = false) => throw new NotSupportedException();
        public Task RunAsync(System.Diagnostics.Process process, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async Task<CommandResult> RunAsync(Command command, bool logOutput = false, CancellationToken cancellationToken = default)
        {
            Ran.Add(command.TargetFilePath);
            if (StartFailure is { } failure) throw failure;
            await command.StandardOutputPipe.CopyFromAsync(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(Output)), cancellationToken);
            return new CommandResult(0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        }
    }
}
