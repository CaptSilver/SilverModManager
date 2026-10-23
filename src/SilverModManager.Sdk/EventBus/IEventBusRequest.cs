using JetBrains.Annotations;

namespace SilverModManager.Sdk.EventBus;

/// <summary>
/// Represents a request.
/// </summary>
[PublicAPI]
public interface IEventBusRequest<TResult> : IEventBusMessage
    where TResult : notnull;
