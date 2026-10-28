using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.TestFramework;
using SilverModManager.Games.Witcher.Witcher3;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.Ini;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.Games.Witcher.Tests.Witcher3.Menu;

/// <summary>A menu mod's <c>.part.txt</c> is stored as an INI tweak and baked into the settings file it names.</summary>
public class SettingsPartsTests(ITestOutputHelper helper) : AIsolatedGameTest<SettingsPartsTests, Witcher3Game>(helper)
{
    private const string Baseline = "[Exploration]\r\nIK_E=(Action=Interact)\r\n";

    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services)
        .AddWitcherGames()
        .AddUniversalGameLocator<Witcher3Game>(new Version("4.0"), locationsFileSystem: IsolatedKnownFolders.Create);

    private static GamePath Settings(string name) => new(WitcherConstants.WitcherDocuments, name);

    private AbsolutePath OnDisk(GamePath path) => GameInstallation.Locations.ToAbsolutePath(path);

    private async Task WriteSettings(string name, string text)
    {
        OnDisk(Settings(name)).Parent.CreateDirectory();
        await OnDisk(Settings(name)).WriteAllBytesAsync(System.Text.Encoding.UTF8.GetBytes(text));
    }

    private Task AddPart(Loadout.ReadOnly loadout, string mod, ulong priority, string target, string text) =>
        Witcher3Fixtures.AddMod(Connection, FileStore, loadout.LoadoutId, mod, priority, ($"INI Tweaks/{mod} [{target}].ini", text));

    private async Task<IniDocument> Applied(string name) => IniDocument.Parse(await OnDisk(Settings(name)).ReadAllBytesAsync());

    [Fact]
    public async Task InputSettings_IsBakedFromTheBaselineAndTheModsParts()
    {
        var loadout = await CreateLoadout();
        await WriteSettings("input.settings", Baseline);
        await AddPart(loadout, "modA", 1, "input.settings", "[SDL]\r\nIK_F=(Action=Fire)\r\n");

        await Synchronizer.Synchronize(Refresh(loadout));

        var baked = await Applied("input.settings");
        baked.Get("Exploration", "IK_E").Should().Be("(Action=Interact)");
        baked.Get("SDL", "IK_F").Should().Be("(Action=Fire)");
    }

    [Fact]
    public async Task HigherPriorityPart_WinsAKey()
    {
        var loadout = await CreateLoadout();
        await WriteSettings("input.settings", Baseline);
        await AddPart(loadout, "modA", 1, "input.settings", "[SDL]\r\nIK_F=(Action=A)\r\n");
        await AddPart(loadout, "modB", 2, "input.settings", "[SDL]\r\nIK_F=(Action=B)\r\n");

        await Synchronizer.Synchronize(Refresh(loadout));

        (await Applied("input.settings")).Get("SDL", "IK_F").Should().Be("(Action=B)", "the group with the higher priority is merged last");
    }

    [Fact]
    public async Task PartFiles_AreKeptOffDisk()
    {
        var loadout = await CreateLoadout();
        await WriteSettings("input.settings", Baseline);
        await AddPart(loadout, "modA", 1, "input.settings", "[SDL]\r\nIK_F=(Action=Fire)\r\n");

        await Synchronizer.Synchronize(Refresh(loadout));

        OnDisk(new GamePath(LocationId.Game, "INI Tweaks")).DirectoryExists().Should().BeFalse("the game ignores the folder, so the fragments stay in the loadout");
    }

    [Fact]
    public async Task AMissingSettingsFile_IsNotRendered()
    {
        var loadout = await CreateLoadout();
        await WriteSettings("input.settings", Baseline);
        await AddPart(loadout, "modA", 1, "dx12user.settings", "[Rendering]\r\nMaxFPS=60\r\n");

        var act = () => Synchronizer.Synchronize(Refresh(loadout));

        await act.Should().NotThrowAsync();
        OnDisk(Settings("dx12user.settings")).FileExists.Should().BeFalse("there is no baseline to build it from");
    }
}
