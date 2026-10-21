using NexusMods.Abstractions.GuidedInstallers;
using NexusMods.Sdk.Jobs;

namespace NexusMods.Games.FOMOD.CoreDelegates;

/// <summary>
/// Wraps an interactive <see cref="IGuidedInstaller"/> for a FOMOD whose saved choices didn't apply: the
/// user answers the steps themselves, with the saved choices already selected, and sees why they are
/// being asked.
/// </summary>
public sealed class InteractiveFallbackGuidedInstaller : IGuidedInstaller
{
    private readonly IGuidedInstaller _inner;
    private readonly FomodOption[] _saved;
    private readonly string _notice;
    private int _position;

    /// <summary>
    /// Constructor.
    /// </summary>
    /// <param name="inner">Shows the steps to the user.</param>
    /// <param name="saved">The saved choices to select where they still exist.</param>
    /// <param name="notice">Shown with the steps, the reason the user is asked.</param>
    public InteractiveFallbackGuidedInstaller(IGuidedInstaller inner, FomodOption[] saved, string notice)
    {
        _inner = inner;
        _saved = saved;
        _notice = notice;
    }

    /// <inheritdoc/>
    public bool SupportsUserInteraction => _inner.SupportsUserInteraction;

    /// <inheritdoc/>
    public void SetupInstaller(string name) => _inner.SetupInstaller(name, _notice);

    /// <inheritdoc/>
    public void SetupInstaller(string name, string? notice) => _inner.SetupInstaller(name, notice is null ? _notice : $"{_notice}\n{notice}");

    /// <inheritdoc/>
    public void CleanupInstaller() => _inner.CleanupInstaller();

    /// <inheritdoc/>
    public void Dispose() => _inner.Dispose();

    /// <inheritdoc/>
    public Task<UserChoice> RequestUserChoice(GuidedInstallationStep installationStep, Percent progress, CancellationToken cancellationToken)
    {
        // The user can go back, so the position is only a hint for steps that share a name.
        var saved = FindSavedStep(installationStep);
        var shown = saved is null ? installationStep : installationStep with { Groups = installationStep.Groups.Select(g => PreSelect(g, saved)).ToArray() };
        return _inner.RequestUserChoice(shown, progress, cancellationToken);
    }

    private FomodOption? FindSavedStep(GuidedInstallationStep step)
    {
        var position = _position++;
        if (position < _saved.Length && NamesMatch(_saved[position].name, step.Name)) return _saved[position];
        return _saved.FirstOrDefault(s => NamesMatch(s.name, step.Name));
    }

    private static OptionGroup PreSelect(OptionGroup group, FomodOption savedStep)
    {
        var savedGroup = savedStep.groups.FirstOrDefault(g => NamesMatch(g.name, group.Name));
        if (savedGroup is null) return group;

        var matching = group.Options
            .Where(o => o.Type is OptionType.Available or OptionType.PreSelected && savedGroup.choices.Any(c => NamesMatch(c.name, o.Name)))
            .ToArray();
        if (matching.Length == 0) return group;

        // Two selected options in a group that takes one would block the user's Next button.
        var takesOne = group.Type is OptionGroupType.ExactlyOne or OptionGroupType.AtMostOne;
        var chosen = takesOne ? matching.Take(1).ToArray() : matching;

        return group with
        {
            Options = group.Options.Select(o =>
            {
                if (o.Type is not (OptionType.Available or OptionType.PreSelected)) return o;
                if (chosen.Contains(o)) return o with { Type = OptionType.PreSelected };
                return takesOne && o.Type == OptionType.PreSelected ? o with { Type = OptionType.Available } : o;
            }).ToArray(),
        };
    }

    private static bool NamesMatch(string a, string b) =>
        string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}
