using Spectre.Console;
using Spectre.Console.Rendering;

namespace SilverModManager.ProxyConsole;

/// <summary>
/// Plain text written as it is. <see cref="Spectre.Console.Text"/> folds lines at the console width, which
/// inserts newlines inside the string values of <c>--json</c> output and makes it unparseable. The terminal
/// does its own soft wrapping, so a long line only costs a scroll in the user's emulator.
/// </summary>
public sealed class UnwrappedText(string text) : Renderable
{
    /// <inheritdoc />
    protected override Measurement Measure(RenderOptions options, int maxWidth)
    {
        var widest = text.Split('\n').Max(l => l.TrimEnd('\r').GetCellWidth());
        return new Measurement(widest, widest);
    }

    /// <inheritdoc />
    protected override IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
    {
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Length > 0) yield return new Segment(line);
            if (i < lines.Length - 1) yield return Segment.LineBreak;
        }
    }
}
