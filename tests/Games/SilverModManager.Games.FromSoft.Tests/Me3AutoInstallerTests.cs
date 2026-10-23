using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.FromSoft.Doctor;
using SilverModManager.Games.FromSoft.EldenRing;
using SilverModManager.Games.FromSoft.Installers;
using SilverModManager.Games.FromSoft.Me3;
using SilverModManager.Games.FromSoft.Tests.Fixtures;
using SilverModManager.Games.TestFramework;
using NexusMods.Paths;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.Games.FromSoft.Tests;

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
