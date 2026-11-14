using Avalonia;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using SilverModManager.App.UI.Settings;

namespace SilverModManager.App.UI.Windows;

/// <summary>
/// Switches the type sizes, row height and focus ring between the Desktop values the theme ships and the larger
/// Couch ones.
/// </summary>
public static class DensityService
{
    private static readonly Uri CouchUri = new("avares://SilverModManager.Themes.Silver/Resources/Density/Couch.axaml");

    /// <summary>
    /// Turns the stored setting into a concrete density: Auto is Couch in Game Mode and Desktop otherwise.
    /// </summary>
    public static UiDensity Resolve(UiDensity setting, bool isGameMode)
    {
        return setting == UiDensity.Auto ? isGameMode ? UiDensity.Couch : UiDensity.Desktop : setting;
    }

    /// <summary>
    /// Merges the Couch values over the theme's, for <see cref="UiDensity.Couch"/>. Desktop keeps the theme's own.
    /// Controls read the sizes as dynamic resources, so merging after the theme loaded still reaches them.
    /// The returned handle removes the merge again, which only tests need.
    /// </summary>
    public static IDisposable Apply(Application app, UiDensity density)
    {
        if (density != UiDensity.Couch) return NoMerge.Instance;

        var couch = new ResourceInclude(CouchUri) { Source = CouchUri };
        app.Resources.MergedDictionaries.Add(couch);
        return new Merge(app, couch);
    }

    private sealed class Merge(Application app, ResourceInclude couch) : IDisposable
    {
        public void Dispose() => app.Resources.MergedDictionaries.Remove(couch);
    }

    private sealed class NoMerge : IDisposable
    {
        public static readonly NoMerge Instance = new();
        public void Dispose() { }
    }
}
