using System.Collections.Immutable;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Games.Starters;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Abstractions.Games.Tests.Starters;

/// <summary>Requests built by hand, for starters that never read the loadout.</summary>
internal static class StarterRequests
{
    public const string GameName = "Cyberpunk 2077";

    public static readonly StartTiming ZeroTiming = new(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeProvider.System);

    public static GameStartRequest For(StoreClient? client, StartTiming? timing = null, bool isPrimaryExe = true, string[]? callerArgs = null, string[]? extraArgs = null, bool replacesCommand = false)
    {
        var game = Substitute.For<IGameData>();
        game.DisplayName.Returns(GameName);
        var folder = FileSystem.Shared.FromUnsanitizedFullPath("/games/cyberpunk");
        var installation = new GameInstallation(new GameLocatorResult
        {
            Game = game,
            Path = folder,
            Store = GameStore.GOG,
            StoreIdentifier = "1423049311",
            LocatorIds = ImmutableArray<LocatorId>.Empty,
            Locator = Substitute.For<IGameLocator>(),
            StoreClient = client,
        }, GameLocations.Create(ImmutableDictionary<LocationId, AbsolutePath>.Empty.Add(LocationId.Game, folder)));
        var spec = new LaunchSpec(folder / "Cyberpunk2077.exe", extraArgs ?? []) { ReplacesCommand = replacesCommand };
        return new GameStartRequest(default, installation, spec, callerArgs ?? [], isPrimaryExe, UseShell: false, timing ?? ZeroTiming);
    }
}
