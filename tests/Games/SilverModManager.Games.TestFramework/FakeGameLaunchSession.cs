using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexusMods.Abstractions.Games;
using NexusMods.Sdk.Loadouts;
using NexusMods.Sdk.Games;

namespace NexusMods.Games.TestFramework;

/// <summary>
/// A launch session registry whose answer the test chooses, so no test needs a real launch lock.
/// </summary>
public class FakeGameLaunchSession : IGameLaunchSession
{
    /// <summary>
    /// What <see cref="IsRunning"/> returns for every installation; nothing is running by default.
    /// </summary>
    public bool Running { get; set; }

    /// <summary>
    /// What the launches this fake hands out did, in order: <c>lock</c>, <c>running</c>, <c>finish</c>, <c>release</c>.
    /// </summary>
    public List<string> Events { get; } = [];

    /// <summary>
    /// What <see cref="IGameLaunch.WrapperActive"/> answers; no wrapper by default.
    /// </summary>
    public Func<bool> WrapperActive { get; set; } = () => false;

    /// <summary>
    /// Thrown from <see cref="IGameLaunch.RunAsync"/> before the callback runs, to play a refused launch.
    /// </summary>
    public GameLaunchException? Refusal { get; set; }

    /// <summary>
    /// The run specs the sessions were begun with, in order.
    /// </summary>
    public List<LaunchSpec?> BegunWith { get; } = [];

    /// <inheritdoc />
    public bool IsRunning(GameInstallation installation) => Running;

    /// <inheritdoc />
    public IGameLaunch BeginSession(Loadout.ReadOnly loadout, LaunchSpec? runSpec = null)
    {
        lock (BegunWith) BegunWith.Add(runSpec);
        return new FakeGameLaunch(this);
    }

    private sealed class FakeGameLaunch(FakeGameLaunchSession owner) : IGameLaunch
    {
        public bool WrapperActive => owner.WrapperActive();

        public async Task RunAsync(Func<CancellationToken, Task> whileRunning, CancellationToken cancellationToken)
        {
            lock (owner.Events) owner.Events.Add("lock");
            if (owner.Refusal is { } refusal) throw refusal;

            lock (owner.Events) owner.Events.Add("running");
            try
            {
                await whileRunning(cancellationToken);
            }
            finally
            {
                lock (owner.Events) owner.Events.Add("finish");
            }
        }

        public void Dispose()
        {
            lock (owner.Events) owner.Events.Add("release");
        }
    }
}

public static class FakeGameLaunchSessionExtensions
{
    /// <summary>
    /// Registers a <see cref="FakeGameLaunchSession"/> as the launch session registry, unless one is already
    /// registered. The fake itself is registered too, so tests can switch it on.
    /// </summary>
    public static IServiceCollection AddFakeGameLaunchSession(this IServiceCollection services)
    {
        services.TryAddSingleton<FakeGameLaunchSession>();
        services.TryAddSingleton<IGameLaunchSession>(s => s.GetRequiredService<FakeGameLaunchSession>());
        return services;
    }
}
