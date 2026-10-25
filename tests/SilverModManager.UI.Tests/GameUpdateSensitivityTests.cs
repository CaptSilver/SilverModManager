using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Games;
using SilverModManager.Games.CreationEngine.Fallout4;
using SilverModManager.Games.CreationEngine.OblivionRemastered;
using SilverModManager.Games.CreationEngine.SkyrimSE;
using SilverModManager.Games.CreationEngine.Starfield;
using SilverModManager.Games.RedEngine.Cyberpunk2077;
using SilverModManager.Games.Unity.Peak;
using SilverModManager.Games.Unity.Repo;
using SilverModManager.Sdk.Games;

namespace SilverModManager.UI.Tests;

/// <summary>
/// Which games the update notes look at, and for how long after the store wrote an update the date note
/// shows. Games whose mods are not tied to one build (and the FromSoft ones, which report their own build
/// drift) implement nothing.
/// </summary>
public class GameUpdateSensitivityTests(IServiceProvider provider) : AUiTest(provider)
{
    private IGameUpdateSensitivity? Sensitivity<TGame>() where TGame : class, IGame =>
        Provider.GetServices<IGame>().OfType<TGame>().Single() as IGameUpdateSensitivity;

    [Theory]
    [InlineData(typeof(SkyrimSE), 30)]
    [InlineData(typeof(Fallout4), 30)]
    [InlineData(typeof(OblivionRemastered), 30)]
    [InlineData(typeof(Cyberpunk2077Game), 30)]
    [InlineData(typeof(Repo), 14)]
    public void GamesWhoseModsBreakOnPatchesHaveADateWindow(Type game, int days)
    {
        var sensitivity = Get(game);

        sensitivity.DateWindow.Should().Be(TimeSpan.FromDays(days));
        sensitivity.WhatBreaks.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    // Patches too often for a date to mean anything: only the change since the last apply is reported.
    [InlineData(typeof(Starfield))]
    [InlineData(typeof(Peak))]
    [InlineData(typeof(SilverModManager.Games.MountAndBlade2Bannerlord.Bannerlord))]
    public void GamesThatPatchOftenHaveNoDateWindow(Type game)
    {
        var sensitivity = Get(game);

        sensitivity.DateWindow.Should().BeNull();
        sensitivity.WhatBreaks.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void CyberpunkSaysWhichExtendersNeedAMatchingRelease()
    {
        Sensitivity<Cyberpunk2077Game>()!.WhatBreaks.Should().Contain("Cyber Engine Tweaks").And.Contain("RED4ext");
    }

    [Fact]
    public void FromSoftGamesLeaveTheirBuildDriftNoteToTheirOwnEmitter()
    {
        var fromSoft = Provider.GetServices<IGame>().Where(g => g.GetType().Namespace!.Contains("FromSoft")).ToArray();

        fromSoft.Should().NotBeEmpty();
        fromSoft.Should().NotContain(g => g is IGameUpdateSensitivity);
    }

    private IGameUpdateSensitivity Get(Type game)
    {
        var instance = Provider.GetServices<IGame>().Single(g => g.GetType() == game);
        return instance.Should().BeAssignableTo<IGameUpdateSensitivity>().Subject;
    }
}
