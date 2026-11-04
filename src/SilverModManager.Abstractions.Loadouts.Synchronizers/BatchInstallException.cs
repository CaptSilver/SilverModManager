using JetBrains.Annotations;
using SilverModManager.Sdk.Errors;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers;

/// <summary>
/// Thrown when one item of a batch install fails. The whole batch is dropped, so the message names the item to
/// leave out when trying again. The reason's message is kept as it is when it is a refusal and escaped otherwise.
/// </summary>
[PublicAPI]
public class BatchInstallException(string itemName, Exception reason)
    : UserFacingException($"Couldn't install {MarkdownText.Code(itemName)}, so nothing was installed: {MarkdownText.Message(reason)}", reason)
{
    /// <summary>The library item that failed.</summary>
    public string ItemName { get; } = itemName;

    /// <inheritdoc />
    public override string Title => "Nothing was installed";
}
