using System.Collections.Immutable;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Games;
using SilverModManager.Games.FileHashes.Emitters;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.FileHashes.Tests;

/// <summary>
/// The note that the store has an update the game has not taken yet. The cases are the manifest states a
/// real Steam library holds: settled, mid-update, and stuck with a pending flag but nothing to fetch.
/// </summary>
public sealed class GameUpdateWaitingCheckTests
{
    [Theory]
    // Settled install: StateFlags 4, no target.
    [InlineData(null, "26161058", false)]
    // Pending flag left behind with the target equal to the installed build: nothing to wait for.
    [InlineData("25080141", "25080141", false)]
    // The target differs: an update is waiting.
    [InlineData("25827938", "25080141", true)]
    public void Check_ComparesTheTargetBuildWithTheInstalledOne(string? target, string installed, bool shows)
    {
        var diagnostics = Check(target, installed, updatesInBackground: true);

        diagnostics.Any().Should().Be(shows);
    }

    [Fact]
    public void Check_TargetEqualsInstalledBuildWhileStateFlagsSayPending_IsSilent()
    {
        // StoreUpdatePending is true here, as for Portal 2 or Krita in a real library; it is not what the note gates on.
        Check("77", "77", updatesInBackground: true, updatePending: true).Should().BeEmpty();
    }

    [Fact]
    public void Check_UpdateWaiting_IsASuggestionNamingTheGameAndStore()
    {
        var diagnostic = Check("25827938", "25080141", updatesInBackground: true).Should().ContainSingle().Subject;

        diagnostic.Severity.Should().Be(DiagnosticSeverity.Suggestion);
        var data = diagnostic.Should().BeOfType<Diagnostic<Diagnostics.GameUpdateWaitingMessageData>>().Subject.MessageData;
        data.Game.Should().Be("Test game");
        data.Store.Should().Be("Steam");
    }

    [Fact]
    public void Check_StoreUpdatesInBackground_AdvisesUpdatingOnLaunch()
    {
        var data = Check("2", "1", updatesInBackground: true).Single()
            .Should().BeOfType<Diagnostic<Diagnostics.GameUpdateWaitingMessageData>>().Subject.MessageData;

        data.WhenItInstalls.Should().Contain("Only update this game when I launch it");
    }

    [Fact]
    public void Check_StoreUpdatesOnlyOnLaunch_SaysSteamInstallsItAtTheNextLaunch()
    {
        var data = Check("2", "1", updatesInBackground: false).Single()
            .Should().BeOfType<Diagnostic<Diagnostics.GameUpdateWaitingMessageData>>().Subject.MessageData;

        data.WhenItInstalls.Should().Contain("next time you launch the game through Steam");
        data.WhenItInstalls.Should().NotContain("Only update this game when I launch it");
    }

    [Fact]
    public void Check_NeedsNoAppliedLoadoutOrMods()
    {
        // The note is about the store, not the loadout: it shows before anything has been applied.
        Check("2", "1", updatesInBackground: true).Should().ContainSingle();
    }

    private static IEnumerable<Diagnostic> Check(string? target, string installed, bool updatesInBackground, bool updatePending = false)
    {
        var game = Substitute.For<SubstitutableGame, IGameUpdateSensitivity>();
        game.DisplayName.Returns("Test game");
        var sensitivity = (IGameUpdateSensitivity)game;
        sensitivity.WhatBreaks.Returns("Mods with a DLL break.");

        var result = new GameLocatorResult
        {
            Game = game,
            Path = FileSystem.Shared.FromUnsanitizedFullPath("/games/test"),
            Store = GameStore.Steam,
            StoreIdentifier = "1",
            LocatorIds = [],
            Locator = Substitute.For<IGameLocator>(),
            StoreBuildId = installed,
            StoreTargetBuildId = target,
            StoreUpdatePending = updatePending,
            StoreUpdatesInBackground = updatesInBackground,
        };
        var installation = new GameInstallation(result, GameLocations.Create(ImmutableDictionary<LocationId, AbsolutePath>.Empty.Add(LocationId.Game, result.Path)));
        var loadout = default(Loadout.ReadOnly);
        return new GameUpdateWaitingCheck().Check(new GameUpdateContext(loadout, installation, sensitivity, result));
    }
}
