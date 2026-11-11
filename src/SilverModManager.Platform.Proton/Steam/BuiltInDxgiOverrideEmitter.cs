using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Games;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.Steam;

namespace SilverModManager.Platform.Proton.Steam;

/// <summary>
/// Warns about a Steam game whose launch options force <c>WINEDLLOVERRIDES=dxgi=b</c> while the game runs on DXVK.
/// Proton then loads Wine's dxgi in place of DXVK's, and the game dies in its renderer start-up with
/// "Adapter is not a DXVK adapter".
/// </summary>
/// <remarks>
/// Reads the same <c>localconfig.vdf</c> entry as <see cref="SteamLaunchOptionEmitter"/> and only reports a Steam
/// install whose line can be read. DXVK is Proton's default, so a line that sets <c>PROTON_USE_WINED3D=1</c> is
/// left alone: there Wine's dxgi is what the user asked for. Only a built-in override listed before a native one
/// counts; <c>dxgi=n,b</c> loads a proxy DLL and falls back to DXVK's.
/// </remarks>
public sealed class BuiltInDxgiOverrideEmitter : ILoadoutDiagnosticEmitter
{
    private const string DxgiName = "dxgi";
    private const string WineD3DVariable = "PROTON_USE_WINED3D";

    /// <inheritdoc />
    public IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, CancellationToken cancellationToken)
        => DiagnoseAsync(loadout);

    private static async IAsyncEnumerable<Diagnostic> DiagnoseAsync(Loadout.ReadOnly loadout)
    {
        await Task.Yield();

        var installation = loadout.InstallationInstance;
        if (SteamLaunchOptionWriter.Locate(installation) is not { } game) yield break;

        var read = LocalConfigLaunchOptions.Read(game.Config, game.AppId);
        if (!read.FileReadable || read.Line is null || !ForcesBuiltInDxgi(read.Line)) yield break;

        yield return SteamLaunchDiagnostics.CreateBuiltInDxgiOverride(installation.Game.DisplayName);
    }

    /// <summary>
    /// Whether the launch options make Wine load its own dxgi first while DXVK is in use.
    /// </summary>
    internal static bool ForcesBuiltInDxgi(string launchOptions)
    {
        if (SteamLaunchOptionLine.LeadingEnvironmentValue(launchOptions, WineParser.WineDllOverridesEnvironmentVariableName) is not { } value) return false;
        if (SteamLaunchOptionLine.LeadingEnvironmentValue(launchOptions, WineD3DVariable) is "1") return false;

        // Wine reads the sections in order, and a later section for the same DLL wins.
        WineDllOverride? dxgi = null;
        foreach (var entry in WineParser.ParseEnvironmentVariable(value))
        {
            if (string.Equals(entry.DllName, DxgiName, StringComparison.OrdinalIgnoreCase)) dxgi = entry;
        }

        return dxgi is { OverrideTypes: [WineDllOverrideType.BuiltIn, ..] };
    }
}
