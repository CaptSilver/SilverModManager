using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Ini;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Ini;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.Games.CreationEngine.Tests;

/// <summary>
/// Fallout 3 and New Vegas read <c>sLocalSavePath</c> from [General] of Fallout.ini, the key Mod Organizer 2's
/// profile-local saves set for both. They save as .fos with the script extender's co-save beside it.
/// </summary>
public abstract class AFalloutPerLoadoutSavesTests<TTest, TGame>(ITestOutputHelper helper, string coSaveExtension)
    : AIsolatedGameTest<TTest, TGame>(helper)
    where TTest : AFalloutPerLoadoutSavesTests<TTest, TGame>
    where TGame : IGame, IGameData<TGame>
{
    private static readonly GamePath MainIni = new(LocationId.Preferences, "Fallout.ini");
    private static readonly byte[] UserIni = "[General]\r\nsLanguage=ENGLISH\r\nsLocalSavePath=Mine\\\r\n"u8.ToArray();

    protected abstract Version GameVersion { get; }

    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services)
        .AddCreationEngine()
        .AddUniversalGameLocator<TGame>(GameVersion, locationsFileSystem: IsolatedKnownFolders.Create);

    private IPerLoadoutSaves Saves => (IPerLoadoutSaves)Game;

    private AbsolutePath Live => GameInstallation.Locations.ToAbsolutePath(MainIni);

    private async Task KeepSaves(Loadout.ReadOnly loadout, bool keep)
    {
        using var tx = Connection.BeginTransaction();
        tx.Add(loadout.Id, Loadout.KeepSavesWithLoadout, keep);
        await tx.Commit();
    }

    [Fact]
    public async Task TheRedirectIsTheLoadoutsSaveFolderInTheGeneralSection()
    {
        var loadout = await CreateLoadout();

        Saves.PlanSaveRedirect(loadout).Should().Be(
            new SaveRedirectPlan.IniKey("General", "sLocalSavePath", $"Saves\\{LoadoutSaveSlot.IdOf(loadout)}\\"));
    }

    [Fact]
    public async Task TheBakeWritesTheKeyIntoFalloutIniAndSwitchingOffBringsTheUsersValueBack()
    {
        var loadout = await CreateLoadout();
        await KeepSaves(loadout, true);
        Live.Parent.CreateDirectory();
        await Live.WriteAllBytesAsync(UserIni);

        await Synchronizer.Synchronize(Refresh(loadout));
        IniDocument.Parse(await Live.ReadAllBytesAsync()).Get("General", "sLocalSavePath")
            .Should().Be($"Saves\\{LoadoutSaveSlot.IdOf(loadout)}\\");

        await KeepSaves(loadout, false);
        await Synchronizer.Synchronize(Refresh(loadout));
        IniDocument.Parse(await Live.ReadAllBytesAsync()).Get("General", "sLocalSavePath").Should().Be("Mine\\");
    }

    [Fact]
    public async Task AnAdoptionCopiesTheSavesAndTheirCoSavesOfThisGameOnly()
    {
        var loadout = await CreateLoadout();
        var root = TemporaryFileManager.CreateFolder().Path;
        var saves = root / "Saves";
        saves.CreateDirectory();
        var coSave = $"Save1{coSaveExtension}";
        foreach (var name in new[] { "Save1.fos", coSave, "Save2.fos.bak", "Save3.f4se", "Save3.ess", "notes.txt" })
            await (saves / name).WriteAllTextAsync(name);
        var slot = saves / LoadoutSaveSlot.IdOf(loadout);

        var plan = Saves.PlanAdoptCurrentSave(loadout, root).Should().NotBeNull().And.Subject.As<SaveAdoptionPlan>();

        plan.Copies.Should().BeEquivalentTo(new[]
        {
            new SaveAdoptionPlan.Copy(saves / "Save1.fos", slot / "Save1.fos"),
            new SaveAdoptionPlan.Copy(saves / coSave, slot / coSave),
            new SaveAdoptionPlan.Copy(saves / "Save2.fos.bak", slot / "Save2.fos.bak"),
        });
    }
}

public class Fallout3PerLoadoutSavesTests(ITestOutputHelper helper)
    : AFalloutPerLoadoutSavesTests<Fallout3PerLoadoutSavesTests, CreationEngine.Fallout3.Fallout3>(helper, ".fose")
{
    protected override Version GameVersion => new("1.7.0.3");
}

public class FalloutNewVegasPerLoadoutSavesTests(ITestOutputHelper helper)
    : AFalloutPerLoadoutSavesTests<FalloutNewVegasPerLoadoutSavesTests, CreationEngine.FalloutNewVegas.FalloutNewVegas>(helper, ".nvse")
{
    protected override Version GameVersion => new("1.4.0.525");
}
