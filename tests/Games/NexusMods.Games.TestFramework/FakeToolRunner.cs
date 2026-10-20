using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexusMods.Paths;
using NexusMods.Sdk.Games;

namespace NexusMods.Games.TestFramework;

/// <summary>
/// A tool runner that records what it was asked to run instead of starting a program, so no test spawns
/// Wine, Proton or umu.
/// </summary>
public class FakeToolRunner : IToolRunner
{
    /// <summary>
    /// One <see cref="RunWindowsTool"/> call. <see cref="ExeContents"/> is the text of the program as it was
    /// when the call was made, because tools usually extract it to a temporary file they delete afterwards.
    /// </summary>
    public sealed record Call(
        GameInstallation Installation,
        AbsolutePath Exe,
        string[] Args,
        IReadOnlyDictionary<string, string>? Env,
        bool LogOutput,
        string? ExeContents,
        ToolRunVia Via = ToolRunVia.Auto);

    private readonly List<Call> _calls = [];

    /// <summary>
    /// Every <see cref="RunWindowsTool"/> call so far, in order.
    /// </summary>
    public IReadOnlyList<Call> Calls
    {
        get { lock (_calls) return _calls.ToArray(); }
    }

    /// <summary>
    /// Whether <see cref="CanRun(GameInstallation, out string?)"/> succeeds.
    /// </summary>
    public bool CanRunTools { get; set; } = true;

    /// <summary>
    /// The reason reported when <see cref="CanRunTools"/> is false.
    /// </summary>
    public string Diagnostic { get; set; } = "The fake tool runner was told it cannot run programs.";

    /// <summary>
    /// The exit code every run reports.
    /// </summary>
    public int ExitCode { get; set; }

    /// <inheritdoc />
    public ToolRunPlan Plan(GameInstallation installation, AbsolutePath exe, IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? env, ToolRunVia via = ToolRunVia.Auto)
        => CanRunTools
            ? ToolRunPlan.Runnable("fake", [exe.ToString(), ..args], exe.Parent, env ?? new Dictionary<string, string>())
            : ToolRunPlan.Failed(Diagnostic);

    /// <inheritdoc />
    public async Task<ToolRunResult> RunWindowsTool(
        GameInstallation installation,
        AbsolutePath exe,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? env,
        bool logOutput,
        CancellationToken cancellationToken,
        ToolRunVia via = ToolRunVia.Auto)
    {
        if (!CanRunTools) throw new InvalidOperationException(Diagnostic);

        var contents = exe.FileExists ? await exe.ReadAllTextAsync(cancellationToken) : null;
        lock (_calls) _calls.Add(new Call(installation, exe, args.ToArray(), env, logOutput, contents, via));
        return new ToolRunResult(ExitCode);
    }

    /// <inheritdoc />
    public bool CanRun(GameInstallation installation, [NotNullWhen(false)] out string? diagnostic)
    {
        diagnostic = CanRunTools ? null : Diagnostic;
        return CanRunTools;
    }
}

public static class FakeToolRunnerExtensions
{
    /// <summary>
    /// Registers a <see cref="FakeToolRunner"/> as the tool runner and a <see cref="FakePrefixResolver"/> as
    /// the prefix resolver, unless they are already registered. The fakes themselves are registered too, so
    /// tests can configure what they report.
    /// </summary>
    public static IServiceCollection AddFakeToolRunner(this IServiceCollection services)
    {
        services.TryAddSingleton<FakeToolRunner>();
        services.TryAddSingleton<IToolRunner>(s => s.GetRequiredService<FakeToolRunner>());
        services.TryAddSingleton<FakePrefixResolver>();
        services.TryAddSingleton<IPrefixResolver>(s => s.GetRequiredService<FakePrefixResolver>());
        return services;
    }
}
