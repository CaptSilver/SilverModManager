using System.Text;
using CliWrap;
using DynamicData.Kernel;
using JetBrains.Annotations;

namespace SilverModManager.Sdk;

[PublicAPI]
public abstract class ExecutableRuntimeDependency : IRuntimeDependency
{
    /// <inheritdoc/>
    public abstract string DisplayName { get; }
    /// <inheritdoc/>
    public abstract string Description { get; }
    /// <inheritdoc/>
    public abstract Uri Homepage { get; }
    /// <inheritdoc/>
    public RuntimeDependencyType DependencyType => RuntimeDependencyType.Executable;

    /// <summary>
    /// Process factory.
    /// </summary>
    protected readonly IProcessRunner ProcessRunner;

    /// <summary>
    /// Constructor.
    /// </summary>
    protected ExecutableRuntimeDependency(IProcessRunner runner)
    {
        ProcessRunner = runner;
    }

    private Optional<Optional<RuntimeDependencyInformation>> _cachedInformation;

    /// <inheritdoc/>
    public ValueTask<Optional<RuntimeDependencyInformation>> QueryInstallationInformation(CancellationToken cancellationToken)
    {
        if (_cachedInformation.HasValue) return ValueTask.FromResult(_cachedInformation.Value);
        return QueryInstallationInformationImpl(cancellationToken);
    }

    /// <summary>
    /// How long the query command may run before the dependency is reported as unavailable.
    /// Generous, because a cold Flatpak start is slow, but finite: probes run at every start of the app and a
    /// stalled one would otherwise stay around for good.
    /// </summary>
    protected virtual TimeSpan ProbeTimeout => TimeSpan.FromSeconds(10);

    protected abstract Command BuildQueryCommand(PipeTarget outputPipeTarget);

    protected abstract RuntimeDependencyInformation ToInformation(ReadOnlySpan<char> output);

    protected virtual async ValueTask<Optional<RuntimeDependencyInformation>> QueryInstallationInformationImpl(CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        var outputPipeTarget = PipeTarget.ToStringBuilder(sb);
        var command = BuildQueryCommand(outputPipeTarget);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);

        CommandResult result;
        try
        {
            // The runner kills the process when this token is cancelled.
            result = await ProcessRunner.RunAsync(command, cancellationToken: timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Our own timeout, not the caller's cancellation. Not cached: a stalled probe doesn't mean the tool is missing.
            return Optional<RuntimeDependencyInformation>.None;
        }

        if (!result.IsSuccess)
        {
            _cachedInformation = Optional<Optional<RuntimeDependencyInformation>>.None;
            return Optional<RuntimeDependencyInformation>.None;
        }

        var information = ToInformation(sb.ToString().AsSpan());
        _cachedInformation = Optional<Optional<RuntimeDependencyInformation>>.Create(information);
        return information;
    }
}
