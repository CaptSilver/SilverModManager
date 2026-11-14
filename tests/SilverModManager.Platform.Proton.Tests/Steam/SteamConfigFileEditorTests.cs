using System.Text;
using FluentAssertions;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Platform.Proton.Steam;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk;

namespace SilverModManager.Platform.Proton.Tests.Steam;

public class SteamConfigFileEditorTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("silvermod-editor-").FullName;
    private readonly IProcessTracker _tracker = Substitute.For<IProcessTracker>();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string File_ => System.IO.Path.Combine(_root, "config.bin");
    private AbsolutePath Path_ => FileSystem.Shared.FromUnsanitizedFullPath(File_);
    private SteamConfigFileEditor Editor => new(_tracker);

    private static (byte[]?, string?) Append(byte[] bytes, string text) => ([.. bytes, .. Encoding.UTF8.GetBytes(text)], null);

    [Fact]
    public void TheOriginalIsWrittenOnceAndSurvivesASecondEdit()
    {
        File.WriteAllText(File_, "user");

        Editor.Edit(Path_, b => Append(b, "+1"), _ => true);
        File.ReadAllText(File_ + ".silvermod-orig").Should().Be("user");
        File.ReadAllText(File_ + ".silvermod-bak").Should().Be("user");

        var second = Editor.Edit(Path_, b => Append(b, "+2"), _ => true);

        File.ReadAllText(File_).Should().Be("user+1+2");
        File.ReadAllText(File_ + ".silvermod-orig").Should().Be("user");
        File.ReadAllText(File_ + ".silvermod-bak").Should().Be("user+1");
        second.Original!.ToString().Should().Be(File_ + ".silvermod-orig");
        second.Backup!.ToString().Should().Be(File_ + ".silvermod-bak");
    }

    [Fact]
    public void AReadBackMismatchRestoresTheBackup()
    {
        File.WriteAllText(File_, "user");

        var result = Editor.Edit(Path_, b => Append(b, "+1"), _ => false);

        result.Refusal.Should().Contain("did not read back").And.Contain("original was restored");
        File.ReadAllText(File_).Should().Be("user");
    }

    [Fact]
    public void SteamAppearingAfterTheRenameIsReported()
    {
        File.WriteAllText(File_, "user");
        _tracker.IsSteamRunning().Returns(false, true);

        var result = Editor.Edit(Path_, b => Append(b, "+1"), _ => true);

        result.Refusal.Should().BeNull();
        result.SteamStartedDuringWrite.Should().BeTrue();
        result.Note.Should().Be("Steam started while the file was being written, so the change may be lost. Close Steam and try again.");
        File.ReadAllText(File_).Should().Be("user+1");
    }

    [Fact]
    public void AMissingFileIsCreatedOnlyWhenAsked()
    {
        var refused = Editor.Edit(Path_, b => Append(b, "new"), _ => true);
        refused.Refusal.Should().NotBeNull();
        File.Exists(File_).Should().BeFalse();

        byte[]? seen = null;
        var created = Editor.Edit(Path_, b => { seen = b; return Append(b, "new"); }, _ => true, createIfMissing: true);

        created.Refusal.Should().BeNull();
        seen.Should().BeEmpty();
        File.ReadAllText(File_).Should().Be("new");
        File.Exists(File_ + ".silvermod-orig").Should().BeFalse();
    }

    [Fact]
    public void RefusesWhileSteamRunsAndLeavesTheFileAlone()
    {
        File.WriteAllText(File_, "user");
        _tracker.IsSteamRunning().Returns(true);
        var called = false;

        var result = Editor.Edit(Path_, b => { called = true; return Append(b, "+1"); }, _ => true);

        result.Refusal.Should().StartWith("Steam rewrites its config on exit");
        called.Should().BeFalse();
        File.ReadAllText(File_).Should().Be("user");
        File.Exists(File_ + ".silvermod-orig").Should().BeFalse();
    }

    [Fact]
    public void AnEditsRefusalLeavesEveryFileAlone()
    {
        File.WriteAllText(File_, "user");

        var result = Editor.Edit(Path_, _ => (null, "nope"), _ => true);

        result.Refusal.Should().Be("nope");
        File.Exists(File_ + ".silvermod-orig").Should().BeFalse();
        File.Exists(File_ + ".silvermod-bak").Should().BeFalse();
    }
}
