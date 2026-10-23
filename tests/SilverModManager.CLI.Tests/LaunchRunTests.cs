using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SilverModManager.Abstractions.Secrets;
using SilverModManager.App;
using SilverModManager.Backend;
using NexusMods.Paths;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Settings;
using SilverModManager.StandardGameLocators.TestHelpers;
using SilverModManager.DataModel;
using SilverModManager.Games.FileHashes;

namespace SilverModManager.CLI.Tests;

public class LaunchRunTests
{
    private static IHost BuildRunHost()
    {
        const KnownPath baseKnownPath = KnownPath.EntryDirectory;
        var baseDirectory = $"LaunchRunTests-{Guid.NewGuid()}";
        var mode = StartupMode.Parse(["run", "x", "--", "y"]);
        mode.SecretBackend = SecretBackendChoice.Memory;

        return new HostBuilder()
            .ConfigureServices(services => services
                .AddApp(addStandardGameLocators: false, startupMode: mode)
                .Validate()
                .OverrideSettingsForTests<FileHashesServiceSettings>(settings => settings with { UseInMemoryLocalBaseline = true, })
                .OverrideSettingsForTests<DataModelSettings>(settings => settings with
                {
                    UseInMemoryDataModel = true,
                    MnemonicDBPath = new ConfigurablePath(baseKnownPath, $"{baseDirectory}/MnemonicDB.rocksdb"),
                    ArchiveLocations = [new ConfigurablePath(baseKnownPath, $"{baseDirectory}/Archives")],
                })
                .RemoveRuntimeDependencyChecker()
                .ReplaceDesktopNotifier())
            .Build();
    }

    [Fact]
    public async Task ALoadoutThatDoesNotExistFailsWith125AndTellsTheDesktopWithoutStartingASession()
    {
        using var host = BuildRunHost();
        LaunchArgs.TryParse(["Missing Loadout", "--", "game.exe"], out var args, out _).Should().BeTrue();

        var exitCode = await LaunchRun.RunAsync(host.Services, args!);

        exitCode.Should().Be(LaunchSession.FailureExitCode);
        var notifier = (RecordingDesktopNotifier)host.Services.GetRequiredService<IDesktopNotifier>();
        notifier.Notifications.Should().ContainSingle().Which.Body.Should().Contain("Missing Loadout");
        host.Services.GetRequiredService<LaunchSessionSlot>().Current.Should().BeNull();
    }
}
