namespace SilverModManager.App.UI.Controls.DragAndDrop;

/// <summary>"1 mod" / "3 mods", the count text used in drop captions and toasts.</summary>
internal static class ModCount
{
    public static string Text(int count) => count == 1 ? "1 mod" : $"{count} mods";
}
