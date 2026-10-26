using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Ini;
using SilverModManager.Games.CreationEngine.Tests.Morrowind.OpenMw;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Ini;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.Games.CreationEngine.Tests;

/// <summary>
/// A Fallout 4 loadout that keeps its saves apart points the game at <c>Saves\&lt;id&gt;\</c> through
/// <c>sLocalSavePath</c> in the loadout's own copy of the INI.
/// </summary>
public class PerLoadoutSavesTests(ITestOutputHelper helper) : AIsolatedGameTest<PerLoadoutSavesTests, CreationEngine.Fallout4.Fallout4>(helper)
{
    private static readonly GamePath MainIni = new(LocationId.Preferences, "Fallout4.ini");
    private static readonly byte[] UserIni = "[General]\r\nsLanguage=EN\r\nsLocalSavePath=Mine\\\r\n"u8.ToArray();

    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services)
        .AddCreationEngine()
        .AddUniversalGameLocator<CreationEngine.Fallout4.Fallout4>(new Version("1.10.163"), locationsFileSystem: IsolatedKnownFolders.Create);

    private IPerLoadoutSaves Saves => (IPerLoadoutSaves)Game;

    private AbsolutePath Live => GameInstallation.Locations.ToAbsolutePath(MainIni);

    private async Task KeepSaves(Loadout.ReadOnly loadout, bool keep)
    {
        using var tx = Connection.BeginTransaction();
        tx.Add(loadout.Id, Loadout.KeepSavesWithLoadout, keep);
        await tx.Commit();
    }

    private async Task<IniDocument> BakeWithUserIni(Loadout.ReadOnly loadout)
    {
        Live.Parent.CreateDirectory();
        await Live.WriteAllBytesAsync(UserIni);
        await Synchronizer.Synchronize(Refresh(loadout));
        return IniDocument.Parse(await Live.ReadAllBytesAsync());
    }

    [Fact]
    public async Task TheRedirectIsTheLoadoutsSaveFolderInTheGeneralSection()
    {
        var loadout = await CreateLoadout();

        Saves.PlanSaveRedirect(loadout).Should().Be(
            new SaveRedirectPlan.IniKey("General", "sLocalSavePath", $"Saves\\{LoadoutSaveSlot.IdOf(loadout)}\\"));
    }

    [Fact]
    public async Task RenamingTheLoadoutLeavesTheRedirectAlone()
    {
        var loadout = await CreateLoadout();
        var before = Saves.PlanSaveRedirect(loadout);

        var renamed = await LoadoutManager.RenameLoadout(loadout.LoadoutId, "another name");

        Saves.PlanSaveRedirect(renamed).Should().Be(before);
    }

    [Fact]
    public async Task TheBakeWritesTheKeyIntoTheLoadoutsIniButNotItsBaseline()
    {
        var loadout = await CreateLoadout();
        await KeepSaves(loadout, true);

        var baked = await BakeWithUserIni(loadout);

        baked.Get("General", "sLocalSavePath").Should().Be($"Saves\\{LoadoutSaveSlot.IdOf(loadout)}\\");
        var state = LoadoutIniState.FindByLoadout(Connection.Db, loadout.LoadoutId).Should().ContainSingle().Subject;
        state.Baseline.Should().Be(UserIni.xxHash3(), "the redirect is the app's key, not the user's");
    }

    [Fact]
    public async Task SwitchingItOffBringsTheBaselinesOwnValueBack()
    {
        var loadout = await CreateLoadout();
        await KeepSaves(loadout, true);
        await BakeWithUserIni(loadout);

        await KeepSaves(loadout, false);
        await Synchronizer.Synchronize(Refresh(loadout));

        IniDocument.Parse(await Live.ReadAllBytesAsync()).Get("General", "sLocalSavePath").Should().Be("Mine\\");
    }

    [Fact]
    public async Task NothingIsRedirectedWhileTheLoadoutDoesNotKeepItsSaves()
    {
        var loadout = await CreateLoadout();

        var baked = await BakeWithUserIni(loadout);

        baked.Get("General", "sLocalSavePath").Should().Be("Mine\\");
    }

    [Fact]
    public async Task AnAdoptionCopiesTheSaveFilesOfTheGameIntoTheLoadoutsFolder()
    {
        var loadout = await CreateLoadout();
        var root = TemporaryFileManager.CreateFolder().Path;
        var saves = root / "Saves";
        saves.CreateDirectory();
        foreach (var name in new[] { "Save1.fos", "Save1.f4se", "Save2.fos.bak", "notes.txt", "Quick.FOS" })
            await (saves / name).WriteAllTextAsync(name);
        var other = saves / "0123456789abcd";
        other.CreateDirectory();
        await (other / "Save9.fos").WriteAllTextAsync("elsewhere");
        var slot = saves / LoadoutSaveSlot.IdOf(loadout);

        var plan = Saves.PlanAdoptCurrentSave(loadout, root).Should().NotBeNull().And.Subject.As<SaveAdoptionPlan>();

        plan.Copies.Should().BeEquivalentTo(new[]
        {
            new SaveAdoptionPlan.Copy(saves / "Save1.fos", slot / "Save1.fos"),
            new SaveAdoptionPlan.Copy(saves / "Save1.f4se", slot / "Save1.f4se"),
            new SaveAdoptionPlan.Copy(saves / "Save2.fos.bak", slot / "Save2.fos.bak"),
            new SaveAdoptionPlan.Copy(saves / "Quick.FOS", slot / "Quick.FOS"),
        });
        slot.DirectoryExists().Should().BeFalse("planning copies nothing");
    }

    [Fact]
    public async Task AnAdoptionOfAFolderWithoutSavesIsNull()
    {
        var loadout = await CreateLoadout();
        var root = TemporaryFileManager.CreateFolder().Path;
        (root / "Saves").CreateDirectory();
        await (root / "Saves" / "readme.txt").WriteAllTextAsync("x");

        Saves.PlanAdoptCurrentSave(loadout, root).Should().BeNull();
        Saves.PlanAdoptCurrentSave(loadout, root / "missing").Should().BeNull();
    }

    [Fact]
    public async Task AnAdoptionLeavesASaveThatIsAlreadyInTheFolderAlone()
    {
        var loadout = await CreateLoadout();
        var root = TemporaryFileManager.CreateFolder().Path;
        var saves = root / "Saves";
        var slot = saves / LoadoutSaveSlot.IdOf(loadout);
        slot.CreateDirectory();
        await (saves / "A.fos").WriteAllTextAsync("a");
        await (slot / "A.fos").WriteAllTextAsync("already here");

        Saves.PlanAdoptCurrentSave(loadout, root).Should().BeNull();
    }
}

