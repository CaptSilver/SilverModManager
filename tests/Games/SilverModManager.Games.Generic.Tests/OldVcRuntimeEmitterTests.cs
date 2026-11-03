using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.Generic.Diagnostics;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;
using Xunit;

namespace SilverModManager.Games.Generic.Tests;

/// <summary>
/// The warning for an old or missing Visual C++ runtime in the prefix when the loadout deploys code that imports it.
/// The prefix is a temporary folder and the PE reader is a fake.
/// </summary>
public class OldVcRuntimeEmitterTests(ITestOutputHelper helper)
    : ALoadoutDiagnosticEmitterTest<OldVcRuntimeEmitterTests, StubbedGame, OldVcRuntimeEmitter>(helper)
{
    private static readonly DiagnosticId OldRuntimeId = new("SilverModManager.Games.Generic", number: 9);

    private readonly FakeNativeImageReader _images = new();

    private FakePrefixResolver Resolver => ServiceProvider.GetRequiredService<FakePrefixResolver>();

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddGenericGameSupport()
            .AddSingleton<INativeImageReader>(_images)
            .AddGame<StubbedGame>()
            .AddSingleton<IGameLocator>(provider => new UniversalStubbedGameLocator<StubbedGame>(
                provider,
                provider.GetRequiredService<IFileSystem>(),
                provider.GetRequiredService<TemporaryFileManager>(),
                stores: [GameStore.Steam]
            )
            {
                LocatorIds = [LocatorId.From("3976631895")],
            });
    }

    private string Details(Diagnostic diagnostic) => diagnostic.FormatDetails(ServiceProvider.GetRequiredService<IDiagnosticWriter>());

    private AbsolutePath RuntimeInPrefix => Resolver.DefaultPrefix.Path / "drive_c/windows/system32/msvcp140.dll";

    /// <summary>A mod that deploys <paramref name="path"/> into the game folder, with the file on disk as a deploy leaves it.</summary>
    private async Task DeployMod(Loadout.ReadOnly loadout, string path)
    {
        using (var tx = Connection.BeginTransaction())
        {
            var group = AddEmptyGroup(tx, loadout, "VR mod " + path);
            AddFile(tx, loadout, group, new GamePath(LocationId.Game, path));
            await tx.Commit();
        }

        var file = GameInstallation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, path));
        file.Parent.CreateDirectory();
        await file.WriteAllTextAsync(path);
    }

    private async Task InstallRuntime(Version? version)
    {
        RuntimeInPrefix.Parent.CreateDirectory();
        await RuntimeInPrefix.WriteAllTextAsync("fake");
        if (version is not null) _images.Versions["msvcp140.dll"] = version;
    }

    [Fact]
    public async Task OldRuntimeIsReportedWithTheVersionAndTheProtontricksFix()
    {
        var loadout = await CreateLoadout();
        await DeployMod(loadout, "openvr_api.dll");
        _images.Imports["openvr_api.dll"] = ["KERNEL32.dll", "MSVCP140.dll"];
        await InstallRuntime(new Version(14, 0, 24215, 1));
        Resolver.Resolution = _ => PrefixResolution.Resolved(Resolver.DefaultPrefix with { ProtonAppId = 611670 });

        var diagnostic = await GetSingleDiagnostic(loadout);

        diagnostic.Id.Should().Be(OldRuntimeId);
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
        diagnostic.Actions.Should().Equal(DiagnosticActions.SetUpPrefix(["vcrun2022"]));
        Details(diagnostic).Should().Contain("version 14.0.24215.1").And.Contain("protontricks 611670 -q vcrun2022").And.Contain("openvr_api.dll");
    }

    [Fact]
    public async Task MissingRuntimeIsReported()
    {
        var loadout = await CreateLoadout();
        await DeployMod(loadout, "plugin.dll");
        _images.Imports["plugin.dll"] = ["VCRUNTIME140.dll"];

        var diagnostic = await GetSingleDiagnostic(loadout);

        Details(diagnostic).Should().Contain("missing");
    }

    [Fact]
    public async Task ARecentRuntimeIsNotReported()
    {
        var loadout = await CreateLoadout();
        await DeployMod(loadout, "openvr_api.dll");
        _images.Imports["openvr_api.dll"] = ["MSVCP140.dll"];
        await InstallRuntime(new Version(14, 40, 33816, 0));

        await ShouldHaveNoDiagnostics(loadout);
    }

    [Fact]
    public async Task TheMinimumVersionItselfIsNotOld()
    {
        var loadout = await CreateLoadout();
        await DeployMod(loadout, "openvr_api.dll");
        _images.Imports["openvr_api.dll"] = ["MSVCP140.dll"];
        await InstallRuntime(new Version(14, 40));

        await ShouldHaveNoDiagnostics(loadout);
    }

    [Fact]
    public async Task NothingIsReportedWhenNoDeployedFileImportsTheRuntime()
    {
        var loadout = await CreateLoadout();
        await DeployMod(loadout, "plain.dll");
        _images.Imports["plain.dll"] = ["KERNEL32.dll"];
        await InstallRuntime(new Version(14, 0, 24215, 1));

        await ShouldHaveNoDiagnostics(loadout);
    }

    [Fact]
    public async Task OnlyFilesTheLoadoutProvidesAreRead()
    {
        var loadout = await CreateLoadout();
        await DeployMod(loadout, "mod.dll");
        // On disk with no loadout row behind it, such as a game file: not the loadout's doing, so never opened.
        var stray = GameInstallation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, "stray.dll"));
        await stray.WriteAllTextAsync("stray");
        _images.Imports["stray.dll"] = ["MSVCP140.dll"];

        await ShouldHaveNoDiagnostics(loadout);
        _images.ImportReads.Should().Contain("mod.dll").And.NotContain("stray.dll");
    }

    [Fact]
    public async Task ARuntimeWithNoReadableVersionIsNotJudged()
    {
        var loadout = await CreateLoadout();
        await DeployMod(loadout, "openvr_api.dll");
        _images.Imports["openvr_api.dll"] = ["MSVCP140.dll"];
        await InstallRuntime(version: null);

        await ShouldHaveNoDiagnostics(loadout);
    }

    [Fact]
    public async Task NothingIsReportedWithoutAPrefix()
    {
        var loadout = await CreateLoadout();
        await DeployMod(loadout, "openvr_api.dll");
        _images.Imports["openvr_api.dll"] = ["MSVCP140.dll"];
        Resolver.Resolution = _ => PrefixResolution.Native("native build");

        await ShouldHaveNoDiagnostics(loadout);
    }

    [Fact]
    public async Task APrefixOutsideSteamGetsTheWinetricksFix()
    {
        var loadout = await CreateLoadout();
        await DeployMod(loadout, "plugin.dll");
        _images.Imports["plugin.dll"] = ["MSVCP140.dll"];
        Resolver.Resolution = _ => PrefixResolution.Resolved(Resolver.DefaultPrefix with { Kind = CompatibilityLayerKind.Heroic });

        var diagnostic = await GetSingleDiagnostic(loadout);

        Details(diagnostic).Should().Contain("winetricks").And.Contain("vcrun2022").And.NotContain("protontricks 611670");
    }
}
