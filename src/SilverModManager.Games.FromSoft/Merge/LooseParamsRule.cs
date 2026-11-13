using System.Collections.Immutable;

namespace SilverModManager.Games.FromSoft.Merge;

/// <summary>
/// A game whose loader reads loose parameter files and, when it finds one, stops using another file of the game.
/// </summary>
/// <param name="Files">The loose parameter paths, relative to a package root, that trigger the rule.</param>
/// <param name="DisablesFile">The file the loader turns off for the game when any package maps one of <paramref name="Files"/>.</param>
public sealed record LooseParamsRule(ImmutableArray<string> Files, string DisablesFile);
