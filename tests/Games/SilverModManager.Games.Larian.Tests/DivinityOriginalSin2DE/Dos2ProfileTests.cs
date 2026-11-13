using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI.DiagnosticSystem;
using SilverModManager.Games.Larian.DivinityOriginalSin2DE;
using SilverModManager.Games.Larian.Engine;
using SilverModManager.Games.Larian.Engine.Emitters;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Games.Larian.Tests.DivinityOriginalSin2DE;

/// <summary>
/// Divinity: Original Sin 2 keeps one <c>modsettings.lsx</c> per player profile. The app writes the one profile
/// it is pointed at, refuses when that is unclear, and never invents a list the game has not written.
/// </summary>
public class Dos2ProfileTests(ITestOutputHelper helper) : Dos2Fixture(helper)
{
    private static readonly DiagnosticId ProfileUnsetId = new("SilverModManager.Games.Larian.BaldursGate3", number: 15);

    private const string Unresolved = "Divinity: Original Sin 2 has more than one player profile. Pick one under Settings, then apply again.";
    private const string NoList = "Divinity: Original Sin 2 has not written its mod list yet. Start the game once, then apply again.";

    private async Task<List<Diagnostic>> DiagnoseProfile(Loadout.ReadOnly loadout) =>
        await Game.DiagnosticEmitters.OfType<LarianProfileEmitter>().Single().Diagnose(Refresh(loadout), CancellationToken.None).ToListAsync();

    // Creating a loadout syncs it once, which needs a mod list to write into, so the first profile exists before it.
    private async Task<Loadout.ReadOnly> LoadoutWithAlice()
    {
        await WriteProfile("Alice", GameEntries);
        return await CreateLoadout();
    }

    [Fact]
    public async Task OneProfileFolderIsUsedWithoutASetting()
    {
        var loadout = await LoadoutWithAlice();
        await InstallMod(loadout, "Alpha mod", "alpha.pak", AlphaPak());

        await Apply(loadout);

        DiskNames("Alice").Should().Equal("Shared", "Shared_DOS", "DivinityOrigins", "8 Action Points", "Alpha");
    }

    [Fact]
    public async Task SeveralProfilesWithTheSettingUseThatOne()
    {
        var loadout = await LoadoutWithAlice();
        await InstallMod(loadout, "Alpha mod", "alpha.pak", AlphaPak());
        await WriteProfile("Bob", GameEntries);
        PickProfile("Bob");

        await Apply(loadout);

        DiskNames("Bob").Should().Contain("Alpha");
        DiskNames("Alice").Should().NotContain("Alpha");
    }

    [Fact]
    public async Task SeveralProfilesWithoutTheSettingWarnAndRefuse()
    {
        var loadout = await LoadoutWithAlice();
        await InstallMod(loadout, "Alpha mod", "alpha.pak", AlphaPak());
        await WriteProfile("Bob", GameEntries);

        var diagnostic = (await DiagnoseProfile(loadout)).Should().ContainSingle().Subject;
        diagnostic.Id.Should().Be(ProfileUnsetId);
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
        diagnostic.FormatSummary(ServiceProvider.GetRequiredService<IDiagnosticWriter>())
            .Should().Be("Divinity: Original Sin 2 has more than one player profile; pick one under Settings");

        var apply = async () => await Apply(loadout);
        (await apply.Should().ThrowAsync<LarianProfileUnresolvedException>()).Which.Message.Should().Be(Unresolved);
    }

    [Fact]
    public async Task TheWarningClearsOnceAProfileIsPicked()
    {
        var loadout = await LoadoutWithAlice();
        await WriteProfile("Bob", GameEntries);
        PickProfile("Alice");

        (await DiagnoseProfile(loadout)).Should().BeEmpty();
    }

    [Fact]
    public async Task ASettingThatNamesNoFolderIsNotAProfile()
    {
        var loadout = await LoadoutWithAlice();
        await WriteProfile("Bob", GameEntries);
        PickProfile("Carol");

        (await DiagnoseProfile(loadout)).Should().ContainSingle();
        var apply = async () => await Apply(loadout);
        await apply.Should().ThrowAsync<LarianProfileUnresolvedException>();
    }

    [Fact]
    public async Task OneProfileNeedsNoWarning()
    {
        var loadout = await LoadoutWithAlice();

        (await DiagnoseProfile(loadout)).Should().BeEmpty();
    }

