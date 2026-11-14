using FluentAssertions;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Platform.Proton.Steam;
using SilverModManager.Platform.Proton.Steam.BinaryKeyValues;
using SilverModManager.Platform.Proton.Steam.Shortcuts;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Platform.Proton.Tests.Steam;

public class ShortcutsVdfWriterTests : IDisposable
{
    private const string Exe = "\"/home/u/.local/share/silvermod/bin/silvermod\"";
    private const string Name = "SilverModManager";

    private readonly string _root = Directory.CreateTempSubdirectory("silvermod-shortcuts-").FullName;
    private readonly IProcessTracker _tracker = Substitute.For<IProcessTracker>();
    private readonly IBinaryKeyValues _kv = new HandWrittenBinaryKeyValues();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string FilePath => System.IO.Path.Combine(_root, "shortcuts.vdf");
    private AbsolutePath File_ => FileSystem.Shared.FromUnsanitizedFullPath(FilePath);
    private ShortcutsVdfWriter Writer => new(_kv, new SteamConfigFileEditor(_tracker));

    private static SteamShortcutEntry Entry => new(SteamShortcutId.AppId(Exe, Name), Name, Exe, "\"/home/u/.local/share/silvermod/bin/\"", "/home/u/.local/share/silvermod/icons/silvermodmanager.png");
    private static ShortcutMatch Match => new(SteamShortcutId.AppId(Exe, Name), new HashSet<string> { Exe });

    private static bool Contains(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle) >= 0;

    [Fact]
    public void AddCreatesTheChildWithEveryFieldQuotedExeAndInt32AppId()
    {
        File.WriteAllBytes(FilePath, ShortcutsVdfFixture.Bytes());

        var edit = Writer.AddOrReplace(File_, Entry, Match);

        edit.Refusal.Should().BeNull();
        var entries = Writer.Read(File_);
        entries.Should().HaveCount(2);
        var ours = entries.Single(e => e.AppName == Name);
        ours.Exe.Should().Be(Exe);
        ours.AppId.Should().Be(Entry.AppId);
        ours.AllowOverlay.Should().BeTrue();
        // appid is stored as the int32 type byte 0x02, not as a string.
        Contains(File.ReadAllBytes(FilePath), [0x02, .. "appid"u8, 0x00, .. BitConverter.GetBytes(unchecked((int)Entry.AppId))]).Should().BeTrue();
    }

    [Fact]
    public void AddKeepsTheExistingChildBytesAsTheyWere()
    {
        var existing = ShortcutsVdfFixture.Bytes();
        File.WriteAllBytes(FilePath, existing);

        Writer.AddOrReplace(File_, Entry, Match);

        Contains(File.ReadAllBytes(FilePath), ShortcutsVdfFixture.ChildBytes("0", unchecked((int)0x8123_4567), "Game", "\"/usr/bin/game\"")).Should().BeTrue();
    }

    [Fact]
    public void AddUsesTheNextFreeIntegerKeyWithoutRenumbering()
    {
        File.WriteAllBytes(FilePath, ShortcutsVdfFixture.Bytes(("0", 1, "A", "\"/a\""), ("3", 2, "B", "\"/b\""), ("7", 3, "C", "\"/c\"")));

        Writer.AddOrReplace(File_, Entry, Match).Refusal.Should().BeNull();

        var bytes = File.ReadAllBytes(FilePath);
        var shortcuts = (BinaryKvMap)_kv.Read(bytes).Children.Single();
        shortcuts.Children.Select(c => c.Name).Should().Equal("0", "3", "7", "8");
        Contains(bytes, ShortcutsVdfFixture.ChildBytes("3", 2, "B", "\"/b\"")).Should().BeTrue();
        Contains(bytes, ShortcutsVdfFixture.ChildBytes("7", 3, "C", "\"/c\"")).Should().BeTrue();
    }

    [Fact]
    public void AddOverAnEntryMatchedByRecordedAppIdReplacesIt()
    {
        // Same appid, different exe (the shim was renamed): found by the recorded id.
        File.WriteAllBytes(FilePath, ShortcutsVdfFixture.Bytes(("0", unchecked((int)Entry.AppId), Name, "\"/old/shim\"")));

        Writer.AddOrReplace(File_, Entry, Match with { QuotedExes = new HashSet<string>() }).Refusal.Should().BeNull();

        var entries = Writer.Read(File_);
        entries.Should().ContainSingle().Which.Exe.Should().Be(Exe);
    }

