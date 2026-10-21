using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Games.FromSoft.Overlay;
using NexusMods.Paths;
using NexusMods.Platform.Proton.Launch;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using NSubstitute;

namespace NexusMods.Games.FromSoft.Tests.Fixtures;

/// <summary>
/// The made-up bwrap, sandbox probe, user ids, game process and installation lock a launch session runs around, for
/// any FromSoft game: the gate and the run tool run for real and nothing is ever started or mounted.
/// </summary>
public sealed class ModdedLaunchFakes : IDisposable
{
    public FakeExecutableLocator Executables { get; } = new();
    public FakeSandboxProbe Probe { get; } = new();
    public RecordingProcessFactory Processes { get; } = new();
    public IOSInterop OsInterop { get; } = Substitute.For<IOSInterop>();
    public string RuntimeDir { get; } = Directory.CreateTempSubdirectory("silvermod-fromsoft-lock-").FullName;

    public void Register(IServiceCollection services)
    {
        services.Replace(ServiceDescriptor.Singleton<IExecutableLocator>(Executables));
        services.Replace(ServiceDescriptor.Singleton<ISandboxProbe>(Probe));
        services.Replace(ServiceDescriptor.Singleton<IUserIds>(new FixedUserIds(1000, 1001)));
        services.Replace(ServiceDescriptor.Singleton<IGameProcessFactory>(Processes));
        services.Replace(ServiceDescriptor.Singleton<IOSInterop>(OsInterop));
        services.Replace(ServiceDescriptor.Singleton<IInstallationLockFactory>(new TempLockFactory(RuntimeDir)));
        services.Replace(ServiceDescriptor.Singleton<ILaunchSynchronizer, NoSynchronizer>());
        services.Replace(ServiceDescriptor.Singleton<IGameLaunchSession>(provider => provider.GetRequiredService<ActiveLaunchSessions>()));
        services.AddSingleton(provider => new ActiveLaunchSessions(provider));
        services.AddTransient<LaunchSession>();
    }

    public void Dispose() => Directory.Delete(RuntimeDir, recursive: true);
}

public sealed class FakeExecutableLocator : IExecutableLocator
{
    public AbsolutePath? Bwrap { get; set; } = NexusMods.Paths.FileSystem.Shared.FromUnsanitizedFullPath("/fake/bin/bwrap");

    public AbsolutePath? Find(string name) => name == "bwrap" ? Bwrap : null;
}

public sealed class FakeSandboxProbe : ISandboxProbe
{
    public string? Failure { get; set; }
    public int Calls { get; private set; }

    public string? Probe(AbsolutePath bwrap)
    {
        Calls++;
        return Failure;
    }
}

public sealed record FixedUserIds(uint Uid, uint Gid) : IUserIds;

public sealed class TempLockFactory(string runtimeDir) : IInstallationLockFactory
{
    public InstallationLock Create(string store, string storeId) => new(InstallationLock.PathFor(runtimeDir, store, storeId));
}

public sealed class NoSynchronizer : ILaunchSynchronizer
{
    public Task Synchronize(LoadoutId loadoutId) => Task.CompletedTask;
}

/// <summary>Records what it was asked to start and "runs" it by calling <see cref="OnStart"/>.</summary>
public sealed class RecordingProcessFactory : IGameProcessFactory
{
    public List<GameProcessSpec> Started { get; } = [];
    public Action? OnStart { get; set; }

    public IGameProcess Start(GameProcessSpec spec, GameInstallation installation)
    {
        lock (Started) Started.Add(spec);
        OnStart?.Invoke();
        return new FinishedProcess();
    }

    public Task<TrackedProcess?> ShutDownGameAsync(GameInstallation installation, CancellationToken cancellationToken) => Task.FromResult<TrackedProcess?>(null);

    public sealed class FinishedProcess : IGameProcess
    {
        public int Pid => 4242;
        public Task<GameProcessResult> WaitAsync(CancellationToken cancellationToken = default) => Task.FromResult(new GameProcessResult(0, null));
        public void Terminate() { }
        public void Dispose() { }
    }
}
