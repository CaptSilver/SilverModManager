using FluentAssertions;
using SilverModManager.Abstractions.GuidedInstallers;
using SilverModManager.Abstractions.GuidedInstallers.ValueObjects;
using SilverModManager.Games.FOMOD.CoreDelegates;
using SilverModManager.Sdk.Jobs;
using Xunit;

namespace SilverModManager.Games.FOMOD.Tests;

public class PresetGuidedInstallerTests
{
    private static GuidedInstallationStep Step(string name, string groupName, params string[] optionNames) => new()
    {
        Id = StepId.From(Guid.NewGuid()),
        Name = name,
        Groups =
        [
            new OptionGroup
            {
                Id = GroupId.From(Guid.NewGuid()),
                Name = groupName,
                Type = OptionGroupType.Any,
                Options = optionNames.Select(n => new Option
                {
                    Id = OptionId.From(Guid.NewGuid()),
                    Name = n,
                    Type = OptionType.Available,
                }).ToArray(),
            },
        ],
    };

    private static FomodOption Saved(string name, string groupName, string optionName) => new()
    {
        name = name,
        groups = [new FomodGroup { name = groupName, choices = [new FomodChoice { name = optionName }] }],
    };

    [Fact]
    public async Task StepsSharingAName_AreAnsweredInOrder()
    {
        // FOMOD authors reuse titles like "Options" for several steps, so a name alone can't tell them apart.
        var first = Step("Options", "Body", "Slim", "Curvy");
        var second = Step("Options", "Textures", "2K", "4K");
        var installer = new PresetGuidedInstaller([Saved("Options", "Body", "Curvy"), Saved("Options", "Textures", "4K")]);

        var firstChoice = await installer.RequestUserChoice(first, Percent.Zero, CancellationToken.None);
        var secondChoice = await installer.RequestUserChoice(second, Percent.Zero, CancellationToken.None);

        firstChoice.AsT2.SelectedOptions.Should().Equal(new SelectedOption(first.Groups[0].Id, first.Groups[0].Options[1].Id));
        secondChoice.AsT2.SelectedOptions.Should().Equal(new SelectedOption(second.Groups[0].Id, second.Groups[0].Options[1].Id));
    }
}