/// <summary>Oblivion only honours the save folder when its INI sends the game to My Games.</summary>
public class OblivionPerLoadoutSavesTests(ITestOutputHelper helper) : AIsolatedGameTest<OblivionPerLoadoutSavesTests, CreationEngine.Oblivion.Oblivion>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services)
        .AddCreationEngine()
        .AddUniversalGameLocator<CreationEngine.Oblivion.Oblivion>(new Version("1.2.0.416"), locationsFileSystem: IsolatedKnownFolders.Create);

    private async Task KeepSaves(Loadout.ReadOnly loadout, bool keep)
    {
        using var tx = Connection.BeginTransaction();
        tx.Add(loadout.Id, Loadout.KeepSavesWithLoadout, keep);
        await tx.Commit();
    }

    [Fact]
    public async Task TheAppAlsoAsksForMyGamesWhileTheLoadoutKeepsItsSaves()
    {
        var loadout = await CreateLoadout();
        await KeepSaves(loadout, true);

        var keys = ((IGameIniFiles)Game).ManagedKeys(Refresh(loadout));

        keys.Should().Contain(new IniKeyEdit("General", "bUseMyGamesDirectory", "1"));
        keys.Should().Contain(new IniKeyEdit("General", "sLocalSavePath", $"Saves\\{LoadoutSaveSlot.IdOf(loadout)}\\"));
    }

    [Fact]
    public async Task TheGameIsLeftInItsFolderWhenTheLoadoutDoesNotKeepItsSaves()
    {
        var loadout = await CreateLoadout();

        ((IGameIniFiles)Game).ManagedKeys(loadout).Select(k => k.Key).Should().NotContain("bUseMyGamesDirectory");
    }

    [Fact]
    public async Task ThePlanNamesTheSaveExtensionsOfTheTitle()
    {
        var loadout = await CreateLoadout();
        var root = TemporaryFileManager.CreateFolder().Path;
        (root / "Saves").CreateDirectory();
        foreach (var name in new[] { "a.ess", "a.obse", "a.fos" }) await (root / "Saves" / name).WriteAllTextAsync(name);

        var plan = ((IPerLoadoutSaves)Game).PlanAdoptCurrentSave(loadout, root)!;

        plan.Copies.Select(c => c.Source.FileName.ToString()).Should().BeEquivalentTo("a.ess", "a.obse");
    }
}

/// <summary>Morrowind and OpenMW have no setting that moves the saves.</summary>
public class MorrowindPerLoadoutSavesTests(ITestOutputHelper helper) : AIsolatedGameTest<MorrowindPerLoadoutSavesTests, CreationEngine.Morrowind.Morrowind>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services)
        .AddCreationEngine()
        .AddTempOpenMwConfigRoots()
        .AddUniversalGameLocator<CreationEngine.Morrowind.Morrowind>(new Version("1.6.0.1820"), locationsFileSystem: IsolatedKnownFolders.Create);

    [Fact]
    public async Task TheRedirectIsUnsupportedWithAReason()
    {
        var loadout = await CreateLoadout();

        ((IPerLoadoutSaves)Game).PlanSaveRedirect(loadout).Should().Be(new SaveRedirectPlan.Unsupported("Morrowind has no save-path setting"));
    }

    [Fact]
    public async Task NoKeyIsAskedForEvenWhenTheLoadoutKeepsItsSaves()
    {
        var loadout = await CreateLoadout();
        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(loadout.Id, Loadout.KeepSavesWithLoadout, true);
            await tx.Commit();
        }

        ((IGameIniFiles)Game).ManagedKeys(Refresh(loadout)).Select(k => k.Key).Should().NotContain("sLocalSavePath");
    }

    [Fact]
    public async Task NothingIsAdopted()
    {
        var loadout = await CreateLoadout();
        var root = TemporaryFileManager.CreateFolder().Path;
        (root / "Saves").CreateDirectory();
        await (root / "Saves" / "a.ess").WriteAllTextAsync("a");

        ((IPerLoadoutSaves)Game).PlanAdoptCurrentSave(loadout, root).Should().BeNull();
    }
}
