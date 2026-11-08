namespace SilverModManager.Abstractions.Games.Modlists;

/// <summary>The plugins.txt and loadorder.txt of one MO2 profile; either may be missing.</summary>
public sealed record ProfilePluginTexts(string? PluginsTxt, string? LoadorderTxt);
