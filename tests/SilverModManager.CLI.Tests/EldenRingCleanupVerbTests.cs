using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexusMods.Abstractions.Games;
using NexusMods.Games.FromSoft;
using NexusMods.Games.FromSoft.Cleanup;
using NexusMods.Games.FromSoft.EldenRing;
using NexusMods.Games.TestFramework;
using NexusMods.Paths;
using NexusMods.Platform.Proton.Launch;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.ProxyConsole;
using NexusMods.Sdk.Settings;
using NexusMods.SingleProcess;
using NexusMods.StandardGameLocators.TestHelpers;
using NSubstitute;

namespace NexusMods.CLI.Tests;

/// <summary>
/// <c>silvermod elden-ring cleanup</c> against a made-up game folder shaped like an install another mod manager left
/// behind. The probe never reports the launcher as immutable, and Steam's files are never read.
/// </summary>
[Collection("ConsoleOutput")]
public class EldenRingCleanupVerbTests(ITestOutputHelper helper) : AIsolatedGameTest<EldenRingCleanupVerbTests, EldenRingGame>(helper)
{
    private const string StubbedSteamId = "3976631895";

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        var dataRoot = new ConfigurablePath(KnownPath.EntryDirectory, $"EldenRingCleanup.{Guid.NewGuid():N}");
        base.AddServices(services)
            .AddFromSoftGames()
            .OverrideSettingsForTests<FromSoftSettings>(settings => settings with { DataRoot = dataRoot })
            .AddSingleton<IGameLocator>(provider => new UniversalStubbedGameLocator<EldenRingGame>(
                provider,
                provider.GetRequiredService<IFileSystem>(),
                provider.GetRequiredService<TemporaryFileManager>(),
                stores: [GameStore.Steam])
            {
                LocatorIds = [LocatorId.From(StubbedSteamId)],
            })
            .AddSingleton<CommandLineConfigurator>()
            .AddDefaultParsers()
            .AddCLI()
            .ReplaceDesktopNotifier();
        services.Replace(ServiceDescriptor.Singleton(Substitute.For<IImmutableFlagProbe>()));
        services.Replace(ServiceDescriptor.Singleton(Substitute.For<ISteamLaunchOptionWriter>()));
        return services;
    }

    private AbsolutePath Game => EldenRingPaths.GameFolder(GameInstallation);

    private string Hook => Path.Combine(Game.Parent.ToString(), "reshade/ReShade64.dll");

    private void WriteErmInstall()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Hook)!);
        File.WriteAllText(Hook, "hook");
        Game.CreateDirectory();
        File.WriteAllText((Game / "eldenring.exe").ToString(), "game");
        File.WriteAllText((Game / "start_protected_game.exe").ToString(), "game");
        File.WriteAllText((Game / "start_protected_game.exe.erm-backup").ToString(), "launcher");
        (Game / "mods/Fov").CreateDirectory();
        File.WriteAllText((Game / "mods/Fov/config.ini").ToString(), "[fov]");
        File.WriteAllText((Game / "ReShade.log").ToString(), "log");
        File.CreateSymbolicLink((Game / "dxgi.dll").ToString(), Hook);
    }

    private async Task<(int ExitCode, string Output)> Run(params string[] args)
    {
        var renderer = new LoggingRenderer();
        var exitCode = await ServiceProvider.GetRequiredService<CommandLineConfigurator>().RunAsync(args, renderer, CancellationToken.None);
        return (exitCode, string.Concat(renderer.Logs.OfType<Text>().Select(t => t.Template)));
    }

    [Fact]
    public async Task PlanPrintsTheInventoryAndTheStepsAndChangesNothing()
    {
        WriteErmInstall();
        var loadout = await CreateLoadout();
        var before = Directory.EnumerateFileSystemEntries(Game.ToString(), "*", SearchOption.AllDirectories).Order().ToArray();

        var (exitCode, output) = await Run("elden-ring", "cleanup", "--plan", "-l", loadout.Name);

        exitCode.Should().Be(0, output);
        output.Should().Contain("link  dxgi.dll -> " + Hook);
        output.Should().Contain("Move start_protected_game.exe.erm-backup over start_protected_game.exe");
        output.Should().Contain("Copy mods/Fov/config.ini");
        output.Should().Contain("Delete mods/Fov").And.Contain("Delete link dxgi.dll").And.Contain("Delete ReShade.log");
        Directory.EnumerateFileSystemEntries(Game.ToString(), "*", SearchOption.AllDirectories).Order().Should().Equal(before);
        File.ReadAllText((Game / "start_protected_game.exe").ToString()).Should().Be("game");
        File.Exists(Hook).Should().BeTrue();
    }

    [Fact]
    public async Task ApplyWithoutConfirmationPrintsTheStepsAndChangesNothing()
    {
        WriteErmInstall();
        var loadout = await CreateLoadout();

        var (exitCode, output) = await Run("elden-ring", "cleanup", "--apply", "-l", loadout.Name);

        exitCode.Should().NotBe(0);
        output.Should().Contain("Move start_protected_game.exe.erm-backup").And.Contain("--yes");
        (Game / "mods/Fov/config.ini").FileExists.Should().BeTrue();
        File.ReadAllText((Game / "start_protected_game.exe").ToString()).Should().Be("game");
    }

    [Fact]
    public async Task ApplyWithConfirmationRunsTheStepsAndPrintsWhatChanged()
    {
        WriteErmInstall();
        var loadout = await CreateLoadout();

        var (exitCode, output) = await Run("elden-ring", "cleanup", "--apply", "--yes", "-l", loadout.Name);

        exitCode.Should().Be(0, output);
        File.ReadAllText((Game / "start_protected_game.exe").ToString()).Should().Be("launcher");
        (Game / "mods").DirectoryExists().Should().BeFalse();
        File.Exists(Hook).Should().BeTrue();
        output.Should().Contain("removed  dxgi.dll").And.Contain("removed  mods/Fov/config.ini").And.Contain("changed  start_protected_game.exe");
    }

    [Fact]
    public async Task ExactlyOneOfPlanAndApplyIsRequired()
    {
        var loadout = await CreateLoadout();

        var (neither, _) = await Run("elden-ring", "cleanup", "-l", loadout.Name);
        var (both, _) = await Run("elden-ring", "cleanup", "--plan", "--apply", "-l", loadout.Name);

        neither.Should().NotBe(0);
        both.Should().NotBe(0);
    }
}
