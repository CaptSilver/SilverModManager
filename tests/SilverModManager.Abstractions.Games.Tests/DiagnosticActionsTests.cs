using System.Collections.Immutable;
using FluentAssertions;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Games.Tests;

public class DiagnosticActionsTests
{
    [Fact]
    public void RemoveLoadoutFileRoundTripsPathAndGroup()
    {
        var path = new GamePath(LocationId.Game, "Data/SKSE/Plugins/old.dll");
        var group = EntityId.From(0x1234);

        var action = DiagnosticActions.RemoveLoadoutFile(path, group);

        action.Id.Should().Be(DiagnosticActions.RemoveLoadoutFileId);
        var (parsedPath, parsedGroup) = DiagnosticActions.ParseRemoveLoadoutFile(action.Arguments);
        parsedPath.Should().Be(path);
        parsedGroup.Should().Be(LoadoutItemGroupId.From(group));
    }

    [Fact]
    public void RoundTripKeepsANonGameLocation()
    {
        var path = new GamePath(LocationId.From("Saves"), "a b/c.sav");

        var (parsedPath, _) = DiagnosticActions.ParseRemoveLoadoutFile(DiagnosticActions.RemoveLoadoutFile(path, EntityId.From(1)).Arguments);

        parsedPath.Should().Be(path);
    }

    [Fact]
    public void ParsingTheWrongNumberOfArgumentsIsRejected()
    {
        var act = () => DiagnosticActions.ParseRemoveLoadoutFile(["Game"]);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void RemoveLoadoutFilesRoundTrips()
    {
        GamePath[] paths = [new(LocationId.Game, "a.dll"), new(LocationId.From("Saves"), "b c/d.sav")];
        var group = EntityId.From(0x99);

        var action = DiagnosticActions.RemoveLoadoutFiles(paths, group);

        action.Id.Should().Be(DiagnosticActions.RemoveLoadoutFilesId);
        var (parsedPaths, parsedGroup) = DiagnosticActions.ParseRemoveLoadoutFiles(action.Arguments);
        parsedPaths.Should().Equal(paths);
        parsedGroup.Should().Be(LoadoutItemGroupId.From(group));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void ParseRemoveLoadoutFilesRejectsAnOddArgumentCount(int count)
    {
        var arguments = Enumerable.Repeat("1", count).ToImmutableArray();

        var act = () => DiagnosticActions.ParseRemoveLoadoutFiles(arguments);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void DismissDiagnosticSubjectsRoundTrips()
    {
        var id = new DiagnosticId("SilverModManager.Games.Generic", number: 12);
        (string Subject, string? Identity)[] subjects = [("Game|a.dll", "XxHash3:0011"), ("Game|b c/d.dll", null)];

        var action = DiagnosticActions.DismissDiagnosticSubjects(id, "Loader Files", subjects);

        action.Id.Should().Be(DiagnosticActions.DismissDiagnosticSubjectsId);
        action.Label.Should().Be("Ignore");
        var (parsedId, title, parsed) = DiagnosticActions.ParseDismissDiagnosticSubjects(action.Arguments);
        parsedId.Should().Be(id);
        title.Should().Be("Loader Files");
        parsed.Should().Equal(subjects);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(4)]
    public void ParsingDismissArgumentsWithAHalfPairOrNothingIsRejected(int count)
    {
        var arguments = Enumerable.Repeat("x", count).ToImmutableArray();

        var act = () => DiagnosticActions.ParseDismissDiagnosticSubjects(arguments);

        act.Should().Throw<ArgumentException>();
    }
}
