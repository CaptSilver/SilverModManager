using SilverModManager.Abstractions.Loadouts.Synchronizers;

namespace SilverModManager.Games.Generic.Simple;

/// <summary>The synchronizer of every <see cref="SimpleGame"/>; the defaults are the behaviour.</summary>
public sealed class SimpleGameSynchronizer(IServiceProvider provider) : ALoadoutSynchronizer(provider);
