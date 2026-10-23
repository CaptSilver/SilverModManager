using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Games.FOMOD.UI;

namespace NexusMods.UI.Tests;

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
}
