using DynamicData.Kernel;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.Generic;

/// <summary>
/// A hand-written game: <see cref="AGameBase"/> with the identity read from the static members
/// <see cref="IGameData{TSelf}"/> declares for <typeparamref name="TSelf"/>.
/// </summary>
public abstract class AGame<TSelf>(IServiceProvider provider) : AGameBase(provider)
    where TSelf : AGame<TSelf>, IGameData<TSelf>
{
    protected sealed override GameId GameIdValue => TSelf.GameId;
    protected sealed override string DisplayNameValue => TSelf.DisplayName;
    protected sealed override Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameIdValue => TSelf.NexusModsGameId;
}
