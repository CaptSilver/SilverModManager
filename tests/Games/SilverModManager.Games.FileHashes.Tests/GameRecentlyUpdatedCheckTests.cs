using System.Collections.Immutable;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.FileHashes.Emitters;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.FileHashes.Tests;

/// <summary>
/// The note that the store updated the game within the game's window, and the build-changed note's
/// escalation when installed mods carry native code. Both only speak up for a loadout that was applied
/// and has mods the update can break.
/// </summary>
public sealed class GameRecentlyUpdatedCheckTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Window = TimeSpan.FromDays(30);

    private readonly IGameRegistry _registry = Substitute.For<IGameRegistry>();
    private readonly RealQueryEngineHost _host;
    private readonly IConnection _main;
    private readonly FakeTimeProvider _time = new(Now);

    public GameRecentlyUpdatedCheckTests()
    {
        _host = new RealQueryEngineHost(services => services.AddSingleton(_registry));
        _main = _host.OpenInMemory("main");
    }

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Recent_UpdateInsideTheWindowWithNativeMods_SuggestsLookingAtMods()
    {
        var loadout = await AddLoadout(applied: true, ("Codeware", "red4ext/plugins/Codeware/Codeware.dll"));

        var diagnostic = Recent(loadout, Window, lastWrite: Now - TimeSpan.FromDays(1)).Should().ContainSingle().Subject;

        diagnostic.Severity.Should().Be(DiagnosticSeverity.Suggestion);
        var data = diagnostic.Should().BeOfType<Diagnostic<Diagnostics.GameRecentlyUpdatedMessageData>>().Subject.MessageData;
        data.Game.Should().Be("Test game");
        data.Store.Should().Be("Steam");
        data.Date.Should().Be((Now - TimeSpan.FromDays(1)).ToLocalTime().ToString("d MMMM yyyy", CultureInfo.InvariantCulture));
        data.WhatBreaks.Should().Be("Mods with a DLL break.");
        data.Mods.Should().Be("- Codeware");
    }

    [Theory]
    [InlineData(29, true)]
    [InlineData(30, true)]
    [InlineData(31, false)]
    public async Task Recent_UpdateAroundTheEdgeOfTheWindow_ShowsOnlyInsideIt(int daysAgo, bool shows)
    {
        var loadout = await AddLoadout(applied: true, ("Codeware", "red4ext/plugins/Codeware/Codeware.dll"));

        Recent(loadout, Window, lastWrite: Now - TimeSpan.FromDays(daysAgo)).Any().Should().Be(shows);
    }

    [Fact]
    public async Task Recent_GameWithoutAWindow_IsSilent()
    {
        var loadout = await AddLoadout(applied: true, ("Codeware", "red4ext/plugins/Codeware/Codeware.dll"));

        Recent(loadout, window: null, lastWrite: Now).Should().BeEmpty();
    }

    [Fact]
    public async Task Recent_StoreHasNoUpdateTime_IsSilent()
    {
        var loadout = await AddLoadout(applied: true, ("Codeware", "red4ext/plugins/Codeware/Codeware.dll"));

        Recent(loadout, Window, lastWrite: null).Should().BeEmpty();
    }

    [Fact]
    public async Task Recent_LoadoutNeverApplied_IsSilent()
    {
        // A fresh install or reinstall sets the update time too; with nothing applied there is nothing stale.
        var loadout = await AddLoadout(applied: false, ("Codeware", "red4ext/plugins/Codeware/Codeware.dll"));

        Recent(loadout, Window, lastWrite: Now).Should().BeEmpty();
    }

    [Fact]
    public async Task Recent_NoModHooksTheGamesCode_IsSilent()
    {
        var loadout = await AddLoadout(applied: true, ("Look", "archive/pc/mod/Look.archive"));

        Recent(loadout, Window, lastWrite: Now).Should().BeEmpty();
    }

    [Fact]
    public async Task Recent_GameThatCannotNameNativeCode_IsSilent()
    {
        var loadout = await AddLoadout(applied: true, ("Codeware", "red4ext/plugins/Codeware/Codeware.dll"));

        Recent(loadout, Window, lastWrite: Now, nativeCode: false).Should().BeEmpty();
    }

    [Fact]
    public async Task Recent_SeveralMods_AreListedByName()
    {
        var loadout = await AddLoadout(applied: true,
            ("Codeware", "red4ext/plugins/Codeware/Codeware.dll"),
            ("ArchiveXL", "red4ext/plugins/ArchiveXL/ArchiveXL.dll"));

        var data = Recent(loadout, Window, lastWrite: Now).Single().Should().BeOfType<Diagnostic<Diagnostics.GameRecentlyUpdatedMessageData>>().Subject.MessageData;

        data.Mods.Should().Be("- ArchiveXL\n- Codeware");
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Recent_StoreUpdatingInTheBackground_AdvisesUpdatingOnLaunch(bool background, bool advice)
    {
        var loadout = await AddLoadout(applied: true, ("Codeware", "red4ext/plugins/Codeware/Codeware.dll"));

        var data = Recent(loadout, Window, lastWrite: Now, updatesInBackground: background).Single()
            .Should().BeOfType<Diagnostic<Diagnostics.GameRecentlyUpdatedMessageData>>().Subject.MessageData;

        if (advice) data.UpdateAdvice.Should().Contain("Only update this game when I launch it");
        else data.UpdateAdvice.Should().BeEmpty();
    }

    [Fact]
    public async Task BuildChanged_WithNativeMods_IsAWarningNamingThem()
    {
        var loadout = await AddLoadout(applied: true, ("Codeware", "red4ext/plugins/Codeware/Codeware.dll"), ("Look", "archive/pc/mod/Look.archive"));

        var diagnostic = Changed(loadout, nativeCode: true).Should().ContainSingle().Subject;

        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
        var data = diagnostic.Should().BeOfType<Diagnostic<Diagnostics.GameBuildChangedWithModsMessageData>>().Subject.MessageData;
        data.ModCount.Should().Be(1);
        data.Mods.Should().Be("- Codeware");
        data.WhatBreaks.Should().Be("Mods with a DLL break.");
    }

    [Fact]
    public async Task BuildChanged_WithoutNativeMods_StaysASuggestion()
    {
        var loadout = await AddLoadout(applied: true, ("Look", "archive/pc/mod/Look.archive"));

        Changed(loadout, nativeCode: true).Should().ContainSingle().Which.Severity.Should().Be(DiagnosticSeverity.Suggestion);
    }

    private IEnumerable<Diagnostic> Recent(Loadout.ReadOnly loadout, TimeSpan? window, DateTimeOffset? lastWrite, bool nativeCode = true, bool updatesInBackground = false)
        => new GameRecentlyUpdatedCheck(_time).Check(Context(loadout, window, lastWrite, nativeCode, updatesInBackground, locatorIds: ["a"]));

    private IEnumerable<Diagnostic> Changed(Loadout.ReadOnly loadout, bool nativeCode)
        => new GameBuildChangedCheck().Check(Context(loadout, Window, lastWrite: null, nativeCode, updatesInBackground: false, locatorIds: ["b"]));

    private async Task<Loadout.ReadOnly> AddLoadout(bool applied, params (string Mod, string Path)[] mods)
    {
        var id = await HashRows.AddLoadout(_main, GameStore.Steam, "/games/test", "a");
        using var tx = _main.BeginTransaction();
        if (applied) tx.Add(id, Loadout.LastAppliedDateTime, Now.UtcDateTime);
        foreach (var (mod, path) in mods) AddMod(tx, LoadoutId.From(id), mod, path);
        await tx.Commit();
        return Loadout.Load(_main.Db, id);
    }

    private static void AddMod(ITransaction tx, LoadoutId loadoutId, string name, string path)
    {
        _ = new LoadoutItemGroup.New(tx, out var group)
        {
            IsGroup = true,
            LoadoutItem = new LoadoutItem.New(tx, group) { LoadoutId = loadoutId, Name = name },
        };

        var bytes = System.Text.Encoding.UTF8.GetBytes(path);
        _ = new LoadoutFile.New(tx, out var file)
        {
            LoadoutItemWithTargetPath = new LoadoutItemWithTargetPath.New(tx, file)
            {
                LoadoutItem = new LoadoutItem.New(tx, file)
                {
                    LoadoutId = loadoutId,
                    ParentId = LoadoutItemGroupId.From(group),
                    Name = path,
                },
                TargetPath = new GamePath(LocationId.Game, path).ToGamePathParentTuple(loadoutId),
            },
            Hash = bytes.xxHash3(),
            Size = Size.FromLong(bytes.Length),
        };
    }

    private GameUpdateContext Context(Loadout.ReadOnly loadout, TimeSpan? window, DateTimeOffset? lastWrite, bool nativeCode, bool updatesInBackground, string[] locatorIds)
    {
        var game = nativeCode
            ? Substitute.For<SubstitutableGame, IGameUpdateSensitivity, IGameNativeCode>()
            : Substitute.For<SubstitutableGame, IGameUpdateSensitivity>();
        game.DisplayName.Returns("Test game");

        var sensitivity = (IGameUpdateSensitivity)game;
        sensitivity.DateWindow.Returns(window);
        sensitivity.WhatBreaks.Returns("Mods with a DLL break.");
        if (nativeCode)
            ((IGameNativeCode)game).HooksGameCode(Arg.Any<GamePath>()).Returns(call => call.Arg<GamePath>().Extension == new Extension(".dll"));

        var result = new GameLocatorResult
        {
            Game = game,
            Path = FileSystem.Shared.FromUnsanitizedFullPath("/games/test"),
            Store = GameStore.Steam,
            StoreIdentifier = "1",
            LocatorIds = [..locatorIds.Select(LocatorId.From)],
            Locator = Substitute.For<IGameLocator>(),
            StoreLastWrite = lastWrite,
            StoreUpdatesInBackground = updatesInBackground,
        };
        var installation = new GameInstallation(result, GameLocations.Create(ImmutableDictionary<LocationId, AbsolutePath>.Empty.Add(LocationId.Game, result.Path)));
        return new GameUpdateContext(loadout, installation, sensitivity, result);
    }
}
