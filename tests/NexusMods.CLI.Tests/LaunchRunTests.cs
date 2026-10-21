using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NexusMods.App;
using NexusMods.Paths;
using NexusMods.Platform.Proton.Launch;
using NexusMods.Sdk;
using NexusMods.Sdk.Settings;
using NexusMods.StandardGameLocators.TestHelpers;
using NexusMods.DataModel;
using NexusMods.Games.FileHashes;

namespace NexusMods.CLI.Tests;

public class LaunchRunTests
{
    private static IHost BuildRunHost()
    {
        const KnownPath baseKnownPath = KnownPath.EntryDirectory;
        var baseDirectory = $"LaunchRunTests-{Guid.NewGuid()}";

        return new HostBuilder()
            .ConfigureServices(services => services
                .AddApp(addStandardGameLocators: false, startupMode: StartupMode.Parse(["run", "x", "--", "y"]))
                .Validate()
                .OverrideSettingsForTests<FileHashesServiceSettings>(settings => settings with { UseInMemoryLocalBaseline = true, })
                .OverrideSettingsForTests<DataModelSettings>(settings => settings with
                {
                    UseInMemoryDataModel = true,
                    MnemonicDBPath = new ConfigurablePath(baseKnownPath, $"{baseDirectory}/MnemonicDB.rocksdb"),
                    ArchiveLocations = [new ConfigurablePath(baseKnownPath, $"{baseDirectory}/Archives")],
                })
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
