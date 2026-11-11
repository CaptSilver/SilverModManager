namespace SilverModManager.Abstractions.Games;

/// <summary>
/// A game that creates its renderer through DXGI, which on Linux means DXVK's or VKD3D-Proton's dxgi.dll. DirectX 9
/// and older titles (Morrowind, Oblivion, Fallout 3, New Vegas, Skyrim LE) and games that render through Vulkan or
/// OpenGL never load it, so a check about the dxgi DLL only applies to the games that implement this.
/// </summary>
/// <remarks>
/// Callers hold an <see cref="SilverModManager.Sdk.Games.IGameData"/>; the registered game instance behind it is
/// what implements this, as with <see cref="IVirtualRealityGame"/>.
/// </remarks>
public interface IDxgiGame;
