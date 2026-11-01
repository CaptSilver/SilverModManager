using System.Collections.Immutable;
using System.Globalization;
using JetBrains.Annotations;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk;

namespace SilverModManager.App.UI.Pages.Diagnostics;

/// <summary>
/// Hands Steam the request to verify a game's files. Steam asks before it starts, and what it finds is only visible
/// on the next Health Check, so the diagnostics are not refreshed here.
/// </summary>
[UsedImplicitly]
public sealed class VerifyGameFilesInSteamActionHandler(IOSInterop osInterop) : IDiagnosticActionHandler
{
    /// <inheritdoc />
    public string ActionId => DiagnosticActions.VerifyGameFilesInSteamId;

    /// <inheritdoc />
    public Task ExecuteAsync(LoadoutId loadoutId, ImmutableArray<string> arguments)
    {
        // The id goes into a URL, so only digits get through.
        if (arguments.Length != 1 || !ulong.TryParse(arguments[0], NumberStyles.None, CultureInfo.InvariantCulture, out var appId))
            throw new ArgumentException("Expected the Steam app id.", nameof(arguments));

        osInterop.OpenUri(new Uri($"steam://validate/{appId.ToString(CultureInfo.InvariantCulture)}"));
        return Task.CompletedTask;
    }
}
