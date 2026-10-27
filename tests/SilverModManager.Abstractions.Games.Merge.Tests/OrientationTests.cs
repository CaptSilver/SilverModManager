using System.Reflection;
using FluentAssertions;

namespace SilverModManager.Abstractions.Games.Merge.Tests;

/// <summary>
/// Every load order page shows the mod loaded last at the bottom, and that one wins, so the advice is the same for
/// every game. The page the cards send the user to differs per game and has no safe default.
/// </summary>
public class OrientationTests
{
    [Fact]
    public void TheAdviceMovesTheDroppedModBelowTheKeptOne()
        => AMergeGame.ReorderAdvice("Beta", "Alpha").Should().Be("move Beta below Alpha so it loads after it");

    [Fact]
    public void EveryMergeEmitterNamesItsReorderPage()
        => typeof(AMergeDiagnosticsEmitter<>).GetMethod("ReorderPage", BindingFlags.Instance | BindingFlags.NonPublic)!.IsAbstract.Should().BeTrue();
}
