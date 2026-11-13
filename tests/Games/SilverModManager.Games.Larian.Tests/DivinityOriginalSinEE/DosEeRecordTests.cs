using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Games;
using SilverModManager.App.UI.DiagnosticSystem;
using SilverModManager.Games.Larian.Engine;
using SilverModManager.Games.Larian.Engine.Emitters;
using SilverModManager.Games.Larian.Engine.SortOrder;
using SilverModManager.Sdk.Games;
using SilverModManager.StandardGameLocators.TestHelpers;
using System.Runtime.InteropServices;

namespace SilverModManager.Games.Larian.Tests.DivinityOriginalSinEE;

/// <summary>
/// What Divinity: Original Sin Enhanced Edition is, as a record on the shared Larian base: pak deployment to its
/// Mods folder, no mod list the app writes, and a located Windows build that Proton has to have prepared.
/// </summary>
public class DosEeRecordTests(ITestOutputHelper helper) : DosEeFixture(helper)
{
    private static readonly DiagnosticId EnableModsInGameId = new("SilverModManager.Games.Larian.BaldursGate3", number: 16);
    private static readonly DiagnosticId NativeBuildId = new("SilverModManager.Games.Larian.BaldursGate3", number: 17);

    [Fact]
    public void IdsAndStoresAreTheOnesTheGameShipsUnder()
    {
        Larian.DivinityOriginalSinEE.DivinityOriginalSinEE.GameId.Should().Be(GameId.From("Larian.DivinityOriginalSinEE"));
        Larian.DivinityOriginalSinEE.DivinityOriginalSinEE.DisplayName.Should().Be("Divinity: Original Sin Enhanced Edition");
        Larian.DivinityOriginalSinEE.DivinityOriginalSinEE.NexusModsGameId.Value.Value.Should().Be(1995u);
        Game.StoreIdentifiers.SteamAppIds.Should().Equal(373420u);
        Game.StoreIdentifiers.GOGProductIds.Should().Equal(1445516929L, 1445524575L);
        Game.HasNativeLinuxBuild.Should().BeFalse("a Linux depot would locate on the host file system and resolve Documents to the real one");

        var profile = Game.Profile;
        profile.GameData.Should().Be(RelativePath.FromUnsanitizedInput("Data"));
        profile.Executables.Select(e => e.ToString()).Should().Equal("Shipping/EoCApp.exe");
        profile.PakVersions.Should().Equal(9, 7);
        profile.Schema.Should().BeNull();
        profile.GameModuleUuids.Should().BeEmpty();
        profile.ExtraModsSourceFolderNames.Should().Equal("DOCS_MOD");
        profile.VarietyId.Value.Should().Be(new Guid("E079B484-35E8-463C-8B25-99719C0F999B"));
        profile.NativeLinuxDepotUnsupported.Should().BeTrue();
        Game.Profile.Profiles.Should().Be(new LarianProfileLayout.PerProfileFolder(RelativePath.FromUnsanitizedInput("PlayerProfiles")));
    }

    [Fact]
    public async Task NoVarietyNoModsettingsNoModListEmitters()
    {
        Game.SortOrderManager.GetSortOrderVarieties().Should().BeEmpty();
        ((SilverModManager.Abstractions.Loadouts.Synchronizers.ALoadoutSynchronizer)Synchronizer).IntrinsicFiles(Refresh(await CreateLoadout())).Should().BeEmpty();
        Game.DiagnosticEmitters.OfType<DuplicateModuleEmitter>().Should().BeEmpty();
        Game.DiagnosticEmitters.OfType<LarianModListReplacedEmitter>().Should().BeEmpty();
        Game.DiagnosticEmitters.OfType<LarianModListResetEmitter>().Should().BeEmpty();
        Game.DiagnosticEmitters.OfType<LarianProfileEmitter>().Should().BeEmpty();
        Game.DiagnosticEmitters.OfType<LarianInGameActivationEmitter>().Should().ContainSingle();
        Game.DiagnosticEmitters.OfType<LarianNativeBuildEmitter>().Should().ContainSingle();
    }

