using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.FromSoft;
using SilverModManager.Games.FromSoft.Build;
using SilverModManager.Games.FromSoft.EldenRing;
using SilverModManager.Games.FromSoft.Formats;
using SilverModManager.Games.FromSoft.Heal;
using SilverModManager.Games.FromSoft.Installers;
using SilverModManager.Games.FromSoft.Tests.Fixtures;
using SilverModManager.Games.FromSoft.Tests.Formats.Fixtures;
using SilverModManager.Games.TestFramework;
using SilverModManager.Games.TestFramework.Helpers;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.ProxyConsole;
using SilverModManager.Sdk.Settings;
using SilverModManager.SingleProcess;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.CLI.Tests;

/// <summary>
/// <c>silvermod fromsoft heal</c> against a made-up Steam install. The verb may write only under the data root, so
/// every test compares the data root and the game folder before and after.
/// </summary>
[Collection("ConsoleOutput")]
public class HealVerbTests(ITestOutputHelper helper) : ASteamInstalledGameTest<HealVerbTests, EldenRingGame>(helper)
{
    private static FromSoftFormatTraits Formats => EldenRingGame.Definition.Formats;

    protected override SteamInstall InstallGame(SteamLibraryFixture library) => EldenRingInstall.Install(library, dirty: false);

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        var dataRoot = new ConfigurablePath(null, TestScratch.Combine($"HealVerbs.{Guid.NewGuid():N}"));
        return base.AddServices(services)
            .AddFromSoftGames()
            .OverrideSettingsForTests<FromSoftSettings>(settings => settings with { DataRoot = dataRoot })
            .AddSingleton<CommandLineConfigurator>()
            .AddDefaultParsers()
            .AddCLI()
            .ReplaceDesktopNotifier();
    }

    private AbsolutePath RealGameFolder => EldenRingPaths.GameFolder(GameInstallation);

    private string[] SnapshotOfGameFolder() => TreeSnapshot.Take(RealGameFolder);

    private AbsolutePath DataRoot => ServiceProvider.GetRequiredService<ISettingsManager>().Get<FromSoftSettings>().DataRoot.ToPath(ServiceProvider.GetRequiredService<IFileSystem>());

    private string[] DataRootListing()
        => DataRoot.DirectoryExists()
            ? Directory.EnumerateFileSystemEntries(DataRoot.ToString(), "*", SearchOption.AllDirectories).Order().ToArray()
            : [];

    private BuildStampStore Stamps => ServiceProvider.GetRequiredService<BuildStampStore>();

    private BuildIdentity Live => BuildIdentity.Identify(GameInstallation);

    private BuildIdentity BeforeAPatch() => Live with { Exe = "2.6.0.0", SteamBuildId = "1", RegulationVersion = "11601000", RegulationHash = new string('b', 64) };

    private static byte[] ParamRegulation(string version, int stride)
        => RegulationFixture.Build(
            new Dictionary<int, (string, IReadOnlyDictionary<int, byte[]>, int)>
            {
                [1] = ("TEST_PARAM_ST", new Dictionary<int, byte[]>
                {
                    [1] = Enumerable.Repeat((byte)1, stride).ToArray(),
                    [2] = Enumerable.Repeat((byte)2, stride).ToArray(),
                }, stride),
            }, version, Formats);

    private async Task InstallPackage(Loadout.ReadOnly loadout, string name, params (string Path, byte[] Content)[] files)
    {
        var path = TemporaryFileManager.CreateFolder().Path / (name + ".zip");
        await TestArchives.WriteEntries(path, files);
        var archive = await RegisterLocalArchive(path);
        var installer = Game.LibraryItemInstallers.OfType<Me3PackageInstaller>().Single();
        await LoadoutManager.InstallItem(archive.AsLibraryFile().AsLibraryItem(), loadout.LoadoutId, installer: installer);
    }

    private async Task<(int ExitCode, string Output)> Run(params string[] args)
    {
        var renderer = new LoggingRenderer();
        var exitCode = await ServiceProvider.GetRequiredService<CommandLineConfigurator>().RunAsync(args, renderer, CancellationToken.None);
        return (exitCode, string.Concat(renderer.Logs.OfType<Text>().Select(t => t.Template + "\n")));
    }

    [Fact]
    public async Task DryRunPrintsTheStepsAndWritesNothing()
    {
        var loadout = await CreateLoadout();
        var stamped = BeforeAPatch();
        await Stamps.Record(loadout.InstallationId, stamped);
        var dataRoot = DataRootListing();
        var game = SnapshotOfGameFolder();

        var (exitCode, output) = await Run("fromsoft", "heal", "-l", loadout.Name, "--dry-run");

        exitCode.Should().Be(0, output);
        foreach (var step in HealPlan.Plan(stamped, Live, Formats)) output.Should().Contain(step.Detail);
        DataRootListing().Should().Equal(dataRoot);
        SnapshotOfGameFolder().Should().Equal(game);
    }

    [Fact]
    public async Task ARealRunKeepsTheInstalledRegulationAsTheBaselineAndTouchesNothingElse()
    {
        var loadout = await CreateLoadout();
        await Stamps.Record(loadout.InstallationId, BeforeAPatch());
        var game = SnapshotOfGameFolder();

        var (exitCode, output) = await Run("fromsoft", "heal", "-l", loadout.Name);

        exitCode.Should().Be(0, output);
        var baseline = DataRoot / "baselines" / $"regulation-{EldenRingInstall.RegulationVersion}.bin";
        File.ReadAllBytes(baseline.GetFullPath()).Should().Equal(EldenRingInstall.RegulationFile);
        output.Should().Contain("next apply");
        SnapshotOfGameFolder().Should().Equal(game);
        Stamps.Find(loadout.InstallationId).Should().Be(BeforeAPatch(), "stamping is the Health Check's Re-stamp action, after the user has looked");
    }

    [Fact]
    public async Task ATamperedInstallRefusesAndWritesNothingEvenWithoutDryRun()
    {
        var loadout = await CreateLoadout();
        var stamped = Live with { RegulationVersion = "11601000", RegulationHash = new string('b', 64) };
        await Stamps.Record(loadout.InstallationId, stamped);
        var dataRoot = DataRootListing();

        var (exitCode, output) = await Run("fromsoft", "heal", "-l", loadout.Name);

        exitCode.Should().NotBe(0);
        output.Should().Contain(HealPlan.TamperedRefusal(Live, Formats));
        DataRootListing().Should().Equal(dataRoot);
    }

    [Fact]
    public async Task AnUnchangedInstallHasNothingToHeal()
    {
        var loadout = await CreateLoadout();
        await Stamps.Record(loadout.InstallationId, Live);
        var dataRoot = DataRootListing();

        var (exitCode, output) = await Run("fromsoft", "heal", "-l", loadout.Name);

        exitCode.Should().Be(0, output);
        output.Should().Contain("Nothing to heal");
        DataRootListing().Should().Equal(dataRoot);
    }

    [Fact]
    public async Task AModWhoseRowLayoutDoesNotFitTheInstalledRegulationFailsTheGate()
    {
        File.WriteAllBytes((RealGameFolder / "regulation.bin").GetFullPath(), ParamRegulation("11701000", stride: 8));
        var loadout = await CreateLoadout();
        await InstallPackage(loadout, "Wide", ("regulation.bin", ParamRegulation("11601000", stride: 16)));
        await Stamps.Record(loadout.InstallationId, BeforeAPatch());

        var (exitCode, output) = await Run("fromsoft", "heal", "-l", loadout.Name);

        exitCode.Should().NotBe(0);
        output.Should().Contain("row stride 16").And.Contain("Wide");
    }
}
