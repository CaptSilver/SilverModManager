using System.Runtime.InteropServices;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.Generic.Installers;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.App.UI.DiagnosticSystem;
using SilverModManager.Games.Larian.BaldursGate3;
using SilverModManager.Games.Larian.BaldursGate3.Emitters;
using SilverModManager.Games.Larian.BaldursGate3.Installers;
using SilverModManager.Games.TestFramework;
using SilverModManager.StandardGameLocators.TestHelpers;
using Xunit;

namespace SilverModManager.Games.Larian.Tests.BaldursGate3;

public class BG3WineDllOverrideTests(ITestOutputHelper outputHelper) : ALibraryArchiveInstallerTests<BG3WineDllOverrideTests, Larian.BaldursGate3.BaldursGate3>(outputHelper)
{
    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddBaldursGate3()
            .AddUniversalGameLocator<Larian.BaldursGate3.BaldursGate3>(new Version("1.6.1"), locationsFileSystem: IsolatedKnownFolders.Create, platform: OSPlatform.Windows);
    }

    [Fact]
    public async Task BaldursGate3RequiresDWriteOnlyWhenTheScriptExtenderIsInTheLoadout()
    {
        var requirements = Game.Should().BeAssignableTo<IWineDllOverrideRequirements>().Subject;
        var loadout = await CreateLoadout();

        requirements.RequiredWineDllOverrides(loadout).Should().BeEmpty();

        var archive = await AddFromPaths("DWrite.dll", "ScriptExtenderSettings.json");
        await Install(typeof(BG3SEInstaller), loadout, archive);
        Refresh(ref loadout);

        requirements.RequiredWineDllOverrides(loadout).Select(o => o.ToString()).Should().Equal("DWrite=n,b");
    }
}

/// <summary>
/// The native Linux build has no Wine to override DLLs for, and the script extender cannot load into it at all.
/// </summary>
public class BG3NativeScriptExtenderTests(ITestOutputHelper outputHelper) : ALibraryArchiveInstallerTests<BG3NativeScriptExtenderTests, Larian.BaldursGate3.BaldursGate3>(outputHelper)
{
    private static readonly DiagnosticId ScriptExtenderOnNativeId = new("SilverModManager.Games.Larian.BaldursGate3", number: 14);

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddBaldursGate3()
            .AddDiagnosticWriter()
            .AddUniversalGameLocator<Larian.BaldursGate3.BaldursGate3>(new Version("1.6.1"), locationsFileSystem: IsolatedKnownFolders.Create, platform: OSPlatform.Linux);
    }

    [Fact]
    public async Task TheNativeBuildNeedsNoWineDllOverride()
    {
        var loadout = await CreateLoadout();
        var archive = await AddFromPaths("bin/DWrite.dll");
        await Install(typeof(GenericPatternMatchInstaller), loadout, archive);
        Refresh(ref loadout);
        Game.Should().BeAssignableTo<IWineDllOverrideRequirements>().Subject.RequiredWineDllOverrides(loadout).Should().BeEmpty();
    }

    [Fact]
    public async Task TheScriptExtenderInstallerDeclinesTheNativeBuild()
    {
        var loadout = await CreateLoadout();
        var archive = await AddFromPaths("DWrite.dll", "ScriptExtenderSettings.json");
        var installer = Game.LibraryItemInstallers.OfType<BG3SEInstaller>().Single();

        using var tx = Connection.BeginTransaction();
        var group = new LoadoutItemGroup.New(tx, out var groupId)
        {
            IsGroup = true,
            LoadoutItem = new LoadoutItem.New(tx, groupId) { Name = "BG3SE", LoadoutId = loadout },
        };
        var result = await installer.ExecuteAsync(archive.AsLibraryFile().AsLibraryItem(), group, tx, loadout, CancellationToken.None);

        result.IsNotSupported(out var reason).Should().BeTrue();
        reason.Should().Contain("native Linux build");
    }

    [Fact]
    public async Task ADllLeftInBinByAnotherInstallerIsReported()
    {
        var loadout = await CreateLoadout();
        var archive = await AddFromPaths("bin/DWrite.dll");
        await Install(typeof(GenericPatternMatchInstaller), loadout, archive);
        Refresh(ref loadout);

        var emitter = Game.DiagnosticEmitters.OfType<DependencyDiagnosticEmitter>().Single();
        var diagnostics = await emitter.Diagnose(loadout, CancellationToken.None).ToListAsync();

        diagnostics.Where(d => d.Id == ScriptExtenderOnNativeId).Should().ContainSingle()
            .Which.FormatSummary(ServiceProvider.GetRequiredService<IDiagnosticWriter>()).Should().Contain("Script Extender");
    }

    [Fact]
    public async Task ANativeLoadoutWithoutTheScriptExtenderReportsNothingAboutIt()
    {
        var loadout = await CreateLoadout();

        var emitter = Game.DiagnosticEmitters.OfType<DependencyDiagnosticEmitter>().Single();
        var diagnostics = await emitter.Diagnose(loadout, CancellationToken.None).ToListAsync();

        diagnostics.Where(d => d.Id == ScriptExtenderOnNativeId).Should().BeEmpty();
    }
}
