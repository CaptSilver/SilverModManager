using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Microsoft.Extensions.Logging;
using Splat;

namespace SilverModManager.App.UI.Extensions;

public static class ClipboardExtensions
{
    /// <summary>
    /// Finds the clipboard of the window the view sits in. A copy button can be pressed after its view left
    /// the window, so a missing clipboard is expected: it is logged at Trace and the caller copies nothing.
    /// </summary>
    public static bool TryGetClipboard(this Visual view, [NotNullWhen(true)] out IClipboard? clipboard)
    {
        clipboard = TopLevel.GetTopLevel(view)?.Clipboard;
        if (clipboard is null)
        {
            // Views are built by XAML and have no injected logger, so the factory comes from the locator.
            var logger = Locator.Current.GetService<ILoggerFactory>()?.CreateLogger(view.GetType().FullName ?? nameof(ClipboardExtensions))
                         ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
            logger.LogTrace("Copy pressed in {View} but it has no window with a clipboard, nothing was copied", view.GetType().Name);
        }
        return clipboard is not null;
    }
}
