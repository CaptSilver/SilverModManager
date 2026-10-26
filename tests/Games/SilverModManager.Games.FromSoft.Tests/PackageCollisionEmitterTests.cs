using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Games.FromSoft.Diagnostics;
using SilverModManager.Games.FromSoft.Doctor;
using SilverModManager.Games.FromSoft.EldenRing;
using SilverModManager.Games.FromSoft.Installers;
using SilverModManager.Games.FromSoft.Merge;
using SilverModManager.Games.FromSoft.Tests.Fixtures;
using SilverModManager.Games.TestFramework;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.Games.FromSoft.Tests;

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
    public async Task TwoPackagesProvidingOnePathRaiseOneWarningNamingTheWinnerAndThePath()
    {
        var loadout = await CreateLoadout();
        await InstallPackage(loadout, "Weapons", "parts/wp_a.dcx", "menu/only_here.dds");
        await InstallPackage(loadout, "Armor", "parts/wp_a.dcx");

        var diagnostic = await GetSingleDiagnostic(loadout);

        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
        var data = diagnostic.Should().BeOfType<Diagnostic<FromSoftDiagnostics.PackageWinnerByPriorityMessageData>>().Which.MessageData;
        data.Path.Should().Be("parts/wp_a.dcx");
        new[] { data.Winner, data.Ignored }.Order().Should().Equal("Armor.zip", "Weapons.zip");
    }

    [Fact]
    public async Task TwoPathsThatDifferOnlyInCaseRaiseTheSameCriticalWithASentenceAboutIt()
    {
        var loadout = await CreateLoadout();
        await InstallPackage(loadout, "Weapons", "parts/Wp.dcx");
        await InstallPackage(loadout, "Armor", "parts/wp.dcx");

        var diagnostic = await GetSingleDiagnostic(loadout);

        diagnostic.Id.Should().Be(new DiagnosticId("SilverModManager.Games.FromSoft", 1));
        var data = diagnostic.Should().BeOfType<Diagnostic<FromSoftDiagnostics.PackageCollisionMessageData>>().Which.MessageData;
        data.CaseNote.Should().Contain("differ only in case");
        data.Path.Should().Contain("parts/Wp.dcx").And.Contain("parts/wp.dcx");
    }

    private static FromSoftDiagnostics.PackageCollisionMessageData CollisionOf(Diagnostic diagnostic)
        => diagnostic.Should().BeOfType<Diagnostic<FromSoftDiagnostics.PackageCollisionMessageData>>().Which.MessageData;

    [Fact]
    public async Task AMsgbndPairTheEngineMergesIsNotACollision()
    {
        var loadout = await CreateLoadout();
        await InstallPackage(loadout, "Weapons", "msg/engus/item.msgbnd.dcx");
        await InstallPackage(loadout, "Armor", "msg/engus/item.msgbnd.dcx");

        await ShouldHaveNoDiagnostics(loadout, because: "the apply merges these, so the game is not left to pick one");
    }

    [Fact]
    public async Task ATalkesdbndPairWithNoVanillaDumpIsCriticalAndNamesWhatIsNeeded()
    {
        var loadout = await CreateLoadout();
        await InstallPackage(loadout, "Weapons", "script/talk/m00.talkesdbnd.dcx");
        await InstallPackage(loadout, "Armor", "script/talk/m00.talkesdbnd.dcx");

        var diagnostic = await GetSingleDiagnostic(loadout);

        diagnostic.Severity.Should().Be(DiagnosticSeverity.Critical);
        CollisionOf(diagnostic).Reason.Should().Be(MergeOutcome.NoVanillaSource);
    }

    [Fact]
    public async Task AnUnknownDcxPairIsAWarningNamingTheModLoadoutPriorityPicks()
    {
        var loadout = await CreateLoadout();
        var weapons = await InstallPackage(loadout, "Weapons", "parts/wp_a.dcx");
        var armor = await InstallPackage(loadout, "Armor", "parts/wp_a.dcx");
        var winner = PriorityOf(weapons) > PriorityOf(armor) ? "Weapons.zip" : "Armor.zip";
        var loser = winner == "Weapons.zip" ? "Armor.zip" : "Weapons.zip";

        var diagnostic = await GetSingleDiagnostic(loadout);

        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
        diagnostic.Id.Should().Be(new DiagnosticId("SilverModManager.Games.FromSoft", 16));
        var data = diagnostic.Should().BeOfType<Diagnostic<FromSoftDiagnostics.PackageWinnerByPriorityMessageData>>().Which.MessageData;
        data.Path.Should().Be("parts/wp_a.dcx");
        data.Winner.Should().Be(winner);
        data.Ignored.Should().Be(loser);
    }

    [Fact]
    public async Task MovingTheWinningModDownHandsAnUnknownDcxToTheOther()
    {
        var loadout = await CreateLoadout();
        var weapons = await InstallPackage(loadout, "Weapons", "parts/wp_a.dcx");
        var armor = await InstallPackage(loadout, "Armor", "parts/wp_a.dcx");
        var (high, low) = PriorityOf(weapons) > PriorityOf(armor) ? (weapons, armor) : (armor, weapons);
        var highRow = LoadoutItemGroupPriority.FindByTarget(Connection.Db, high.LoadoutItemGroupId).Single();
        var lowRow = LoadoutItemGroupPriority.FindByTarget(Connection.Db, low.LoadoutItemGroupId).Single();

        await LoadoutManager.ResolveFileConflicts([lowRow.LoadoutItemGroupPriorityId], highRow.LoadoutItemGroupPriorityId);

        var data = (await GetSingleDiagnostic(loadout)).Should().BeOfType<Diagnostic<FromSoftDiagnostics.PackageWinnerByPriorityMessageData>>().Which.MessageData;
        data.Winner.Should().Be(low.AsLoadoutItem().Name);
    }

    private ConflictPriority PriorityOf(LoadoutItemGroup.ReadOnly group)
        => LoadoutItemGroupPriority.FindByTarget(Connection.Db, group.LoadoutItemGroupId).Single().Priority;

    [Fact]
    public async Task AnUnknownDcxPairInThreePackagesNamesEveryIgnoredMod()
    {
        var loadout = await CreateLoadout();
        await InstallPackage(loadout, "A", "parts/wp_a.dcx");
        await InstallPackage(loadout, "B", "parts/wp_a.dcx");
        await InstallPackage(loadout, "C", "parts/wp_a.dcx");

        var data = (await GetSingleDiagnostic(loadout)).Should().BeOfType<Diagnostic<FromSoftDiagnostics.PackageWinnerByPriorityMessageData>>().Which.MessageData;

        data.Ignored.Split(", ").Should().HaveCount(2).And.NotContain(data.Winner);
    }

    [Fact]
    public async Task ACaseOnlyPairKeepsTheCaseNoteAsItsReason()
    {
        var loadout = await CreateLoadout();
        await InstallPackage(loadout, "Weapons", "parts/Wp.dcx");
        await InstallPackage(loadout, "Armor", "parts/wp.dcx");

        var data = CollisionOf(await GetSingleDiagnostic(loadout));

        data.Reason.Should().Be(MergeOutcome.CaseOnlyCollision);
    }

    [Fact]
    public async Task AMergeableCaseVariantPairIsNotACollision()
    {
        var loadout = await CreateLoadout();
        await InstallPackage(loadout, "Weapons", "msg/engUS/item.msgbnd.dcx");
        await InstallPackage(loadout, "Armor", "msg/engus/item.msgbnd.dcx");

        await ShouldHaveNoDiagnostics(loadout, because: "the engine merges the two spellings into one file");
    }

    [Fact]
    public async Task OnePackageUnderTwoSpellingsOfOnePathIsStillReported()
    {
        var loadout = await CreateLoadout();
        // One archive can't hold both spellings (extraction folds them), so the same package folder is installed twice.
        await InstallPackage(loadout, "Weapons", "msg/engus/Item.msgbnd.dcx");
        await InstallPackage(loadout, "Weapons", "msg/engus/item.msgbnd.dcx");
        ServiceProvider.GetRequiredService<LoadoutMergeSources>().Providers(Loadout.Load(Connection.Db, loadout))
            .Select(p => (p.PackageId, p.RelPath)).Should().BeEquivalentTo([("Weapons", "msg/engus/Item.msgbnd.dcx"), ("Weapons", "msg/engus/item.msgbnd.dcx")]);

        // The helper builds a sync tree, which refuses two items whose paths differ only in case; the emitter doesn't need one.
        var diagnostics = new List<Diagnostic>();
        await foreach (var d in Emitter.Diagnose(Loadout.Load(Connection.Db, loadout), CancellationToken.None)) diagnostics.Add(d);
        var data = CollisionOf(diagnostics.Should().ContainSingle().Which);

        data.Packages.Should().Be("Weapons");
        data.Reason.Should().Be(MergeOutcome.CaseOnlyCollision);
    }

    [Fact]
    public async Task AnOverlapTheEngineCannotMergeStillAppliesAndDeploysBothCopies()
    {
        var loadout = await CreateLoadout();
        await InstallPackage(loadout, "Weapons", "parts/wp_a.dcx");
        await InstallPackage(loadout, "Armor", "parts/wp_a.dcx");
        (await GetAllDiagnostics(loadout)).Should().ContainSingle();

        await Synchronizer.Synchronize(Loadout.Load(Connection.Db, loadout));

        var mods = GameInstallation.Locations[LocationId.Me3Mods].Path;
        (mods / "Weapons/parts/wp_a.dcx").FileExists.Should().BeTrue();
        (mods / "Armor/parts/wp_a.dcx").FileExists.Should().BeTrue();
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
        // A talk archive stays Critical (it needs a vanilla copy), so the Critical text still has to name every package.
        await InstallPackage(loadout, "C", "script/talk/m00.talkesdbnd.dcx");
        await InstallPackage(loadout, "A", "script/talk/m00.talkesdbnd.dcx");
        await InstallPackage(loadout, "B", "script/talk/m00.talkesdbnd.dcx");

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
        diagnostics.Should().OnlyContain(d => d.Severity == DiagnosticSeverity.Critical && d.Id == new DiagnosticId("SilverModManager.Games.FromSoft", 2));
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
