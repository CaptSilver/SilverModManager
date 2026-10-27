namespace SilverModManager.Abstractions.Games.Merge.Tests.Fixtures;

/// <summary>The winner is listed above the loser, so moving a mod up is what makes it win.</summary>
internal sealed class HigherWinsGame : TestMergeGame
{
    public override bool WinnerIsLower => false;
}
