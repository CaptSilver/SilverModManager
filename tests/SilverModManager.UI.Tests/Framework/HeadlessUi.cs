namespace SilverModManager.UI.Tests.Framework;

/// <summary>
/// Name of the xunit collection for tests that show windows and drive keyboard, pointer or drag input. The
/// headless platform has a single keyboard focus and pointer, so two such classes running at once press and
/// move each other's mouse. A collection that disables parallelization runs after the parallel ones, one class at a time.
/// </summary>
public static class HeadlessUi
{
    public const string Collection = "Headless UI";

    /// <summary>
    /// Name of the xunit collection for classes that change the appearance (Glass) setting. It is one store for the
    /// whole run, so two such classes in parallel reset it under each other.
    /// </summary>
    public const string GlassSettingsCollection = "Glass settings";
}

[CollectionDefinition(HeadlessUi.Collection, DisableParallelization = true)]
public class HeadlessUiCollection;
