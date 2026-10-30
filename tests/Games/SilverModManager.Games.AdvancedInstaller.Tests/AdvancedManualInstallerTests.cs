using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Library.Installers;
using Xunit;

namespace SilverModManager.Games.AdvancedInstaller.Tests;

public class AdvancedManualInstallerTests(IServiceProvider serviceProvider)
{
    /// <summary>
    /// Without a UI handler registered (CLI runs) the installer is inactive and says so.
    /// </summary>
    [Fact]
    public async Task WithoutAHandler_ExecuteReportsNotSupported()
    {
        var installer = new AdvancedManualInstaller(serviceProvider);

        installer.IsActive.Should().BeFalse();
        var result = await installer.ExecuteAsync(default, null!, null!, default, CancellationToken.None);

        result.IsNotSupported(out var reason).Should().BeTrue();
        reason.Should().Contain("No UI");
    }
}
