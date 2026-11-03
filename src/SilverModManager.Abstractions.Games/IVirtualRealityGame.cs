namespace SilverModManager.Abstractions.Games;

/// <summary>
/// A game that is a VR title, such as Skyrim VR or Fallout 4 VR. Checks that only matter for a headset, like the
/// OpenXR runtime a replacement for the OpenVR library needs, apply to the games that implement this.
/// </summary>
/// <remarks>
/// Callers hold an <see cref="SilverModManager.Sdk.Games.IGameData"/>; the registered game instance behind it is
/// what implements this, as with <see cref="IWinePrefixRequirements"/>.
/// </remarks>
public interface IVirtualRealityGame;
