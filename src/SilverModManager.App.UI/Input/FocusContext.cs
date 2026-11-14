using Avalonia.Controls;
using Avalonia.Input;
using SilverModManager.App.UI.Overlays;

namespace SilverModManager.App.UI.Input;

/// <summary>What a binding can act on: the window, what has the focus in it, and the overlay stack.</summary>
public sealed record FocusContext(TopLevel Root, IInputElement? Focused, IOverlayController Overlays);
