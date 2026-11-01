using Avalonia;
using Avalonia.Controls;
using SilverModManager.App.UI.Controls.DragAndDrop;

namespace SilverModManager.App.UI.Windows;

/// <summary>
/// Drop feedback on the main window's overlay layer. The window's own zone gets the dimmed full-window overlay; any
/// other zone gets a caption next to its host and the <c>:drop-over</c> pseudo-class on it, so the page underneath
/// stays readable while the user chooses where to drop.
/// </summary>
public sealed class WindowDropFeedback(Control overlay, TextBlock overlayText, Border caption, TextBlock captionText) : IDropFeedback
{
    public const string DropOverPseudoClass = ":drop-over";
    public const string RefuseClass = "refuse";

    // Hosts narrower than this share of the layer (left menu entries) get the caption beside them; wider ones
    // (a whole page) have no room beside them, so it goes inside their top left corner.
    private const double NarrowShare = 0.4;
    private const double Gap = 8;

    private Control? _over;

    public void Show(Control over, DropVerdict verdict)
    {
        var text = verdict switch
        {
            DropVerdict.Accept accept => accept.Text,
            DropVerdict.Refuse refuse => refuse.Reason,
            _ => throw new ArgumentException("Only an accept or a refuse has anything to show.", nameof(verdict)),
        };

        Hide();
        _over = over;

        if (over is TopLevel)
        {
            overlayText.Text = text;
            overlay.IsVisible = true;
            return;
        }

        captionText.Text = text;
        if (verdict is DropVerdict.Refuse) caption.Classes.Add(RefuseClass);
        else caption.Classes.Remove(RefuseClass);
        caption.Margin = CaptionMargin(over);
        caption.IsVisible = true;
        SetDropOver(over, true);
    }

    public void Hide()
    {
        overlay.IsVisible = false;
        caption.IsVisible = false;
        if (_over is { } over) SetDropOver(over, false);
        _over = null;
    }

    private Thickness CaptionMargin(Control over)
    {
        if (caption.Parent is not Visual layer || over.TranslatePoint(default, layer) is not { } origin) return default;

        var host = new Rect(origin, over.Bounds.Size);
        var beside = host.Width < layer.Bounds.Width * NarrowShare;
        return beside
            ? new Thickness(host.Right + Gap, host.Top, 0, 0)
            : new Thickness(host.Left + Gap * 2, host.Top + Gap * 2, 0, 0);
    }

    private static void SetDropOver(Control control, bool on) => ((IPseudoClasses)control.Classes).Set(DropOverPseudoClass, on);
}
