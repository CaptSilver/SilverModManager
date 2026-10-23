using SilverModManager.Abstractions.GuidedInstallers;
using SilverModManager.Sdk.Jobs;

namespace SilverModManager.Games.TestFramework;

/// <summary>
/// Stands in for the guided installer window: records every installation it is asked to run and answers
/// each step by accepting the options that are already selected. Register <see cref="Create"/> as a
/// transient <see cref="IGuidedInstaller"/> so every installation gets an instance of its own, like the
/// real window.
/// </summary>
public sealed class ScriptedGuidedInstallerState
{
    private readonly object _lock = new();
    private readonly List<Prompt> _prompts = [];
    private int _active;
    private int _maxActive;

    /// <summary>
    /// What <see cref="IGuidedInstaller.SupportsUserInteraction"/> answers.
    /// </summary>
    public bool SupportsUserInteraction { get; set; } = true;

    /// <summary>
    /// Answer every step with cancel, like closing the window.
    /// </summary>
    public bool Cancel { get; set; }

    /// <summary>
    /// Awaited with each step before it is answered, to keep a window open.
    /// </summary>
    public Func<Prompt, Task>? BeforeAnswer { get; set; }

    /// <summary>
    /// The installations that were set up, in order.
    /// </summary>
    public Prompt[] Prompts
    {
        get { lock (_lock) return _prompts.ToArray(); }
    }

    /// <summary>
    /// The most installations that were set up and not yet cleaned up at the same time.
    /// </summary>
    public int MaxConcurrentPrompts => Volatile.Read(ref _maxActive);

    public IGuidedInstaller Create() => new Installer(this);

    /// <summary>
    /// One installation: the name and notice it was set up with, and the steps it was asked.
    /// </summary>
    public sealed class Prompt(string name, string? notice)
    {
        public string Name { get; } = name;
        public string? Notice { get; } = notice;
        private readonly List<GuidedInstallationStep> _steps = [];
        public GuidedInstallationStep[] Steps
        {
            get { lock (_steps) return _steps.ToArray(); }
        }

        internal void Add(GuidedInstallationStep step)
        {
            lock (_steps) _steps.Add(step);
        }
    }

    private sealed class Installer(ScriptedGuidedInstallerState state) : IGuidedInstaller
    {
        private Prompt? _prompt;
        private int _cleanedUp;

        public bool SupportsUserInteraction => state.SupportsUserInteraction;

        public void SetupInstaller(string name) => SetupInstaller(name, null);

        public void SetupInstaller(string name, string? notice)
        {
            var prompt = new Prompt(name, notice);
            lock (state._lock) state._prompts.Add(prompt);
            _prompt = prompt;

            var active = Interlocked.Increment(ref state._active);
            int seen;
            while (active > (seen = Volatile.Read(ref state._maxActive)))
                Interlocked.CompareExchange(ref state._maxActive, active, seen);
        }

        public void CleanupInstaller()
        {
            // The FOMOD library and the caller can both end an installation.
            if (_prompt is null || Interlocked.Exchange(ref _cleanedUp, 1) == 1) return;
            Interlocked.Decrement(ref state._active);
        }

        public async Task<UserChoice> RequestUserChoice(GuidedInstallationStep installationStep, Percent progress, CancellationToken cancellationToken)
        {
            var prompt = _prompt ?? throw new InvalidOperationException("Asked a step before the installer was set up");
            prompt.Add(installationStep);
            if (state.BeforeAnswer is { } before) await before(prompt);

            if (state.Cancel) return new UserChoice(new UserChoice.CancelInstallation());

            var selected = installationStep.Groups
                .SelectMany(g => g.Options
                    .Where(o => o.Type is OptionType.PreSelected or OptionType.Required)
                    .Select(o => new SelectedOption(g.Id, o.Id)))
                .ToArray();
            return new UserChoice(new UserChoice.GoToNextStep(selected));
        }

        public void Dispose() => CleanupInstaller();
    }
}
