using System.Reflection;
using FluentAssertions;

namespace SilverModManager.Abstractions.Games.Merge.Tests;

/// <summary>
/// Which end of a game's list wins, and which page the cards send the user to, have no safe default: a wrong guess
/// tells the user to move a mod the wrong way and nothing fails. Each merge game and emitter has to say.
/// </summary>
public class OrientationTests
{
    [Fact]
    public void EveryMergeGameSaysWhichEndOfItsListWins()
        => typeof(AMergeGame).GetProperty(nameof(AMergeGame.WinnerIsLower))!.GetMethod!.IsAbstract.Should().BeTrue();

    [Fact]
    public void EveryMergeEmitterNamesItsReorderPage()
        => typeof(AMergeDiagnosticsEmitter<>).GetMethod("ReorderPage", BindingFlags.Instance | BindingFlags.NonPublic)!.IsAbstract.Should().BeTrue();
}
