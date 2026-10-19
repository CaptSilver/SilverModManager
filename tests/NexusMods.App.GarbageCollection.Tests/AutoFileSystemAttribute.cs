using AutoFixture;
using AutoFixture.Xunit3;

namespace NexusMods.Paths.TestingHelpers;

/// <summary>
/// xunit v3 replacement for the attribute of the same name in NexusMods.Paths.TestingHelpers,
/// which is built against AutoFixture.Xunit2.
/// </summary>
public class AutoFileSystemAttribute(bool useSharedFileSystem = true) : AutoDataAttribute(() => CreateFixture(useSharedFileSystem))
{
    private static IFixture CreateFixture(bool useSharedFileSystem)
    {
        var fixture = new Fixture();
        fixture.AddFileSystemCustomizations(useSharedFileSystem);
        return fixture;
    }
}
