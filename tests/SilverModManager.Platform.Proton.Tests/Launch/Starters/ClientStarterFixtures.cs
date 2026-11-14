using System.Collections.Immutable;
using CliWrap;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.Starters;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Platform.Proton.Tests.Launch.Starters;

/// <summary>Runs nothing: records the commands and lets a test decide how a started child ends.</summary>
internal sealed class FakeClientRunner : IProcessRunner
{
    public List<Command> Commands { get; } = [];

    /// <summary>What a command started with <c>RunAsync</c> does; by default it exits cleanly at once.</summary>
    public Func<Command, Task<CommandResult>> OnRunAsync { get; set; } = _ => Task.FromResult(Result(0));

    public static CommandResult Result(int exitCode) => new(exitCode, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    public void Run(Command command, bool logOutput = false) => Commands.Add(command);

    public Task<CommandResult> RunAsync(Command command, bool logOutput = false, CancellationToken cancellationToken = default, Action<int>? started = null)
    {
        Commands.Add(command);
        return OnRunAsync(command);
    }

    public Task RunAsync(System.Diagnostics.Process process, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

internal static class ClientRequests
{
    public const string GameName = "Cyberpunk 2077";

    public static readonly StartTiming ZeroTiming = new(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeProvider.System);

    public static GameStartRequest For(StoreClient client, GameStore? store = null, StartTiming? timing = null, string[]? callerArgs = null, string[]? extraArgs = null)
    {
        var game = Substitute.For<IGameData>();
        game.DisplayName.Returns(GameName);
        var folder = FileSystem.Shared.FromUnsanitizedFullPath("/games/cyberpunk");
        var installation = new GameInstallation(new GameLocatorResult
        {
            Game = game,
            Path = folder,
            Store = store ?? GameStore.GOG,
            StoreIdentifier = "1423049311",
            LocatorIds = ImmutableArray<LocatorId>.Empty,
            Locator = Substitute.For<IGameLocator>(),
            StoreClient = client,
        }, GameLocations.Create(ImmutableDictionary<LocationId, AbsolutePath>.Empty.Add(LocationId.Game, folder)));
        var spec = new LaunchSpec(folder / "Cyberpunk2077.exe", extraArgs ?? []);
        return new GameStartRequest(default, installation, spec, callerArgs ?? [], IsPrimaryExe: true, UseShell: false, timing ?? ZeroTiming);
    }

    public static StartTiming Timing(int startTimeoutMs, int pollMs = 5)
        => new(TimeSpan.FromMilliseconds(startTimeoutMs), TimeSpan.FromMilliseconds(startTimeoutMs), TimeSpan.FromMilliseconds(pollMs), TimeSpan.FromMilliseconds(7), TimeSpan.Zero, TimeProvider.System);

    public static RunningProcesses GameRunning { get; } = new([new TrackedProcess(4242, "Cyberpunk2077.")], []);
}
