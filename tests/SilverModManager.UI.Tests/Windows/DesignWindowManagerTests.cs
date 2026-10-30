using FluentAssertions;
using SilverModManager.App.UI.Windows;

namespace SilverModManager.UI.Tests.Windows;

public class DesignWindowManagerTests
{
    [Fact]
    public void Test_ActiveWindow_ThrowsNamingTheManagerUntilOneIsAssigned()
    {
        var manager = new DesignWindowManager();

        var act = () => manager.ActiveWindow;

        act.Should().Throw<InvalidOperationException>().WithMessage("*DesignWindowManager*ActiveWindow*");
    }
}
