using SilverModManager.Sdk.EventBus;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.Sdk.Library;

namespace SilverModManager.CLI;

/// <summary>
/// Messages passed from the CLI to the main application.
/// </summary>
public static class CliMessages
{
    /// <summary>
    /// A new collection is being added to the app.
    /// </summary>
    public record CollectionAddStarted() : IEventBusMessage;
    
    /// <summary>
    /// A new collection was added to the app.
    /// </summary>
    public record CollectionAddSucceeded(CollectionRevisionMetadata.ReadOnly Revision) : IEventBusMessage;
    
    /// <summary>
    /// A new collection failed to be added to the app.
    /// </summary>
    public record CollectionAddFailed(IFailureReason Reason) : IEventBusMessage;

    /// <summary>
    /// A new mod download was added to the app.
    /// </summary>
    public record ModDownloadStarted() : IEventBusMessage;
    
    /// <summary>
    /// A mod download was successfully completed.
    /// </summary>
    public record ModDownloadSucceeded(LibraryItem.ReadOnly LibraryItem) : IEventBusMessage;
    
    /// <summary>
    /// A mod download failed.
    /// </summary>
    public record ModDownloadFailed(IFailureReason Reason) : IEventBusMessage;
    
    /// <summary>
    /// A protocol registration test was initialized.
    /// </summary>
    public record TestProtocolRegistration(Guid Id) : IEventBusMessage;

    /// <summary>
    /// A package was installed into a loadout from a link.
    /// </summary>
    public record ModInstalled(LibraryItem.ReadOnly Item, string LoadoutName) : IEventBusMessage;
}

public interface IFailureReason { }

public static class FailureReason
{
    public record Unknown(Exception Exception) : IFailureReason;

    public record NotLoggedIn : IFailureReason;
    
    public record AlreadyExists(string Name) : IFailureReason;
    
    public record GameNotManaged(string Game) : IFailureReason;

    /// <summary>
    /// The app declined the request on purpose; <paramref name="Message"/> is what to tell the user.
    /// </summary>
    public record Refused(string Message) : IFailureReason;

    /// <summary>
    /// The link fits more than one managed game and nothing says which is meant.
    /// </summary>
    public record Ambiguous(string Package, IReadOnlyList<string> Games) : IFailureReason;
}
