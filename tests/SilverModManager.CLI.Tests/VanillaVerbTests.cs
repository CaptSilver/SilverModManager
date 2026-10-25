using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Steam;
using SilverModManager.Games.FromSoft;
using SilverModManager.Games.FromSoft.EldenRing;
using SilverModManager.Games.FromSoft.Formats;
using SilverModManager.Games.FromSoft.Heal;
using SilverModManager.Games.FromSoft.Tests.Fixtures;
using SilverModManager.Games.FromSoft.Tests.Formats.Fixtures;
using SilverModManager.Games.FromSoft.Tests.Heal;
using SilverModManager.Games.TestFramework;
using SilverModManager.Games.TestFramework.Helpers;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.ProxyConsole;
using SilverModManager.Sdk.Settings;
using SilverModManager.SingleProcess;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.CLI.Tests;

/// <summary>
/// <c>silvermod fromsoft vanilla import</c> against a made-up Steam install whose manifests are in memory. The verb may
/// write only under the data root, so every test compares the game folder before and after.
/// </summary>
[Collection("ConsoleOutput")]
public class VanillaVerbTests(ITestOutputHelper helper) : ASteamInstalledGameTest<VanillaVerbTests, EldenRingGame>(helper)
{
    private const string OlderVersion = "11611000";

    private static FromSoftFormatTraits Formats => EldenRingGame.Definition.Formats;

    private readonly FakeManifestCatalog _manifests = new();

    protected override SteamInstall InstallGame(SteamLibraryFixture library) => EldenRingInstall.Install(library, dirty: false);

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        var dataRoot = new ConfigurablePath(null, TestScratch.Combine($"VanillaVerbs.{Guid.NewGuid():N}"));
        return base.AddServices(services)
            .AddFromSoftGames()
            .OverrideSettingsForTests<FromSoftSettings>(settings => settings with { DataRoot = dataRoot })
            .AddSingleton<IDepotManifestSource>(_manifests)
            .AddSingleton<CommandLineConfigurator>()
            .AddDefaultParsers()
            .AddCLI()
            .ReplaceDesktopNotifier();
    }

    private AbsolutePath DataRoot => ServiceProvider.GetRequiredService<ISettingsManager>().Get<FromSoftSettings>().DataRoot.ToPath(ServiceProvider.GetRequiredService<IFileSystem>());

    private static byte[] Regulation(string version) => RegulationFixture.BuildFromEntries([(1, "a.param", new byte[16])], version, Formats);

    private AbsolutePath Saved(byte[] regulation)
    {
        var path = TemporaryFileManager.CreateFolder().Path / "regulation-saved.bin";
        File.WriteAllBytes(path.GetFullPath(), regulation);
        return path;
    }

    // Steam's manifest of the installed depot, as the locator names it, lists the file.
    private void SteamShipped(byte[] regulation)
    {
        var id = GameInstallation.LocatorResult.LocatorIds.Single().Value;
        _manifests.Add(depot: 1245621, id: ulong.Parse(id), files: regulation);
    }

    private async Task<(int ExitCode, string Output)> Run(params string[] args)
    {
        var renderer = new LoggingRenderer();
        var exitCode = await ServiceProvider.GetRequiredService<CommandLineConfigurator>().RunAsync(args, renderer, CancellationToken.None);
        return (exitCode, string.Concat(renderer.Logs.OfType<Text>().Select(t => t.Template + "\n")));
    }

    [Fact]
    public async Task ASteamShippedRegulationOfAnOlderBuildIsKeptAsItsVanillaBaseline()
    {
        var loadout = await CreateLoadout();
        var older = Regulation(OlderVersion);
        SteamShipped(older);
        var file = Saved(older);
        var game = TreeSnapshot.Take(EldenRingPaths.GameFolder(GameInstallation));

        var (exitCode, output) = await Run("fromsoft", "vanilla", "import", "-l", loadout.Name, "-f", file.GetFullPath());

        exitCode.Should().Be(0, output);
        output.Should().Contain(OlderVersion).And.Contain("vouched");
        File.ReadAllBytes((DataRoot / "baselines" / $"regulation-{OlderVersion}.bin").GetFullPath()).Should().Equal(older);
        TreeSnapshot.Take(EldenRingPaths.GameFolder(GameInstallation)).Should().Equal(game);
    }

    [Fact]
    public async Task ARegulationNoManifestListsIsRefusedAndNothingIsWritten()
    {
        var loadout = await CreateLoadout();
        var file = Saved(Regulation(OlderVersion));

        var (exitCode, output) = await Run("fromsoft", "vanilla", "import", "-l", loadout.Name, "-f", file.GetFullPath());

        exitCode.Should().NotBe(0);
        output.Should().Contain("Steam");
        (DataRoot / "baselines").DirectoryExists().Should().BeFalse();
    }

    [Fact]
    public async Task AFileOfAnotherBuildThanAskedForIsRefused()
    {
        var loadout = await CreateLoadout();
        var older = Regulation(OlderVersion);
        SteamShipped(older);

        var (exitCode, output) = await Run("fromsoft", "vanilla", "import", "-l", loadout.Name, "-f", Saved(older).GetFullPath(), "-v", "11701000");

        exitCode.Should().NotBe(0);
        output.Should().Contain(OlderVersion).And.Contain("11701000");
        (DataRoot / "baselines").DirectoryExists().Should().BeFalse();
    }

    [Fact]
    public async Task ImportingTheSameFileTwiceSaysNothingChanged()
    {
        var loadout = await CreateLoadout();
        var older = Regulation(OlderVersion);
        SteamShipped(older);
        var file = Saved(older).GetFullPath();
        await Run("fromsoft", "vanilla", "import", "-l", loadout.Name, "-f", file);

        var (exitCode, output) = await Run("fromsoft", "vanilla", "import", "-l", loadout.Name, "-f", file);

        exitCode.Should().Be(0, output);
        output.Should().Contain("nothing changed");
    }
}
