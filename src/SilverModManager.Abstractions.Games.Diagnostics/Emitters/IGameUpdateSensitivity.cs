using JetBrains.Annotations;
using SilverModManager.Abstractions.Diagnostics.Values;

namespace SilverModManager.Abstractions.Diagnostics.Emitters;

/// <summary>
/// Implemented by games whose mods break when the game updates, so that the update checks of the Health
/// Check look at them. A game without it gets none of those checks.
/// </summary>
[PublicAPI]
public interface IGameUpdateSensitivity
{
    /// <summary>
    /// How long after the store finished writing an update the game counts as recently updated, or null
    /// when a date alone says nothing useful for this game.
    /// </summary>
    TimeSpan? DateWindow { get; }

    /// <summary>
    /// A sentence or two on what an update breaks for this game, shown in the details of the notes.
    /// </summary>
    string WhatBreaks { get; }

    /// <summary>
    /// Where the user finds the matching release of the game's mod extender, if it has one.
    /// </summary>
    NamedLink? ExtenderPage { get; }
}
