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

public class Me3HostProvisionerTests(ITestOutputHelper helper) : Me3ProvisioningTest<Me3HostProvisionerTests>(helper)
{
    protected override bool AutoInstall => false;

    [Fact]
    public async Task ALoadoutThatAlreadyHasTheHostFetchesNothing()
    {
        var loadout = await CreateLoadout();
        await InstallPackage(loadout);
        Source.Result = () => AddReleaseTarball();
        await Provisioner.EnsureInstalled(loadout, CancellationToken.None);

        await Provisioner.EnsureInstalled(loadout, CancellationToken.None);

        Source.Calls.Should().Be(1);
        HostFileCount(loadout).Should().Be(1);
    }

    [Fact]
    public async Task AReleaseAlreadyInTheLibraryIsInstalledWithoutADownload()
    {
        var loadout = await CreateLoadout();
        await InstallPackage(loadout);
        await AddReleaseTarball();

        await Provisioner.EnsureInstalled(loadout, CancellationToken.None);

        Source.Calls.Should().Be(0);
        HostFileCount(loadout).Should().Be(1);
    }

    [Fact]
    public async Task ALibraryArchiveThatIsNotTheLinuxReleaseIsNotUsed()
    {
        var loadout = await CreateLoadout();
        await InstallPackage(loadout);
        var windows = TemporaryFileManager.CreateFolder().Path / "me3-windows-amd64.zip";
        await TestArchives.Write(windows, "bin/me3.exe", "bin/win64/me3-launcher.exe", "bin/win64/me3_mod_host.dll");
        await RegisterLocalArchive(windows);
        Source.Result = () => AddReleaseTarball();

        await Provisioner.EnsureInstalled(loadout, CancellationToken.None);

        Source.Calls.Should().Be(1);
        HostFileCount(loadout).Should().Be(1);
    }

    [Fact]
    public async Task AFailedFetchAddsNothingAndLeavesTheCriticalInPlace()
    {
        var loadout = await CreateLoadout();
        await InstallPackage(loadout);
        Source.Result = () => throw new InvalidOperationException("SHA-256 mismatch for asset 'me3-linux-amd64.tar.gz'");

        var act = async () => await Provisioner.EnsureInstalled(loadout, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*SHA-256 mismatch*");
        HostFileCount(loadout).Should().Be(0);
        (await GetSingleDiagnostic(loadout)).Severity.Should().Be(DiagnosticSeverity.Critical);
    }

    [Fact]
    public async Task TwoTriggersAtOnceDownloadOnce()
    {
        var loadout = await CreateLoadout();
        await InstallPackage(loadout);
        var release = new TaskCompletionSource();
        Source.Result = async () =>
        {
            await release.Task;
            return await AddReleaseTarball();
        };

        var first = Provisioner.EnsureInstalled(loadout, CancellationToken.None);
        var second = Provisioner.EnsureInstalled(loadout, CancellationToken.None);
        await Eventually(() => Source.Calls >= 1);
        release.SetResult();
        await Task.WhenAll(first, second);

        Source.Calls.Should().Be(1);
        HostFileCount(loadout).Should().Be(1);
    }

    [Fact]
    public async Task TheMissingHostDiagnosticOffersTheInstallFixAndTheFixClearsIt()
    {
        var loadout = await CreateLoadout();
        await InstallPackage(loadout);
        Source.Result = () => AddReleaseTarball();

        var diagnostic = await GetSingleDiagnostic(loadout);

        diagnostic.Actions.Should().ContainSingle().Which.Should().Be(Me3HostMissingEmitter.InstallAction());
        Me3HostMissingEmitter.InstallAction().Label.Should().Be("Install me3");
        diagnostic.FormatDetails(ServiceProvider.GetRequiredService<IDiagnosticWriter>()).Should().Contain("installs me3 automatically");

        await Provisioner.EnsureInstalled(loadout, CancellationToken.None);

        await ShouldHaveNoDiagnostics(loadout);
    }
}
