using FluentAssertions;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.References;
using SilverModManager.App.UI.Controls.Diagnostics;
using SilverModManager.App.UI.Pages.Diagnostics;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>When a refreshed Health Check list keeps the place the user scrolled to, and when it starts at the top.</summary>
public class DiagnosticListScrollTests
{
    private static readonly DiagnosticId Kind = new();

    private static DiagnosticRowKey Row(string title, string summary = "s", DiagnosticId? id = null) => new(id ?? Kind, title, summary);

    [Fact]
    public void Test_TheSameRowsInTheSameOrder_KeepTheOffset()
    {
        DiagnosticListScroll.KeepsOffset([Row("a"), Row("b")], [Row("a"), Row("b")]).Should().BeTrue();
    }

    [Fact]
    public void Test_NoRowsBeforeAndAfter_KeepTheOffset()
    {
        DiagnosticListScroll.KeepsOffset([], []).Should().BeTrue();
    }

    [Fact]
    public void Test_ARowMoreOrFewer_StartsAtTheTop()
    {
        DiagnosticListScroll.KeepsOffset([Row("a")], [Row("a"), Row("b")]).Should().BeFalse();
        DiagnosticListScroll.KeepsOffset([Row("a"), Row("b")], [Row("a")]).Should().BeFalse();
    }

    [Fact]
    public void Test_TheSameRowsInAnotherOrder_StartAtTheTop()
    {
        DiagnosticListScroll.KeepsOffset([Row("a"), Row("b")], [Row("b"), Row("a")]).Should().BeFalse();
    }

    [Fact]
    public void Test_AnIdIsOnlyTheKindOfDiagnostic_SoTheTitleAndSummaryDecideToo()
    {
        DiagnosticListScroll.KeepsOffset([Row("a", "one")], [Row("a", "two")]).Should().BeFalse("another file is another card of the same kind");
        DiagnosticListScroll.KeepsOffset([Row("a")], [Row("b")]).Should().BeFalse();
    }

    [Fact]
    public void Test_TheKeyOfAnEntryIsItsIdTitleAndSummary()
    {
        var entry = new DiagnosticEntryDesignViewModel(new Diagnostic
        {
            Id = Kind,
            Title = "t",
            Severity = DiagnosticSeverity.Warning,
            Summary = DiagnosticMessage.From("sum"),
            Details = DiagnosticMessage.DefaultValue,
            DataReferences = new Dictionary<DataReferenceDescription, IDataReference>(),
        });

        DiagnosticListScroll.KeysOf([entry]).Should().Equal(new DiagnosticRowKey(Kind, "t", entry.Summary));
    }
}
