using SilverModManager.Abstractions.Loadouts.Synchronizers;

namespace SilverModManager.Games.Unreal;

/// <summary>The synchronizer of every <see cref="UnrealGame"/>; the defaults are the behaviour.</summary>
public sealed class UnrealGameSynchronizer(IServiceProvider provider) : ALoadoutSynchronizer(provider);
