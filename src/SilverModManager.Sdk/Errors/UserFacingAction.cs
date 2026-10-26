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
