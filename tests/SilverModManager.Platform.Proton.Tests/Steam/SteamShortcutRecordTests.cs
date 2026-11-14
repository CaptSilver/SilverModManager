using FluentAssertions;
using NexusMods.Paths;
using SilverModManager.Platform.Proton.Steam.Shortcuts;

namespace SilverModManager.Platform.Proton.Tests.Steam;

public class SteamShortcutRecordTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("silvermod-record-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private AbsolutePath RecordPath => SteamShortcutRecord.PathIn(FileSystem.Shared.FromUnsanitizedFullPath(_root));

    [Fact]
    public void TheRecordRoundTripsThroughJson()
    {
        var record = new SteamShortcutRecord(65369667, 0x8123_4567, "\"/x/silvermod\"", true, ["/a/b.png", "/a/c.png"]);

        record.Save(RecordPath);

        SteamShortcutRecord.Load(RecordPath).Should().BeEquivalentTo(record);
        RecordPath.ToString().Should().EndWith("/silvermod/steam-shortcut.json");
    }

    [Fact]
    public void AnUnconfirmedRecordHasNoAppId()
    {
        new SteamShortcutRecord(1, null, "\"/x\"", false, []).Save(RecordPath);

        var loaded = SteamShortcutRecord.Load(RecordPath)!;
        loaded.AppId.Should().BeNull();
        loaded.Confirmed.Should().BeFalse();
    }

    [Fact]
    public void AMissingOrUnreadableRecordLoadsAsNull()
    {
        SteamShortcutRecord.Load(RecordPath).Should().BeNull();

        RecordPath.Parent.CreateDirectory();
        File.WriteAllText(RecordPath.ToString(), "{ not json");
        SteamShortcutRecord.Load(RecordPath).Should().BeNull();
    }

    [Fact]
    public void DeleteRemovesTheFileAndToleratesItBeingGone()
    {
        new SteamShortcutRecord(1, 2, "e", true, []).Save(RecordPath);

        SteamShortcutRecord.Delete(RecordPath);
        SteamShortcutRecord.Delete(RecordPath);

        File.Exists(RecordPath.ToString()).Should().BeFalse();
    }
}
