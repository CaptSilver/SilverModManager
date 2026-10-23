using FluentAssertions;
using NexusMods.Games.FromSoft.EldenRing;
using NexusMods.Games.FromSoft.Saves;

namespace NexusMods.Games.FromSoft.Tests;

public class FromSoftGameDefinitionTests
{
    [Fact]
    public void EldenRingsLayoutIsBuiltFromItsPathConstants()
    {
        EldenRingGame.Definition.Layout.Should().Be(new FromSoftGameLayout(
            EldenRingPaths.SteamAppId,
            "EldenRing",
            EldenRingPaths.GameSubfolder,
            EldenRingPaths.GameExecutable,
            EldenRingPaths.AntiCheatLauncher,
            "EldenRing",
            SaveRootKind.RoamingAppData,
            SaveAccountFolderKind.SteamId64Decimal));
    }

    [Fact]
    public void EldenRingsSaveNamesAreItsPathConstants()
    {
        EldenRingGame.Definition.SaveNames.Should().Be(new FromSoftSaveNames(
            EldenRingPaths.VanillaSave,
            EldenRingPaths.ModdedSave,
            EldenRingPaths.SeamlessCoopSave));
    }
}
