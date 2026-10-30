using Bannerlord.LauncherManager.External;
using Bannerlord.LauncherManager.Models;
using FluentAssertions;
using SilverModManager.Games.MountAndBlade2Bannerlord.LauncherManager;
using Xunit;

namespace SilverModManager.Games.MountAndBlade2Bannerlord.Tests;

/// <summary>
/// The LauncherManager library reads the launcher state only while ordering a load order
/// (<c>TryOrderByLoadOrder</c> and the import that calls it), which the app never does.
/// Handing it null there would be a NullReferenceException deep in the library.
/// </summary>
public class LauncherStateProviderTests(LauncherManagerFactory factory)
{
    [Fact]
    public void GetState_RefusesByNameInsteadOfReturningNull()
    {
        ILauncherStateProvider provider = factory.Get("/nonexistent/bannerlord", GameStore.Steam);

        var act = () => provider.GetState();

        act.Should().Throw<NotSupportedException>()
            .WithMessage($"*{nameof(ILauncherStateProvider.GetState)}*");
    }
}
