namespace NexusMods.Games.FromSoft.Overlay;

/// <summary>What a modded session may leave in the overlay's upper folder without the audit flagging it.</summary>
/// <param name="Folders">Folders, with a trailing slash, whose whole content is expected. They belong to a game and its mods, so the default has none.</param>
/// <param name="Names">File name patterns the game and its libraries write on their own, at any depth.</param>
public sealed record OverlayAllowlist(IReadOnlyList<string> Folders, IReadOnlyList<string> Names)
{
    public static readonly OverlayAllowlist Default = new(Folders: [], Names: ["*.dmp", "*.log", "vkd3d-proton.cache*", "*.dxvk-cache", "*.ini"]);
}