    [Fact]
    public async Task TwoFoldersWhereOnlyOneHasAListStillCountAsTwo()
    {
        var loadout = await LoadoutWithAlice();
        await InstallMod(loadout, "Alpha mod", "alpha.pak", AlphaPak());
        WriteEmptyProfile("Bob");

        (await DiagnoseProfile(loadout)).Should().ContainSingle();
        var apply = async () => await Apply(loadout);
        await apply.Should().ThrowAsync<LarianProfileUnresolvedException>();

        OnDisk(ModsettingsOf("Bob")).FileExists.Should().BeFalse();
    }

    [Fact]
    public async Task NoProfileFolderRefusesUntilTheGameHasRun()
    {
        var loadout = await LoadoutWithAlice();
        await InstallMod(loadout, "Alpha mod", "alpha.pak", AlphaPak());
        OnDisk(new GamePath(LarianConstants.PlayerProfilesLocationId, "Alice")).DeleteDirectory(recursive: true);

        var apply = async () => await Apply(loadout);

        (await apply.Should().ThrowAsync<LarianModListMissingException>()).Which.Message.Should().Be(NoList);
    }

    // Managing the game syncs it once; a refusal there must not leave a loadout behind that was never synced.
    [Fact]
    public async Task ManagingTheGameBeforeItHasRunRefusesAndAddsNoLoadout()
    {
        var manage = async () => await CreateLoadout();

        (await manage.Should().ThrowAsync<LarianModListMissingException>()).Which.Message.Should().Be(NoList);
        Loadout.All(Connection.Db).Should().BeEmpty();
    }

    [Fact]
    public async Task ManagingTheGameWithAnUnpickedProfileRefusesAndAddsNoLoadout()
    {
        await WriteProfile("Alice", GameEntries);
        await WriteProfile("Bob", GameEntries);

        var manage = async () => await CreateLoadout();

        (await manage.Should().ThrowAsync<LarianProfileUnresolvedException>()).Which.Message.Should().Be(Unresolved);
        Loadout.All(Connection.Db).Should().BeEmpty();
    }

    [Fact]
    public async Task AProfileWithNoListRefusesInsteadOfInventingOne()
    {
        var loadout = await LoadoutWithAlice();
        await InstallMod(loadout, "Alpha mod", "alpha.pak", AlphaPak());
        OnDisk(ModsettingsOf("Alice")).Delete();

        var apply = async () => await Apply(loadout);

        (await apply.Should().ThrowAsync<LarianModListMissingException>()).Which.Message.Should().Be(NoList);
        OnDisk(ModsettingsOf("Alice")).FileExists.Should().BeFalse();
    }

    [Fact]
    public async Task SwitchingTheProfileLeavesTheOldFileByteIdentical()
    {
        var loadout = await LoadoutWithAlice();
        await InstallMod(loadout, "Alpha mod", "alpha.pak", AlphaPak());
        await WriteProfile("Bob", GameEntries);
        PickProfile("Alice");
        await Apply(loadout);
        var aliceBefore = await File.ReadAllBytesAsync(OnDisk(ModsettingsOf("Alice")).ToString());
        DiskNames("Alice").Should().Contain("Alpha");

        PickProfile("Bob");
        await Apply(loadout);

        (await File.ReadAllBytesAsync(OnDisk(ModsettingsOf("Alice")).ToString())).Should().Equal(aliceBefore);
        DiskNames("Bob").Should().Contain("Alpha");

        // A later apply keeps leaving it alone: the file is not the loadout's to delete once the profile moved on.
        await Apply(loadout);
        (await File.ReadAllBytesAsync(OnDisk(ModsettingsOf("Alice")).ToString())).Should().Equal(aliceBefore);
    }

    [Fact]
    public async Task TheGamesOtherFilesInAProfileAreNotTheLoadouts()
    {
        var loadout = await LoadoutWithAlice();
        var save = OnDisk(new GamePath(LarianConstants.PlayerProfilesLocationId, "Alice/profile.lsb"));
        await save.WriteAllTextAsync("player data");

        await Apply(loadout);

        save.FileExists.Should().BeTrue();
        (await File.ReadAllTextAsync(save.ToString())).Should().Be("player data");
        LoadoutItem.FindByLoadout(Connection.Db, loadout.LoadoutId).OfTypeLoadoutItemWithTargetPath().OfTypeLoadoutFile()
            .Select(f => ((GamePath)f.AsLoadoutItemWithTargetPath().TargetPath).Path.ToString())
            .Should().NotContain(p => p.EndsWith("profile.lsb"));
    }
}
