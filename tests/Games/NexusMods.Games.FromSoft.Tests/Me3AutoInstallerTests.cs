using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Library;
using NexusMods.Abstractions.Library.Installers;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Games.FromSoft.Doctor;
using NexusMods.Games.FromSoft.EldenRing;
using NexusMods.Games.FromSoft.Installers;
using NexusMods.Games.FromSoft.Me3;
using NexusMods.Games.FromSoft.Tests.Fixtures;
using NexusMods.Games.TestFramework;
using NexusMods.Paths;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Jobs;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Loadouts;
using NexusMods.StandardGameLocators.TestHelpers;

namespace NexusMods.Games.FromSoft.Tests;

public class Me3AutoInstallerTests(ITestOutputHelper helper) : Me3ProvisioningTest<Me3AutoInstallerTests>(helper)
{
    protected override bool AutoInstall => true;

    [Fact]
    public async Task AnUnmoddedLoadoutFetchesNothing()
    {
        var loadout = await CreateLoadout();

        await ServiceProvider.GetRequiredService<Me3AutoInstaller>().OnInstallCompleted(loadout);

        Source.Calls.Should().Be(0);
        HostFileCount(loadout).Should().Be(0);
    }

    [Fact]
    public async Task TheFirstPackageInstalledFetchesTheReleaseOnceAndLeavesTheBinaryInTheHostLocation()
    {
        var loadout = await CreateLoadout();
        Source.Result = () => AddReleaseTarball();

        await InstallPackage(loadout);
        await Eventually(() => HostFileCount(loadout) == 1);

        Source.Calls.Should().Be(1);
        await ShouldHaveNoDiagnostics(loadout);
    }

    [Fact]
    public async Task AFailedFetchAfterAnInstallIsLoggedAndNeverThrownAtTheInstall()
    {
        var loadout = await CreateLoadout();
        await InstallPackage(loadout);
        Source.Result = () => throw new HttpRequestException("offline");

        var act = async () => await ServiceProvider.GetRequiredService<Me3AutoInstaller>().OnInstallCompleted(loadout);

        await act.Should().NotThrowAsync();
        HostFileCount(loadout).Should().Be(0);
    }
}
