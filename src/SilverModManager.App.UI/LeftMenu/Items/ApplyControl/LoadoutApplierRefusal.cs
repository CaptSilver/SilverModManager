using SilverModManager.UI.Sdk.Dialog;

namespace SilverModManager.App.UI.LeftMenu.Items;

/// <summary>
/// The dialog <see cref="LoadoutApplier"/> shows when the synchronizer refuses to apply a loadout.
/// </summary>
internal static class LoadoutApplierRefusal
{
    public const string Title = "The loadout was not applied";

    /// <summary>
    /// The second button beside OK. The Health Check holds the diagnostic that carries the choices for fixing
    /// the refusal, so the dialog points there instead of repeating them.
    /// </summary>
    public static readonly ButtonDefinitionId OpenHealthCheck = ButtonDefinitionId.From("openHealthCheck");
}