    [Fact]
    public void AddOverAnEntryMatchedByExeReplacesIt()
    {
        File.WriteAllBytes(FilePath, ShortcutsVdfFixture.Bytes(("0", 12345, "Old name", Exe)));

        Writer.AddOrReplace(File_, Entry, new ShortcutMatch(null, new HashSet<string> { Exe })).Refusal.Should().BeNull();

        var entries = Writer.Read(File_);
        entries.Should().ContainSingle().Which.AppName.Should().Be(Name);
    }

    [Fact]
    public void RemoveDropsOnlyOurs()
    {
        File.WriteAllBytes(FilePath, ShortcutsVdfFixture.Bytes(("0", 1, "A", "\"/a\""), ("1", unchecked((int)Entry.AppId), Name, Exe), ("2", 3, "C", "\"/c\"")));

        Writer.Remove(File_, Match).Refusal.Should().BeNull();

        var bytes = File.ReadAllBytes(FilePath);
        Writer.Read(File_).Select(e => e.AppName).Should().Equal("A", "C");
        Contains(bytes, ShortcutsVdfFixture.ChildBytes("0", 1, "A", "\"/a\"")).Should().BeTrue();
        Contains(bytes, ShortcutsVdfFixture.ChildBytes("2", 3, "C", "\"/c\"")).Should().BeTrue();
    }

    [Fact]
    public void RemoveWithNoMatchingEntryRefusesAndLeavesTheFile()
    {
        var before = ShortcutsVdfFixture.Bytes();
        File.WriteAllBytes(FilePath, before);

        Writer.Remove(File_, Match).Refusal.Should().NotBeNull();

        File.ReadAllBytes(FilePath).Should().Equal(before);
    }

    [Fact]
    public void AMissingFileWithAConfigFolderIsCreatedWithJustOurEntry()
    {
        Writer.AddOrReplace(File_, Entry, Match).Refusal.Should().BeNull();

        Writer.Read(File_).Should().ContainSingle().Which.AppName.Should().Be(Name);
        File.Exists(FilePath + ".silvermod-orig").Should().BeFalse();
    }

    [Fact]
    public void AMissingConfigFolderIsARefusal()
    {
        var file = FileSystem.Shared.FromUnsanitizedFullPath(System.IO.Path.Combine(_root, "nope", "shortcuts.vdf"));

        Writer.AddOrReplace(file, Entry, Match).Refusal.Should().NotBeNull();
    }

    [Fact]
    public void RefusesWhileSteamRuns()
    {
        var before = ShortcutsVdfFixture.Bytes();
        File.WriteAllBytes(FilePath, before);
        _tracker.IsSteamRunning().Returns(true);

        // The shortcut file gets its own sentence, not the launch-option one the editor uses by default.
        Writer.AddOrReplace(File_, Entry, Match).Refusal.Should().StartWith("Steam is running.").And.NotContain("launch option");

        File.ReadAllBytes(FilePath).Should().Equal(before);
    }

    [Fact]
    public void AnUnparseableFileIsRefusedAndLeftAlone()
    {
        File.WriteAllBytes(FilePath, [0x07, 0x41, 0x00]);

        Writer.AddOrReplace(File_, Entry, Match).Refusal.Should().Be("Steam's shortcuts.vdf could not be parsed.");

        File.ReadAllBytes(FilePath).Should().Equal(0x07, 0x41, 0x00);
    }

    [Fact]
    public void AReadBackThatDiffersRestoresTheOriginal()
    {
        var before = ShortcutsVdfFixture.Bytes();
        File.WriteAllBytes(FilePath, before);
        var corrupting = new RetypingAppidOnFirstWrite();

        var edit = new ShortcutsVdfWriter(corrupting, new SteamConfigFileEditor(_tracker)).AddOrReplace(File_, Entry, Match);

        edit.Refusal.Should().Be("Steam's shortcuts.vdf did not read back the entry that was written, so the original was restored.");
        File.ReadAllBytes(FilePath).Should().Equal(before);
    }

    /// <summary>
    /// Retypes the appid of the added entry as a string on the first write, the failure SteamKit2's writer would cause.
    /// </summary>
    private sealed class RetypingAppidOnFirstWrite : IBinaryKeyValues
    {
        private readonly HandWrittenBinaryKeyValues _real = new();
        private int _writes;

        public BinaryKvMap Read(ReadOnlySpan<byte> bytes) => _real.Read(bytes);

        public byte[] Write(BinaryKvMap root)
        {
            if (++_writes == 1)
            {
                var added = (BinaryKvMap)((BinaryKvMap)root.Children.Single()).Children.Last();
                var index = added.Children.FindIndex(c => c.Name == "appid");
                added.Children[index] = new BinaryKvString("appid", ((BinaryKvInt32)added.Children[index]).Value.ToString());
            }
            return _real.Write(root);
        }
    }
}
