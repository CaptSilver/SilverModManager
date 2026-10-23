namespace NexusMods.Games.FOMOD;

/// <summary>
/// The choices saved for a FOMOD (for example by a collection author) don't fit the FOMOD that is
/// being installed: a step, group or option they name doesn't exist, a step has no saved choices, or
/// the saved selection breaks the group's type.
/// </summary>
public sealed class SavedChoicesDoNotApplyException : Exception
{
    /// <summary>
    /// The FOMOD step the saved choices failed at.
    /// </summary>
    public string StepName { get; }

    /// <summary>
    /// The group within the step, if the failure is about a group or one of its options.
    /// </summary>
    public string? GroupName { get; }

    /// <summary>
    /// The option within the group, if the failure is about a single option.
    /// </summary>
    public string? OptionName { get; }

    /// <summary>
    /// Where the saved choices failed and why, without the lead-in of <see cref="Exception.Message"/>.
    /// </summary>
    public string Details { get; }

    /// <summary>
    /// Constructor.
    /// </summary>
    public SavedChoicesDoNotApplyException(string stepName, string? groupName, string? optionName, string reason)
        : base($"The saved FOMOD choices don't apply at {CreateDetails(stepName, groupName, optionName, reason)}")
    {
        StepName = stepName;
        GroupName = groupName;
        OptionName = optionName;
        Details = CreateDetails(stepName, groupName, optionName, reason);
    }

    private static string CreateDetails(string stepName, string? groupName, string? optionName, string reason)
    {
        var location = $"step `{stepName}`";
        if (groupName is not null) location += $", group `{groupName}`";
        if (optionName is not null) location += $", option `{optionName}`";
        return $"{location}: {reason}";
    }
}
