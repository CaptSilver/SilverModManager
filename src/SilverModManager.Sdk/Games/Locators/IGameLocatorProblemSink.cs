using JetBrains.Annotations;

namespace NexusMods.Sdk.Games;

/// <summary>
/// Where a locator puts what it could not resolve cleanly during one <see cref="IGameLocator.Locate(IGameLocatorProblemSink)"/>.
/// </summary>
[PublicAPI]
public interface IGameLocatorProblemSink
{
    void Report(string locator, string message);
}
