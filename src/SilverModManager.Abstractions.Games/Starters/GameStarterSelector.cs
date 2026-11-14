using SilverModManager.Sdk.Games;

namespace SilverModManager.Abstractions.Games.Starters;

/// <summary>
/// Picks the starter for a launch: a loadout that replaced the command is started as a process; the game's own
/// executable goes to the store client that owns the install, when it can carry the arguments; everything else
/// is started directly.
/// </summary>
public sealed class GameStarterSelector : IGameStarterSelector
{
    private readonly Dictionary<GameStarterKind, IGameStarter> _starters;

    /// <summary>Takes the shared starters; the Steam one is per tool and comes in with each request.</summary>
    public GameStarterSelector(IEnumerable<IGameStarter> starters)
    {
        _starters = [];
        foreach (var starter in starters)
        {
            if (!_starters.TryAdd(starter.Kind, starter))
                throw new ArgumentException($"Two game starters are registered for {starter.Kind}", nameof(starters));
        }
    }

    /// <inheritdoc />
    public StarterChoice For(GameStartRequest request, IGameStarter steam)
    {
        if (request.Spec.ReplacesCommand) return new StarterChoice(Required(GameStarterKind.Process), null);

        if (request.IsPrimaryExe && request.Installation.LocatorResult.StoreClient is { } client)
        {
            var starter = client.Kind == StoreClientKind.Steam
                ? steam
                : _starters.GetValueOrDefault(KindOf(client.Kind));

            if (starter is null)
            {
                // The tool logs and shows the notice once the session has started.
                var notice = $"{client.Kind} launches are not available in this build, so SilverModManager starts {request.Spec.Exe.FileName} directly. {Lost(client)}";
                return new StarterChoice(Required(GameStarterKind.Direct), notice);
            }

            if (starter.CanCarryArguments(request)) return new StarterChoice(starter, null);

            var cannotPass = $"{client.Kind} cannot pass {string.Join(' ', request.AllArgs)} to {request.GameName}, so SilverModManager starts {request.Spec.Exe.FileName} directly. {Lost(client)}";
            return new StarterChoice(Required(GameStarterKind.Direct), cannotPass);
        }

        return new StarterChoice(Required(GameStarterKind.Direct), null);
    }

    private static GameStarterKind KindOf(StoreClientKind kind) => kind switch
    {
        StoreClientKind.Steam => GameStarterKind.Steam,
        StoreClientKind.Heroic => GameStarterKind.Heroic,
        StoreClientKind.Lutris => GameStarterKind.Lutris,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static string Lost(StoreClient client) => $"{client.Kind}'s wrappers, environment and cloud saves are not used for this launch.";

    private IGameStarter Required(GameStarterKind kind)
        => _starters.TryGetValue(kind, out var starter)
            ? starter
            : throw new InvalidOperationException($"No {kind} game starter is registered");
}
