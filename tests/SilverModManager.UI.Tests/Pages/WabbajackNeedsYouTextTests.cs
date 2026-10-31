using FluentAssertions;
using NexusMods.Hashing.xxHash3;
using SilverModManager.App.UI.Pages.Wabbajack;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Format;

namespace SilverModManager.UI.Tests.Pages;

// Which archives of a modlist the user has to deal with, and the texts that go with that.
public class WabbajackNeedsYouTextTests
{
    private static ArchivePlanRow Plan(ArchiveState state, string name = "a.7z", string? reason = null, string? page = null, DownloadState? source = null)
        => new(new ArchiveEntry(FileIdentity.XxHash64(Hash.From((ulong)name.GetHashCode())), 1_000_000, name, "", source ?? new ManualState(new Uri("https://example.com/a"), "get it")),
            state, reason, page is null ? null : new Uri(page), null);

    [Theory]
    [InlineData(ArchiveState.Browser, null, true)]
    [InlineData(ArchiveState.Manual, null, true)]
    [InlineData(ArchiveState.SignIn, null, true)]
    [InlineData(ArchiveState.Automatic, null, false)]
    [InlineData(ArchiveState.Automatic, "", false)]
    [InlineData(ArchiveState.Automatic, "connection reset", true)]
    [InlineData(ArchiveState.Have, null, false)]
    [InlineData(ArchiveState.GameFile, null, false)]
    [InlineData(ArchiveState.GameFileVersion, null, false)]
    [InlineData(ArchiveState.Unsupported, null, false)]
    [InlineData(ArchiveState.NotNeeded, null, false)]
    public void Needs_IsTheStatesThatWaitForTheUser_AndAnAutomaticOneThatFailed(ArchiveState state, string? reason, bool expected)
        => WabbajackNeedsYou.Needs(Plan(state, reason: reason)).Should().Be(expected);

    [Theory]
    [InlineData(1, "1 archive needs you")]
    [InlineData(2, "2 archives need you")]
    [InlineData(1294, "1294 archives need you")]
    public void Heading_AgreesWithTheCount(int count, string expected)
        => WabbajackNeedsYou.Heading(count).Should().Be(expected);

    [Theory]
    [InlineData(WabbajackArchiveFilter.All, new[] { true, true, true, true, true })]
    [InlineData(WabbajackArchiveFilter.NeedsYou, new[] { false, false, true, false, false })]
    [InlineData(WabbajackArchiveFilter.Missing, new[] { false, true, true, false, true })]
    [InlineData(WabbajackArchiveFilter.InLibrary, new[] { true, false, false, false, false })]
    public void Matches_EachFilterKeepsItsOwnRows(WabbajackArchiveFilter filter, bool[] expected)
    {
        ArchivePlanRow[] rows = [Plan(ArchiveState.Have), Plan(ArchiveState.Automatic), Plan(ArchiveState.Manual), Plan(ArchiveState.GameFile), Plan(ArchiveState.Unsupported)];
        rows.Select(row => WabbajackNeedsYou.Matches(filter, row)).Should().Equal(expected);
    }

    [Fact]
    public void Order_IsBySourceThenName_IgnoringCase()
    {
        var url = new Uri("https://example.com/a");
        WabbajackArchiveRow Row(string name, DownloadState source) => new(Plan(ArchiveState.Manual, name, source: source));
        var rows = new[]
        {
            Row("b.7z", new NexusState("skyrim", 1, 2, "n", "v", "a", false)),
            Row("a.7z", new ManualState(new Uri("https://zeta.example/x"), "p")),
            Row("C.7z", new ManualState(new Uri("https://alpha.example/x"), "p")),
            Row("a.7z", new ManualState(new Uri("https://alpha.example/x"), "p")),
        };

        rows.Order(WabbajackNeedsYou.Order).Select(r => $"{r.Source}/{r.Name}").Should().Equal("alpha.example/a.7z", "alpha.example/C.7z", "Nexus Mods/b.7z", "zeta.example/a.7z");
    }

    [Fact]
    public void CopyLine_IsNamePageAndReason()
        => WabbajackNeedsYou.CopyLine(new WabbajackArchiveRow(Plan(ArchiveState.Manual, "Big Pack.7z", "hash mismatch", "https://example.com/files/1")))
            .Should().Be("Big Pack.7z — https://example.com/files/1 — hash mismatch");

    [Fact]
    public void CopyLine_SaysNoPageWhenThereIsNone_AndFallsBackToTheStateWithoutAReason()
        => WabbajackNeedsYou.CopyLine(new WabbajackArchiveRow(Plan(ArchiveState.Manual, "Big Pack.7z")))
            .Should().Be("Big Pack.7z — no page — Needs a manual download");

    [Fact]
    public void CopyText_IsOneLinePerRow_WithoutATrailingNewline()
        => WabbajackNeedsYou.CopyText([new WabbajackArchiveRow(Plan(ArchiveState.Manual, "a.7z", "r1", "https://e.com/1")), new WabbajackArchiveRow(Plan(ArchiveState.Browser, "b.7z", "r2"))])
            .Should().Be("a.7z — https://e.com/1 — r1\nb.7z — no page — r2");
}
