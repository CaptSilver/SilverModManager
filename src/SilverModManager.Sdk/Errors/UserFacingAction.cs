using JetBrains.Annotations;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Sdk.Errors;

/// <summary>
/// A follow-up a refusal dialog offers as a button.
/// </summary>
[PublicAPI]
public abstract record UserFacingAction;

/// <summary>
/// Open the Health Check of a loadout, which holds the diagnostic that carries the choices for fixing the refusal.
/// </summary>
/// <param name="LoadoutId">The loadout whose Health Check to open.</param>
[PublicAPI]
public sealed record OpenHealthCheck(LoadoutId LoadoutId) : UserFacingAction;

/// <summary>
/// Close Steam, then run <paramref name="Retry"/>: the refusal is that Steam is running, and the step that was refused
/// can go ahead once it is not.
/// </summary>
/// <param name="Retry">Closes Steam, redoes what was refused and starts Steam again.</param>
[PublicAPI]
public sealed record CloseSteamAndRetry(Func<Task> Retry) : UserFacingAction;
