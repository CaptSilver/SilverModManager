using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Library.Listing;
using SilverModManager.Wabbajack.Library;

namespace SilverModManager.Wabbajack.Tests.Library;

public class WabbajackModlistKindRegistrationTests
{
    [Fact]
    public void AddWabbajackRegistersTheKindThatLibraryListUsesForModlists()
    {
        using var provider = new ServiceCollection().AddWabbajack().BuildServiceProvider();

        provider.GetServices<ILibraryItemKind>().Should().ContainSingle(kind => kind is WabbajackModlistKind);
    }
}
