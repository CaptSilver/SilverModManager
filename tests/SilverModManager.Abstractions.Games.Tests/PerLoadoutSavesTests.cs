using FluentAssertions;
using NexusMods.Paths;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.StandardGameLocators.TestHelpers;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;

namespace SilverModManager.Abstractions.Games.Tests;

public class LoadoutSaveSlotTests(ITestOutputHelper helper) : AIsolatedGameTest<LoadoutSaveSlotTests, StubbedGame>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddGame<StubbedGame>()
            .AddSingleton<IGameLocator>(s => new UniversalStubbedGameLocator<StubbedGame>(
                s,
                s.GetRequiredService<IFileSystem>(),
                s.GetRequiredService<TemporaryFileManager>(),
                stores: [GameStore.Steam])
            {
                LocatorIds = [LocatorId.From("3976631895")],
            });
    }

    [Fact]
    public async Task IdOfIsUnchangedByRenamingTheLoadout()
    {
        var loadout = await LoadoutManager.CreateLoadout(GameInstallation, "before");
        var before = LoadoutSaveSlot.IdOf(loadout);

        var renamed = await LoadoutManager.RenameLoadout(loadout.LoadoutId, "after");

        renamed.Name.Should().Be("after");
        LoadoutSaveSlot.IdOf(renamed).Should().Be(before);
    }

    [Fact]
    public async Task IdOfDiffersBetweenLoadouts()
    {
        var first = await LoadoutManager.CreateLoadout(GameInstallation, "first");
        var second = await LoadoutManager.CreateLoadout(GameInstallation, "second");

        LoadoutSaveSlot.IdOf(first).Should().NotBe(LoadoutSaveSlot.IdOf(second));
    }

    [Fact]
    public async Task IdPatternMatchesTheIdOfALoadout()
    {
        var loadout = await LoadoutManager.CreateLoadout(GameInstallation, "any");
        var id = LoadoutSaveSlot.IdOf(loadout);

        System.Text.RegularExpressions.Regex.IsMatch(id, LoadoutSaveSlot.IdPattern).Should().BeTrue();
        LoadoutSaveSlot.IsSlotId(id).Should().BeTrue();
    }
}

public class LoadoutSaveSlotPatternTests
{
    [Theory]
    [InlineData("modded")]
    [InlineData("silver")]
    [InlineData("")]
    [InlineData("0123456789abcd0")] // one digit too many
    [InlineData("0123456789abc")] // one digit too few
    [InlineData("0123456789ABCD")] // not the generated case
    [InlineData("0123456789abcd\n")] // a trailing newline must not slip past the anchor
    public void IdPatternRejectsNamesTheAppDidNotWrite(string text)
    {
        LoadoutSaveSlot.IsSlotId(text).Should().BeFalse();
        System.Text.RegularExpressions.Regex.IsMatch(text, LoadoutSaveSlot.IdPattern).Should().BeFalse();
    }
}

public class SavePlanTests
{
    [Fact]
    public void IniKeyPlansWithTheSameFieldsAreEqual()
    {
        new SaveRedirectPlan.IniKey("General", "sLocalSavePath", "Saves\\a").Should()
            .Be(new SaveRedirectPlan.IniKey("General", "sLocalSavePath", "Saves\\a"));
    }

    [Fact]
    public void LoaderKeyAndUnsupportedPlansAreValueEqual()
    {
        new SaveRedirectPlan.LoaderKey("save_file_name", "ER0000_a.sl2").Should()
            .Be(new SaveRedirectPlan.LoaderKey("save_file_name", "ER0000_a.sl2"));
        new SaveRedirectPlan.Unsupported("no").Should().Be(new SaveRedirectPlan.Unsupported("no"));
        SaveRedirectPlan x = new SaveRedirectPlan.Unsupported("no");
        x.Should().NotBe(new SaveRedirectPlan.LoaderKey("no", "no"));
    }

    [Fact]
    public void AdoptionPlansWithTheSameCopiesAreEqual()
    {
        AbsolutePath Path(string p) => FileSystem.Shared.FromUnsanitizedFullPath(p);
        SaveAdoptionPlan Make() => new("Copy 1 save", [new SaveAdoptionPlan.Copy(Path("/s/a.sl2"), Path("/s/b.sl2"))]);

        Make().Should().Be(Make());
        Make().GetHashCode().Should().Be(Make().GetHashCode());
        Make().Should().NotBe(new SaveAdoptionPlan("Copy 1 save", []));
    }
}
