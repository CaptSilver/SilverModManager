using System.Collections.Immutable;
using FluentAssertions;
using NexusMods.Games.FromSoft.Cleanup;
using NexusMods.Games.FromSoft.Doctor;
using NexusMods.Games.FromSoft.EldenRing;
using NexusMods.Games.FromSoft.Formats;
using NexusMods.Games.FromSoft.Me3;
using NexusMods.Games.FromSoft.Overlay;
using NexusMods.Games.FromSoft.Saves;

namespace NexusMods.Games.FromSoft.Tests;

/// <summary>Elden Ring's definition, spelled out as literals so that moving a value out of the game class fails a test.</summary>
public class EldenRingConstantsTests
{
    private static FromSoftGameDefinition Definition => EldenRingGame.Definition;

    [Fact]
    public void TheLayoutIsEldenRingsOwn()
    {
        Definition.Layout.Should().Be(new FromSoftGameLayout(
            1245620, "EldenRing", "Game", "eldenring.exe", "start_protected_game.exe", "EldenRing",
            SaveRootKind.RoamingAppData, SaveAccountFolderKind.SteamId64Decimal));
    }

    [Fact]
    public void TheSaveNamesAreEldenRingsOwn()
    {
        Definition.SaveNames.Should().Be(new FromSoftSaveNames("ER0000.sl2", "ER0000_silver.sl2", "ER0000.co2"));
    }

    [Fact]
    public void TheCheckRulesNameTheLauncherSeamlessCoopAndTheSpawners()
    {
        Definition.CheckRules.Should().BeEquivalentTo(GameFolderCheckRules.ModEngine2 with
        {
            Launcher = "start_protected_game.exe",
            ModFolders = ["SeamlessCoop", "mods"],
            ExtraArtifacts = ["ersc_launcher.exe"],
            Spawners = ["ermerchant.dll", "erdtools.dll", "glorious_merchant.dll"],
        });
    }

    [Fact]
    public void TheCleanupRulesKeepTheModsFolderAndRemoveSeamlessCoop()
    {
        Definition.Cleanup.Should().BeEquivalentTo(CleanupRules.ModEngine2 with
        {
            Launcher = "start_protected_game.exe",
            ModsFolder = "mods",
            Leftovers = [..CleanupRules.ModEngine2.Leftovers, "SeamlessCoop", "ersc_launcher.exe"],
        });
    }

    [Fact]
    public void TheVerbAndTheOverlayAllowlistAreEldenRingsOwn()
    {
        Definition.CleanupVerb.Should().Be("elden-ring cleanup");
        Definition.Overlay.Should().BeEquivalentTo(OverlayAllowlist.Default with { Folders = ["mods/", "SeamlessCoop/"] });
    }

    [Fact]
    public void TheFormatsAreEldenRingsOwn()
    {
        Definition.Formats.Should().BeEquivalentTo(new FromSoftFormatTraits(
            BinderFormat.Bnd4,
            [DcxCompression.Kraken, DcxCompression.Dflt, DcxCompression.Zstd],
            new DcxWriteFormat(DcxCompression.Dflt, 0x11000, 9, 0),
            "regulation.bin",
            new DcxWriteFormat(DcxCompression.Zstd, 0x11000, 15, 16),
            [..Convert.FromHexString("99BFFC366A6BC8C6F5827D093602D676C42892A01C207FB024D3AF4E493FEF99")],
            "eldenring",
            Definition.Formats.Archives,
            PathHashBits: 64,
            PathHashPrime: 133,
            Bhd5Layout.Salted40ByteEntries));
    }

    [Fact]
    public void Me3IsToldTheGameNameAndSeamlessCoopIsItsOnlyNative()
    {
        EldenRingGame.Me3Options.GameName.Should().Be("eldenring");
        EldenRingGame.Me3Options.OverlayNatives.Should().Equal(SeamlessCoop.Native);
    }
}
