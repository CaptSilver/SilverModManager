using FluentAssertions;
using NexusMods.Games.FromSoft.Overlay;
using NexusMods.Paths;

namespace NexusMods.Games.FromSoft.Tests.Overlay;

public class UpperdirAuditTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("silvermod-audit-").FullName;
    private readonly OverlayLayout _layout;

    public UpperdirAuditTests()
    {
        var lower = FileSystem.Shared.FromUnsanitizedFullPath(Path.Combine(_folder, "Game"));
        var root = FileSystem.Shared.FromUnsanitizedFullPath(Path.Combine(_folder, "overlay"));
        _layout = new OverlayLayout(lower, root);
        lower.CreateDirectory();
        _layout.Upper.CreateDirectory();
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private void Write(AbsolutePath folder, string relative, string content = "x")
    {
        var path = Path.Combine(folder.GetFullPath(), relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private void Upper(string relative, string content = "x") => Write(_layout.Upper, relative, content);
    private void Lower(string relative, string content = "x") => Write(_layout.Lower, relative, content);

    [Fact]
    public async Task TheRuntimeFilesTheGameAndItsModsWriteAreAllowed()
    {
        Upper("mods/x/config.ini");
        Upper("SeamlessCoop/crashdumps/a.dmp");
        Upper("eldenring.log");
        Upper("sub/vkd3d-proton.cache.1");
        Upper("shaders.dxvk-cache");
        Upper("GraphicsConfig.ini");

        (await UpperdirAudit.Run(_layout)).Should().BeEmpty();
    }

    [Fact]
    public async Task AFileThatIsNeitherAllowedNorShadowingIsAFinding()
    {
        Upper("stray.txt");

        (await UpperdirAudit.Run(_layout)).Should().Equal(new AuditFinding(AuditFindingKind.Unexpected, "stray.txt"));
    }

    [Fact]
    public async Task AFileThatAlsoExistsInTheLowerFolderShadowsAGameFileEvenWithAnAllowedName()
    {
        Lower("DLC.bdt");
        Upper("DLC.bdt");
        Lower("mods/AdjustTheFov/config.ini");
        Upper("mods/AdjustTheFov/config.ini", "changed");

        var findings = await UpperdirAudit.Run(_layout);

        findings.Should().Equal(
            new AuditFinding(AuditFindingKind.ShadowedGameFile, "DLC.bdt"),
            new AuditFinding(AuditFindingKind.ShadowedGameFile, "mods/AdjustTheFov/config.ini"));
    }

    [Fact]
    public async Task APathThePreparationWroteWithTheBytesItWroteIsExemptEvenWhenTheLowerFolderHasItToo()
    {
        Lower("SeamlessCoop/ersc_settings.ini", "template");
        Upper("SeamlessCoop/ersc_settings.ini", "template with password");
        await RecordWritten("SeamlessCoop/ersc_settings.ini");

        (await UpperdirAudit.Run(_layout)).Should().BeEmpty();
    }

    [Fact]
    public async Task AWrittenFileWhoseBytesChangedFallsBackToTheOrdinaryRules()
    {
        Lower("SeamlessCoop/ersc_settings.ini", "template");
        Upper("SeamlessCoop/ersc_settings.ini", "template with password");
        await RecordWritten("SeamlessCoop/ersc_settings.ini");
        Upper("SeamlessCoop/ersc_settings.ini", "edited by the game");

        (await UpperdirAudit.Run(_layout)).Should().Equal(new AuditFinding(AuditFindingKind.ShadowedGameFile, "SeamlessCoop/ersc_settings.ini"));
    }

    [Fact]
    public async Task FindingsAreKeptUntilDiscardedAndSurviveANewLayoutObject()
    {
        Upper("stray.txt");
        await UpperdirAudit.Run(_layout);

        UpperdirAudit.PendingFindings(new OverlayLayout(_layout.Lower, _layout.Root)).Should().ContainSingle().Which.Path.Should().Be("stray.txt");
    }

    [Fact]
    public async Task DiscardDeletesOnlyThatFindingFromTheUpperFolderAndLeavesTheLowerFolderAlone()
    {
        Lower("DLC.bdt");
        Upper("DLC.bdt");
        Upper("stray.txt");
        Upper("mods/x/config.ini");
        var findings = await UpperdirAudit.Run(_layout);

        UpperdirAudit.Discard(_layout, findings.Single(f => f.Path == "stray.txt"));

        File.Exists(Path.Combine(_layout.Upper.GetFullPath(), "stray.txt")).Should().BeFalse();
        File.Exists(Path.Combine(_layout.Upper.GetFullPath(), "DLC.bdt")).Should().BeTrue();
        File.Exists(Path.Combine(_layout.Upper.GetFullPath(), "mods/x/config.ini")).Should().BeTrue();
        File.Exists(Path.Combine(_layout.Lower.GetFullPath(), "DLC.bdt")).Should().BeTrue();
        UpperdirAudit.PendingFindings(_layout).Should().Equal(new AuditFinding(AuditFindingKind.ShadowedGameFile, "DLC.bdt"));
    }

    [Fact]
    public async Task DiscardRefusesAPathThatLeavesTheUpperFolder()
    {
        Lower("DLC.bdt");

        var act = () => UpperdirAudit.Discard(_layout, new AuditFinding(AuditFindingKind.Unexpected, "../../Game/DLC.bdt"));

        act.Should().Throw<InvalidOperationException>();
        File.Exists(Path.Combine(_layout.Lower.GetFullPath(), "DLC.bdt")).Should().BeTrue();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ARunKeepsTheGameFolderFindingsOfEarlierAndReplacesTheUpperFolderOnes()
    {
        Upper("stray.txt");
        await UpperdirAudit.Run(_layout);
        UpperdirAudit.RecordGameFolderChanges(_layout, [new GameFolderSnapshot.Difference("regulation.bin", "changed")]);
        File.Delete(Path.Combine(_layout.Upper.GetFullPath(), "stray.txt"));

        await UpperdirAudit.Run(_layout);

        UpperdirAudit.PendingFindings(_layout).Should().Equal(new AuditFinding(AuditFindingKind.GameFolderChanged, "regulation.bin", "changed"));
    }

    [Fact]
    public async Task AnAuditWithNoUpperFolderFindsNothing()
    {
        Directory.Delete(_layout.Upper.GetFullPath());

        (await UpperdirAudit.Run(_layout)).Should().BeEmpty();
    }

    [Fact]
    public async Task TheSnapshotSeesAddedRemovedAndChangedFilesButNotArchives()
    {
        Lower("eldenring.exe", "exe");
        Lower("regulation.bin", "reg");
        Lower("Data0.bdt", "archive");
        var before = await GameFolderSnapshot.Take(_layout.Lower);

        Lower("new.dll", "dll");
        Lower("regulation.bin", "REG");
        File.Delete(Path.Combine(_layout.Lower.GetFullPath(), "eldenring.exe"));
        Lower("Data0.bdt", "archive changed");
        var after = await GameFolderSnapshot.Take(_layout.Lower);

        before.Entries.Select(e => e.Path).Should().Equal("eldenring.exe", "regulation.bin");
        before.Compare(after).Should().Equal(
            new GameFolderSnapshot.Difference("eldenring.exe", "removed"),
            new GameFolderSnapshot.Difference("new.dll", "added"),
            new GameFolderSnapshot.Difference("regulation.bin", "changed"));
    }

    [Fact]
    public async Task TwoSnapshotsOfAnUntouchedFolderAreEqual()
    {
        Lower("eldenring.exe", "exe");
        File.CreateSymbolicLink(Path.Combine(_layout.Lower.GetFullPath(), "dxgi.dll"), "/nonexistent/ReShade64.dll");

        var first = await GameFolderSnapshot.Take(_layout.Lower);
        var second = await GameFolderSnapshot.Take(_layout.Lower);

        first.Compare(second).Should().BeEmpty();
        first.Entries.Should().Contain(e => e.Path == "dxgi.dll" && e.Hash == "link:/nonexistent/ReShade64.dll");
    }

    private async Task RecordWritten(string relative)
    {
        var hash = await UpperdirAudit.HashOf(_layout, Path.Combine(_layout.Upper.GetFullPath(), relative), CancellationToken.None);
        new OverlaySessionState(new Dictionary<string, string> { [relative] = hash }, new GameFolderSnapshot([])).Save(_layout);
    }
}
