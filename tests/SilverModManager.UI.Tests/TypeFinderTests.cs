using System.Reflection;
using FluentAssertions;
using SilverModManager.Abstractions.Serialization.Attributes;
using SilverModManager.App.UI;
using SilverModManager.App.UI.WorkspaceSystem;

namespace SilverModManager.UI.Tests;

/// <summary>
/// The saved window layout stores each open page's context by its <see cref="JsonNameAttribute"/>, and reading it
/// back only finds types the UI's <see cref="TypeFinder"/> lists. A page left out of that list saves fine and then
/// throws "Unknown type" on the next start, which resets the whole layout.
/// </summary>
public class TypeFinderTests
{
    [Fact]
    public void EveryPageContextWithAJsonNameCanBeReadBack()
    {
        var listed = new TypeFinder().DescendentsOf(typeof(IPageFactoryContext)).ToHashSet();

        var saved = typeof(TypeFinder).Assembly.GetTypes()
            .Where(type => type is { IsAbstract: false, IsInterface: false })
            .Where(type => type.IsAssignableTo(typeof(IPageFactoryContext)))
            .Where(type => type.GetCustomAttribute<JsonNameAttribute>() is not null);

        // Equal rather than BeEmpty: BeEmpty names only the first missing type.
        saved.Where(type => !listed.Contains(type)).Select(type => type.FullName).Order()
            .Should().Equal([], "a page context that is saved with the layout has to be listed in TypeFinder to be read back");
    }
}
