using SilverModManager.Sdk.Loadouts.Install;

namespace SilverModManager.App.UI.Pages.Collections;

/// <summary>The words the Mods section shows for the rows of an install.</summary>
public static class ModInstallText
{
    public static string State(ModInstallState state) => state switch
    {
        ModInstallState.Waiting => "waiting",
        ModInstallState.Downloading => "downloading",
        ModInstallState.NeedsUser => "get it yourself",
        ModInstallState.Extracting => "extracting",
        ModInstallState.Installing => "installing",
        ModInstallState.Installed => "installed",
        ModInstallState.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    public static string Count(ModInstallRowState state) => $"{state.Done:N0} of {state.Total:N0} files";

    public static string Header(ModInstallTotals totals)
    {
        var text = $"{totals.Installed:N0} of {totals.Total:N0} mods installed";
        return totals.Failed > 0 ? $"{text} · {totals.Failed:N0} failed" : text;
    }

    /// <summary>The reason a row failed, else its detail, which carries its own leading separator.</summary>
    public static string Tail(ModInstallRowState state)
        => state.State == ModInstallState.Failed ? state.Reason ?? "" : state.Detail ?? "";

    /// <summary>What the row's last column says: the reason alone for a failed row, otherwise the count and its detail.</summary>
    public static string Summary(ModInstallRowState state)
        => state.State == ModInstallState.Failed ? Tail(state) : Count(state) + Tail(state);

    public static bool Matches(ModInstallFilter filter, ModInstallRowState state) => filter switch
    {
        ModInstallFilter.All => true,
        ModInstallFilter.Failed => state.State == ModInstallState.Failed,
        ModInstallFilter.NotInstalled => state.State != ModInstallState.Installed,
        _ => throw new ArgumentOutOfRangeException(nameof(filter), filter, null),
    };
}
