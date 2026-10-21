using System.Diagnostics.CodeAnalysis;
using System.Text;
using NexusMods.Abstractions.Games;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Platform.Proton.Launch;

/// <summary>
/// What a <see cref="LoadoutResolver"/> found: the loadout and the installation it belongs to, or the
/// message to print before exiting with <see cref="LaunchSession.FailureExitCode"/>.
/// </summary>
public sealed record LoadoutResolution(Loadout.ReadOnly? Loadout, GameInstallation? Installation, string? Error)
{
    /// <summary>
    /// Whether a loadout was found.
    /// </summary>
    [MemberNotNullWhen(true, nameof(Loadout), nameof(Installation))]
    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsResolved => Error is null;

    internal static LoadoutResolution Found(Loadout.ReadOnly loadout, GameInstallation installation) => new(loadout, installation, null);

    internal static LoadoutResolution Failed(string error) => new(null, null, error);

    /// <summary>
    /// The process exit code: 0 when resolved, else the launch failure code.
    /// </summary>
    public int ExitCode => IsResolved ? 0 : LaunchSession.FailureExitCode;
}

/// <summary>
/// Finds the loadout a launch names. The name is matched as typed, trimmed and ignoring case, among the loadouts
/// the user can see; nothing is guessed when it matches none or more than one.
/// </summary>
public sealed class LoadoutResolver(IConnection connection, IGameRegistry registry, IEnvironmentReader environment)
{
    private const string SteamAppIdVariable = "SteamAppId";

    /// <summary>
    /// Narrows the installations to search by the Steam app id Steam put in the environment, else the one in
    /// the command, else <c>--game</c>, else searches them all.
    /// </summary>
    public LoadoutResolution Resolve(LaunchArgs args)
    {
        var installations = registry.LocateGameInstallations();
        if (!TryScope(args, installations, out var scoped, out var scopeError)) return LoadoutResolution.Failed(scopeError);

        var wanted = args.LoadoutName.Trim();
        var db = connection.Db;
        var matches = new List<(GameInstallation Installation, Loadout.ReadOnly Loadout)>();
        var names = new List<(GameInstallation Installation, string Name)>();
        foreach (var installation in scoped)
        {
            if (!registry.TryGetMetadata(installation, out var metadata)) continue;

            var visible = Loadout.FindByInstallation(db, metadata).Where(l => l.IsVisible()).ToArray();
            var named = visible.Where(l => string.Equals(l.Name.Trim(), wanted, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (named.Length > 1)
            {
                var shortNames = string.Join(", ", named.Select(l => l.ShortName).Order(StringComparer.Ordinal));
                return LoadoutResolution.Failed(
                    $"{installation.Game.DisplayName} has more than one loadout named '{wanted}' (short names {shortNames}); " +
                    "rename one in SilverModManager and launch again.");
            }

            if (named.Length == 1) matches.Add((installation, named[0]));
            names.AddRange(visible.Select(l => (installation, l.Name)));
        }

        switch (matches.Count)
        {
            case 1:
                return LoadoutResolution.Found(matches[0].Loadout, matches[0].Installation);
            case 0:
                return LoadoutResolution.Failed(NoMatch(wanted, scoped, names));
            default:
                var listing = string.Join('\n', matches.Select(m => $"  {m.Installation.Game.DisplayName}: {m.Loadout.Name}"));
                return LoadoutResolution.Failed(
                    $"More than one game has a loadout named '{wanted}':\n{listing}\n" +
                    "Pick one with --game <appid|name>, for example: silvermod run '" + wanted + "' --game <appid|name> -- <command>");
        }
    }

    private bool TryScope(
        LaunchArgs args,
        IReadOnlyList<GameInstallation> installations,
        [NotNullWhen(true)] out IReadOnlyList<GameInstallation>? scoped,
        [NotNullWhen(false)] out string? error)
    {
        scoped = null;
        error = null;

        var fromEnvironment = environment.Get(SteamAppIdVariable)?.Trim();
        var fromCommand = ExeSubstitution.ExtractAppId(args.Command);
        var appId = !string.IsNullOrEmpty(fromEnvironment) ? fromEnvironment : fromCommand;
        if (appId is not null)
        {
            var bySteam = installations.Where(i => IsSteamApp(i, appId)).ToArray();
            if (bySteam.Length > 0)
            {
                scoped = bySteam;
                return true;
            }

            error = $"No installed game has the Steam app id {appId}, which Steam passed in " +
                    (appId == fromEnvironment ? $"{SteamAppIdVariable}." : "the launch command.");
            return false;
        }

        if (args.Game is not { } game)
        {
            scoped = installations;
            return true;
        }

        var byGame = ByGame(installations, game);
        if (byGame.Count > 0)
        {
            scoped = byGame;
            return true;
        }

        error = NoGameMatch(game);
        return false;
    }

    /// <summary>
    /// The one installation <paramref name="game"/> names, by Steam app id or display name. Nothing is guessed
    /// when it names none or several.
    /// </summary>
    public GameInstallation? ResolveGame(string game, out string? error)
    {
        var matches = ByGame(registry.LocateGameInstallations(), game);
        error = matches.Count switch
        {
            1 => null,
            0 => NoGameMatch(game),
            _ => $"More than one installed game matches --game '{game}': "
                 + string.Join(", ", matches.Select(m => $"{m.Game.DisplayName} ({m.LocatorResult.Store.Value.ToLowerInvariant()} {m.LocatorResult.StoreIdentifier})"))
                 + ". Use the Steam app id.",
        };
        return error is null ? matches[0] : null;
    }

    private static IReadOnlyList<GameInstallation> ByGame(IReadOnlyList<GameInstallation> installations, string game)
    {
        var wanted = game.Trim();
        return installations
            .Where(i => IsSteamApp(i, wanted) || string.Equals(i.Game.DisplayName, wanted, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private static string NoGameMatch(string game) => $"No installed game matches --game '{game}'. Use a Steam app id or the game's name.";

    private static bool IsSteamApp(GameInstallation installation, string appId)
        => installation.LocatorResult.Store == GameStore.Steam && installation.LocatorResult.StoreIdentifier == appId;

    private static string NoMatch(string wanted, IReadOnlyList<GameInstallation> scoped, IReadOnlyList<(GameInstallation Installation, string Name)> names)
    {
        var message = new StringBuilder($"No loadout named '{wanted}'");
        if (scoped.Count == 1) message.Append(" for ").Append(scoped[0].Game.DisplayName);
        message.Append('.');

        if (names.Count == 0)
        {
            message.Append(" There are no loadouts to run yet.");
        }
        else if (scoped.Count == 1)
        {
            message.Append(" Loadouts: ").Append(string.Join(", ", names.Select(n => n.Name))).Append('.');
        }
        else
        {
            message.Append(" Loadouts:");
            foreach (var (installation, name) in names) message.Append("\n  ").Append(installation.Game.DisplayName).Append(": ").Append(name);
        }

        message.Append("\nFix the name in Steam > Properties > Launch Options, or rename a loadout in SilverModManager.");
        return message.ToString();
    }
}
