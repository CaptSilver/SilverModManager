namespace SilverModManager.SingleProcess;

/// <summary>
/// A game launch a <c>run</c> process hands to the main process, with the environment and working directory it
/// was started in.
/// </summary>
public sealed record ForwardedLaunch(
    string LoadoutName,
    string? Game,
    IReadOnlyList<string> Command,
    IReadOnlyDictionary<string, string> Environment,
    string? WorkingDirectory);

/// <summary>
/// How a launch the main process ran ended. <paramref name="Message"/> is why the launch itself failed, and null
/// when the game ran and <paramref name="ExitCode"/> is its own.
/// </summary>
public sealed record ForwardedLaunchExit(int ExitCode, string? Message = null);

/// <summary>
/// The main process's answer to a forwarded launch: either a refusal with its reason, or the work to run.
/// </summary>
public sealed class ForwardedLaunchAdmission
{
    private readonly Func<CancellationToken, Task<ForwardedLaunchExit>>? _run;

    private ForwardedLaunchAdmission(string? declineReason, Func<CancellationToken, Task<ForwardedLaunchExit>>? run)
    {
        DeclineReason = declineReason;
        _run = run;
    }

    /// <summary>Why the launch was refused, or null when it was accepted.</summary>
    public string? DeclineReason { get; }

    public bool IsAccepted => _run is not null;

    public static ForwardedLaunchAdmission Declined(string reason) => new(reason, null);

    /// <param name="run">Runs the launch to its end. Its token is cancelled when the launch is to be stopped.</param>
    public static ForwardedLaunchAdmission Accepted(Func<CancellationToken, Task<ForwardedLaunchExit>> run) => new(null, run);

    public Task<ForwardedLaunchExit> RunAsync(CancellationToken stop)
        => _run?.Invoke(stop) ?? throw new InvalidOperationException("A declined launch has nothing to run.");
}

/// <summary>
/// Runs forwarded launches in the main process, which owns the database. Lives behind an interface because the
/// launch session is not something this project can reference.
/// </summary>
public interface IForwardedLaunchHandler
{
    /// <summary>
    /// Decides whether the launch can run now. Nothing may have been started or changed when it is declined.
    /// </summary>
    Task<ForwardedLaunchAdmission> AdmitAsync(ForwardedLaunch launch);
}

/// <summary>
/// What <see cref="CliClient.ForwardLaunchAsync"/> found out.
/// </summary>
public abstract record ForwardedLaunchOutcome
{
    /// <summary>The main process ran the launch; the game's exit code or the launch's failure.</summary>
    public sealed record Exited(int ExitCode, string? Message) : ForwardedLaunchOutcome;

    /// <summary>The main process understood the launch and refused it, for the reason given.</summary>
    public sealed record Declined(string Reason) : ForwardedLaunchOutcome;

    /// <summary>The main process could not be asked: unreachable, or from a build that does not know the request.</summary>
    public sealed record Unavailable(string Reason) : ForwardedLaunchOutcome;
}
