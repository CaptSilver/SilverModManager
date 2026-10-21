using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Library.Installers;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Games.FromSoft.Doctor;
using NexusMods.Games.FromSoft.EldenRing;
using NexusMods.Games.FromSoft.Installers;
using NexusMods.Games.FromSoft.Tests.Fixtures;
using NexusMods.Games.TestFramework;
using NexusMods.Paths;
using NexusMods.Sdk.Loadouts;
using NexusMods.StandardGameLocators.TestHelpers;

namespace NexusMods.Games.FromSoft.Tests;

public class Me3HostMissingEmitterTests(ITestOutputHelper helper)
    : ALoadoutDiagnosticEmitterTest<Me3HostMissingEmitterTests, EldenRingGame, Me3HostMissingEmitter>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddFromSoftGames()
            .AddUniversalGameLocator<EldenRingGame>(new Version("1.0"));
    }

    private async Task<LoadoutItemGroup.ReadOnly> Install<TInstaller>(LoadoutId loadout, string archiveName, params string[] paths)
        where TInstaller : ILibraryItemInstaller
    {
        var path = TemporaryFileManager.CreateFolder().Path / archiveName;
        if (archiveName.EndsWith(".tar.gz")) await TestArchives.WriteTarGz(path, paths);
        else await TestArchives.Write(path, paths);
        var archive = await RegisterLocalArchive(path);
        var installer = Game.LibraryItemInstallers.OfType<TInstaller>().Single();
        var result = await LoadoutManager.InstallItem(archive.AsLibraryFile().AsLibraryItem(), loadout, installer: installer);
        return result.LoadoutItemGroup!.Value;
    }

    private Task<LoadoutItemGroup.ReadOnly> InstallHost(LoadoutId loadout)
        => Install<Me3HostInstaller>(loadout, "me3-linux-amd64.tar.gz", "bin/me3", "bin/win64/me3-launcher.exe", "bin/win64/me3_mod_host.dll");

    private Task<LoadoutItemGroup.ReadOnly> InstallPackage(LoadoutId loadout)
        => Install<Me3PackageInstaller>(loadout, "Weapons.zip", "parts/wp_a.dcx");

    [Fact]
    public async Task APackageWithoutTheHostIsCritical()
    {
        var loadout = await CreateLoadout();
        await InstallPackage(loadout);

        var diagnostic = await GetSingleDiagnostic(loadout);

        diagnostic.Severity.Should().Be(DiagnosticSeverity.Critical);
        diagnostic.Id.Should().Be(new DiagnosticId("NexusMods.Games.FromSoft", 3));
    }

    [Fact]
    public async Task ANativeWithoutTheHostIsCritical()
    {
        var loadout = await CreateLoadout();
        await Install<Me3NativeInstaller>(loadout, "Hook.zip", "hook.dll");

        (await GetSingleDiagnostic(loadout)).Severity.Should().Be(DiagnosticSeverity.Critical);
    }

    [Fact]
    public async Task APackageWithTheHostInstalledHasNoDiagnostic()
    {
        var loadout = await CreateLoadout();
        await InstallPackage(loadout);
        await InstallHost(loadout);

        await ShouldHaveNoDiagnostics(loadout);
    }

    [Fact]
    public async Task ADisabledPackageWithoutTheHostHasNoDiagnostic()
    {
        var loadout = await CreateLoadout();
        var package = await InstallPackage(loadout);

        await DisableItem(package.Id);

        await ShouldHaveNoDiagnostics(loadout, because: "a disabled package is not loaded, so me3 is not needed");
    }

    [Fact]
    public async Task ADisabledHostCountsAsMissing()
    {
        var loadout = await CreateLoadout();
        await InstallPackage(loadout);
        var host = await InstallHost(loadout);

        await DisableItem(host.Id);

        (await GetSingleDiagnostic(loadout)).Severity.Should().Be(DiagnosticSeverity.Critical);
    }

    [Fact]
    public async Task NoModsAndNoHostHasNoDiagnostic()
    {
        var loadout = await CreateLoadout();

        await ShouldHaveNoDiagnostics(loadout);
    }

    [Fact]
    public async Task TheMessageNamesTheReleaseToInstall()
    {
        var loadout = await CreateLoadout();
        await InstallPackage(loadout);

        var diagnostic = await GetSingleDiagnostic(loadout);

        var writer = ServiceProvider.GetRequiredService<IDiagnosticWriter>();
        diagnostic.FormatSummary(writer).Should().Be("me3 is not installed for this loadout");
        diagnostic.FormatDetails(writer).Should().Contain("me3-linux-amd64.tar.gz");
    }
}
