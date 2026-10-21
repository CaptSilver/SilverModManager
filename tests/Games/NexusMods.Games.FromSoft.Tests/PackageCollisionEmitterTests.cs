using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Library.Installers;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Games.FromSoft.Diagnostics;
using NexusMods.Games.FromSoft.Doctor;
using NexusMods.Games.FromSoft.EldenRing;
using NexusMods.Games.FromSoft.Installers;
using NexusMods.Games.FromSoft.Tests.Fixtures;
using NexusMods.Games.TestFramework;
using NexusMods.Paths;
using NexusMods.Sdk.Loadouts;
using NexusMods.StandardGameLocators.TestHelpers;

namespace NexusMods.Games.FromSoft.Tests;

public class PackageCollisionEmitterTests(ITestOutputHelper helper)
    : ALoadoutDiagnosticEmitterTest<PackageCollisionEmitterTests, EldenRingGame, PackageCollisionEmitter>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddFromSoftGamesWithOwnDataRoot()
            .AddUniversalGameLocator<EldenRingGame>(new Version("1.0"));
    }

    private async Task<LoadoutItemGroup.ReadOnly> Install<TInstaller>(LoadoutId loadout, string archiveName, params string[] paths)
        where TInstaller : ILibraryItemInstaller
    {
        var path = TemporaryFileManager.CreateFolder().Path / archiveName;
        await TestArchives.Write(path, paths);
        var archive = await RegisterLocalArchive(path);
        var installer = Game.LibraryItemInstallers.OfType<TInstaller>().Single();
        var result = await LoadoutManager.InstallItem(archive.AsLibraryFile().AsLibraryItem(), loadout, installer: installer);
        return result.LoadoutItemGroup!.Value;
    }

    private Task<LoadoutItemGroup.ReadOnly> InstallPackage(LoadoutId loadout, string name, params string[] paths)
        => Install<Me3PackageInstaller>(loadout, name + ".zip", paths);

    [Fact]
    public async Task PackagesThatProvideDifferentPathsDoNotCollide()
    {
        var loadout = await CreateLoadout();
        await InstallPackage(loadout, "Weapons", "parts/wp_a.dcx");
        await InstallPackage(loadout, "Armor", "parts/am_a.dcx");

        await ShouldHaveNoDiagnostics(loadout);
    }

    [Fact]
    public async Task TwoPackagesProvidingOnePathRaiseOneCriticalNamingBothAndThePath()
    {
        var loadout = await CreateLoadout();
        await InstallPackage(loadout, "Weapons", "parts/wp_a.dcx", "menu/only_here.dds");
        await InstallPackage(loadout, "Armor", "parts/wp_a.dcx");

        var diagnostic = await GetSingleDiagnostic(loadout);

        diagnostic.Severity.Should().Be(DiagnosticSeverity.Critical);
        diagnostic.Id.Should().Be(new DiagnosticId("NexusMods.Games.FromSoft", 1));
        var data = diagnostic.Should().BeOfType<Diagnostic<FromSoftDiagnostics.PackageCollisionMessageData>>().Which.MessageData;
        data.Packages.Should().Be("Armor, Weapons");
        data.Path.Should().Be("parts/wp_a.dcx");
        data.CaseNote.Should().BeEmpty();
    }

    [Fact]
    public async Task TwoPathsThatDifferOnlyInCaseRaiseTheSameCriticalWithASentenceAboutIt()
    {
        var loadout = await CreateLoadout();
        await InstallPackage(loadout, "Weapons", "msg/engus/Item.msgbnd.dcx");
        await InstallPackage(loadout, "Armor", "msg/engus/item.msgbnd.dcx");

        var diagnostic = await GetSingleDiagnostic(loadout);

        diagnostic.Id.Should().Be(new DiagnosticId("NexusMods.Games.FromSoft", 1));
        var data = diagnostic.Should().BeOfType<Diagnostic<FromSoftDiagnostics.PackageCollisionMessageData>>().Which.MessageData;
        data.CaseNote.Should().Contain("differ only in case");
        data.Path.Should().Contain("msg/engus/Item.msgbnd.dcx").And.Contain("msg/engus/item.msgbnd.dcx");
    }

    [Fact]
    public async Task ADisabledPackageDoesNotCount()
    {
        var loadout = await CreateLoadout();
        await InstallPackage(loadout, "Weapons", "parts/wp_a.dcx");
        var armor = await InstallPackage(loadout, "Armor", "parts/wp_a.dcx");
        (await GetAllDiagnostics(loadout)).Should().ContainSingle();

        await DisableItem(armor.Id);

        await ShouldHaveNoDiagnostics(loadout, because: "only one enabled package provides the path");
    }

    [Fact]
    public async Task ThreePackagesOnOnePathRaiseOneDiagnosticNamingAllThree()
    {
        var loadout = await CreateLoadout();
        await InstallPackage(loadout, "C", "parts/wp_a.dcx");
        await InstallPackage(loadout, "A", "parts/wp_a.dcx");
        await InstallPackage(loadout, "B", "parts/wp_a.dcx");

        var data = (await GetSingleDiagnostic(loadout)).Should().BeOfType<Diagnostic<FromSoftDiagnostics.PackageCollisionMessageData>>().Which.MessageData;

        data.Packages.Should().Be("A, B, C");
    }

    [Fact]
    public async Task NativesFromDifferentModsMayShareAFileName()
    {
        var loadout = await CreateLoadout();
        await Install<Me3NativeInstaller>(loadout, "One.zip", "hook.dll");
        await Install<Me3NativeInstaller>(loadout, "Two.zip", "hook.dll");

        await ShouldHaveNoDiagnostics(loadout);
    }

    [Fact]
    public async Task TwoConfigLayerFilesAtOneOverlayPathRaiseASeparateCritical()
    {
        var loadout = await CreateLoadout();
        await Install<SeamlessCoopInstaller>(loadout, "Seamless Co-op.zip", "SeamlessCoop/ersc.dll", "SeamlessCoop/ersc_settings.ini");
        await Install<SeamlessCoopInstaller>(loadout, "Seamless Co-op copy.zip", "SeamlessCoop/ersc.dll", "SeamlessCoop/ersc_settings.ini");

        var diagnostics = await GetAllDiagnostics(loadout);

        diagnostics.Should().HaveCount(2, "one per overlay path both archives provide");
        diagnostics.Should().OnlyContain(d => d.Severity == DiagnosticSeverity.Critical && d.Id == new DiagnosticId("NexusMods.Games.FromSoft", 2));
        var paths = diagnostics.Select(d => ((Diagnostic<FromSoftDiagnostics.OverlayCollisionMessageData>)d).MessageData.Path).ToArray();
        paths.Should().BeEquivalentTo("SeamlessCoop/ersc.dll", "SeamlessCoop/ersc_settings.ini");
    }

    [Fact]
    public async Task APackageAndANativeAndAnOverlayFileNeverCollideWithEachOther()
    {
        var loadout = await CreateLoadout();
        await InstallPackage(loadout, "Weapons", "parts/wp_a.dcx");
        await Install<Me3NativeInstaller>(loadout, "Hook.zip", "hook.dll");
        await Install<SeamlessCoopInstaller>(loadout, "Seamless Co-op.zip", "SeamlessCoop/ersc.dll", "SeamlessCoop/ersc_settings.ini");

        await ShouldHaveNoDiagnostics(loadout);
    }
}
