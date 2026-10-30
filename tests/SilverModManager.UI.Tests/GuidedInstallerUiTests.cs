using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Games.FOMOD.UI;

namespace SilverModManager.UI.Tests;

public class GuidedInstallerUiTests
{
    [Fact]
    public void TheWindowInstaller_SupportsUserInteraction()
    {
        using var installer = new GuidedInstallerUi(new ServiceCollection().BuildServiceProvider());

        installer.SupportsUserInteraction.Should().BeTrue();
    }

    [Fact]
    public void TheWindowViewModels_CarryANoticeAndAnnounceChangingIt()
    {
        var window = new GuidedInstallerWindowViewModel();
        var changed = new List<string?>();
        window.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        window.Notice.Should().BeNull();
        window.Notice = "Saved choices don't match";

        window.Notice.Should().Be("Saved choices don't match");
        changed.Should().Contain(nameof(IGuidedInstallerWindowViewModel.Notice));
    }

    [Fact]
    public async Task RequestUserChoice_BeforeTheWindowIsSetUp_NamesTheMissingSetup()
    {
        using var installer = new GuidedInstallerUi(new ServiceCollection().BuildServiceProvider());

        var act = async () => await installer.RequestUserChoice(
            new SilverModManager.Abstractions.GuidedInstallers.GuidedInstallationStep
            {
                Id = SilverModManager.Abstractions.GuidedInstallers.ValueObjects.StepId.From(Guid.NewGuid()),
                Name = "Step",
                HasNextStep = false,
                HasPreviousStep = false,
                Groups = [],
            },
            SilverModManager.Sdk.Jobs.Percent.Zero,
            CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*SetupInstaller*");
    }
}