    [Fact]
    public async Task AFullDiagnosticPassAndAnApplyRunOnAnEeLoadout()
    {
        var loadout = await CreateLoadout();
        await InstallPak(loadout, "x.pak", V9Pak("Alpha", "Alpha", AlphaUuid));

        loadout = await Apply(loadout);

        OnDisk(Pak("x.pak")).FileExists.Should().BeTrue();
        var syncTree = await Synchronizer.BuildSyncTree(loadout);
        var found = new List<Diagnostic>();
        foreach (var emitter in Game.DiagnosticEmitters.OfType<SilverModManager.Abstractions.Diagnostics.Emitters.ILoadoutDiagnosticEmitter>())
        {
            await foreach (var diagnostic in emitter.Diagnose(loadout, System.Collections.Frozen.FrozenDictionary.ToFrozenDictionary(syncTree), CancellationToken.None))
                found.Add(diagnostic);
        }

        found.Select(d => d.Id).Should().Contain(EnableModsInGameId);
        var summary = found.Single(d => d.Id == EnableModsInGameId).FormatSummary(ServiceProvider.GetRequiredService<IDiagnosticWriter>());
        summary.Should().Be("Switch mods on in Divinity: Original Sin Enhanced Edition's Mods menu");
    }

    [Fact]
    public async Task NoPakNoInGameActivationSuggestion()
    {
        var loadout = await CreateLoadout();

        (await Diagnose<LarianInGameActivationEmitter>(loadout)).Should().BeEmpty();
    }

    [Fact]
    public async Task TheNativeBuildIsReportedAsUnsupported()
    {
        var loadout = await CreateLoadout();
        UnderUnpreparedProton();

        var found = (await Diagnose<LarianNativeBuildEmitter>(Refresh(loadout))).Should().ContainSingle().Subject;

        found.Id.Should().Be(NativeBuildId);
        found.Severity.Should().Be(DiagnosticSeverity.Warning);
        found.FormatSummary(ServiceProvider.GetRequiredService<IDiagnosticWriter>())
            .Should().Be("The native Linux build of Divinity: Original Sin Enhanced Edition is not supported yet. Set the game to use Proton in Steam and let it install the Windows files.");
    }

    [Fact]
    public async Task NoNativeBuildReportOnceProtonHasMadeThePrefix()
    {
        var loadout = await CreateLoadout();
        var prefix = UnderUnpreparedProton();
        prefix.CreateDirectory();
        await prefix.Combine("system.reg").WriteAllTextAsync("WINE REGISTRY");

        (await Diagnose<LarianNativeBuildEmitter>(Refresh(loadout))).Should().BeEmpty();
    }

    // The files stay on the host's file system; only the located game becomes a Windows build with an empty prefix.
    private AbsolutePath UnderUnpreparedProton()
    {
        var locator = ServiceProvider.GetServices<IGameLocator>().OfType<UniversalStubbedGameLocator<Larian.DivinityOriginalSinEE.DivinityOriginalSinEE>>().Single();
        locator.Platform = OSPlatform.Windows;
        locator.WinePrefixPath = TemporaryFileManager.CreateFolder().Path.Combine("pfx");
        GameRegistry.ClearCache();
        GameInstallation = GameRegistry.LocateGameInstallations().Single(i => i.Game is Larian.DivinityOriginalSinEE.DivinityOriginalSinEE);
        return locator.WinePrefixPath.Value;
    }

    private async Task<List<Diagnostic>> Diagnose<TEmitter>(SilverModManager.Sdk.Loadouts.Loadout.ReadOnly loadout)
        where TEmitter : SilverModManager.Abstractions.Diagnostics.Emitters.ILoadoutDiagnosticEmitter =>
        await Game.DiagnosticEmitters.OfType<TEmitter>().Single().Diagnose(loadout, CancellationToken.None).ToListAsync();
}
