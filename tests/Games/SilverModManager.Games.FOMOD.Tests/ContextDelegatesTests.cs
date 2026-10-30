using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Games.FOMOD.CoreDelegates;
using Xunit;

namespace SilverModManager.Games.FOMOD.Tests;

/// <summary>
/// The FOMOD library declares the existing-data-file delegates as returning a byte array and a string
/// array, never null. The app has no view of the game's Data folder to answer them with, so they refuse
/// by name instead of handing the library a null it would dereference.
/// </summary>
public class ContextDelegatesTests
{
    private static ContextDelegates Delegates => new(NullLogger<ContextDelegates>.Instance);

    [Fact]
    public async Task GetExistingDataFile_RefusesByName()
    {
        var act = () => Delegates.GetExistingDataFile("Data/some.esp");

        (await act.Should().ThrowAsync<NotSupportedException>())
            .WithMessage($"*{nameof(ContextDelegates.GetExistingDataFile)}*");
    }

    [Fact]
    public async Task GetExistingDataFileList_RefusesByName()
    {
        var act = () => Delegates.GetExistingDataFileList("Data", "*.esp", isRecursive: true);

        (await act.Should().ThrowAsync<NotSupportedException>())
            .WithMessage($"*{nameof(ContextDelegates.GetExistingDataFileList)}*");
    }
}
