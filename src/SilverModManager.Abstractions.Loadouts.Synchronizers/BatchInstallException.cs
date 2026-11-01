using JetBrains.Annotations;
using SilverModManager.Sdk.Errors;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers;

/// <summary>
/// Thrown when one item of a batch install fails. The whole batch is dropped, so the message names the item to
/// leave out when trying again.
/// </summary>
[PublicAPI]
public class BatchInstallException(string itemName, Exception reason)
    : UserFacingException($"Couldn't install `{itemName}`, so nothing was installed: {reason.Message}", reason)
{
    /// <summary>The library item that failed.</summary>
    public string ItemName { get; } = itemName;

    /// <inheritdoc />
    public override string Title => "Nothing was installed";
}
