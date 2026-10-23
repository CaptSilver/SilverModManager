using FluentAssertions;
using SilverModManager.Abstractions.GuidedInstallers;
using SilverModManager.Abstractions.GuidedInstallers.ValueObjects;
using SilverModManager.Games.FOMOD.CoreDelegates;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk.Jobs;
using Xunit;

namespace SilverModManager.Games.FOMOD.Tests;

public class InteractiveFallbackGuidedInstallerTests
{
    private static GuidedInstallationStep Step(string name, string groupName, OptionGroupType type, params (string Name, OptionType Type)[] options) => new()
    {
        Id = StepId.From(Guid.NewGuid()),
        Name = name,
        Groups =
        [
            new OptionGroup
            {
                Id = GroupId.From(Guid.NewGuid()),
                Name = groupName,
                Type = type,
                Options = options.Select(o => new Option { Id = OptionId.From(Guid.NewGuid()), Name = o.Name, Type = o.Type }).ToArray(),
            },
        ],
    };

    private static FomodOption Saved(string name, string groupName, params string[] optionNames) => new()
    {
        name = name,
        groups = [new FomodGroup { name = groupName, choices = optionNames.Select(n => new FomodChoice { name = n }).ToArray() }],
    };

    private static async Task<GuidedInstallationStep> Shown(FomodOption[] saved, params GuidedInstallationStep[] steps)
    {
        var state = new ScriptedGuidedInstallerState();
        var installer = new InteractiveFallbackGuidedInstaller(state.Create(), saved, "notice");
        installer.SetupInstaller("mod");
        _ = await installer.RequestUserChoice(steps[0], Percent.Zero, CancellationToken.None);
        return state.Prompts.Single().Steps.Single();
    }

    [Fact]
    public async Task SavedOptions_BecomePreSelected_WhileRequiredAndDisabledOnesStay()
    {
        var step = Step("Step", "Group", OptionGroupType.Any,
            ("A", OptionType.Available), ("B", OptionType.Available), ("C", OptionType.Required), ("D", OptionType.Disabled));

        var shown = await Shown([Saved("Step", "Group", "A", "C", "D")], step);

        shown.Groups.Single().Options.Select(o => o.Type).Should().Equal(OptionType.PreSelected, OptionType.Available, OptionType.Required, OptionType.Disabled);
    }

    [Fact]
    public async Task InAGroupThatTakesOneOption_TheSavedOptionReplacesTheRecommendedOne()
    {
        var step = Step("Step", "Group", OptionGroupType.ExactlyOne, ("A", OptionType.PreSelected), ("B", OptionType.Available));

        var shown = await Shown([Saved("Step", "Group", "B")], step);

        shown.Groups.Single().Options.Select(o => o.Type).Should().Equal(OptionType.Available, OptionType.PreSelected);
    }

    [Fact]
    public async Task ASavedOptionThatIsGone_IsIgnoredAndTheRecommendationStays()
    {
        var step = Step("Step", "Group", OptionGroupType.ExactlyOne, ("A", OptionType.PreSelected), ("B", OptionType.Available));

        var shown = await Shown([Saved("Step", "Group", "Gone")], step);

        shown.Groups.Single().Options.Select(o => o.Type).Should().Equal(OptionType.PreSelected, OptionType.Available);
    }

    [Fact]
    public async Task AStepWithNoSavedChoices_IsShownUntouched()
    {
        var step = Step("Other step", "Group", OptionGroupType.Any, ("A", OptionType.Available));

        var shown = await Shown([Saved("Step", "Group", "A")], step);

        shown.Groups.Single().Options.Single().Type.Should().Be(OptionType.Available);
    }

    [Fact]
    public void SetupInstaller_PassesTheNoticeOn_AndInteractionFollowsTheWrappedInstaller()
    {
        var state = new ScriptedGuidedInstallerState();
        var installer = new InteractiveFallbackGuidedInstaller(state.Create(), [], "why we ask");

        installer.SupportsUserInteraction.Should().BeTrue();
        installer.SetupInstaller("mod");

        state.Prompts.Single().Notice.Should().Be("why we ask");

        state.SupportsUserInteraction = false;
        installer.SupportsUserInteraction.Should().BeFalse();
    }
}
