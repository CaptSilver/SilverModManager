using FluentAssertions;
using NexusMods.Abstractions.Games;
using NexusMods.Paths;
using NexusMods.Platform.Proton.Launch;

namespace NexusMods.Platform.Proton.Tests.Launch;

public class ExeSubstitutionTests
{
    private const string SkyrimRoot = "/games/steamapps/common/Skyrim Special Edition";
    private const string CyberpunkRoot = "/games/steamapps/common/Cyberpunk 2077";
    private const string BannerlordRoot = "/games/steamapps/common/Mount & Blade II Bannerlord";

    private static AbsolutePath Path_(string path) => FileSystem.Shared.FromUnsanitizedFullPath(path);

    private static LaunchSpec Spec(string exe, params string[] extraArgs) => new(Path_(exe), extraArgs);

    /// <summary>
    /// Steam's command for a Proton game, as logged: wrapper, reaper, entry point, then Proton's verb and the exe.
    /// </summary>
    private static string[] SteamCommand(string appId, string exe, params string[] gameArgs) =>
    [
        "/steam/ubuntu12_32/steam-launch-wrapper", "--", "/steam/ubuntu12_32/reaper", "SteamLaunch", $"AppId={appId}",
        "--", "/steam/steamapps/common/SteamLinuxRuntime_sniper/_v2-entry-point", "--verb=waitforexitandrun", "--",
        "/steam/steamapps/common/Proton 10.0/proton", "waitforexitandrun", exe, ..gameArgs,
    ];

    [Fact]
    public void Test_LauncherReplacedByLoaderWithGameArgsKept()
    {
        var argv = SteamCommand("489830", $"{SkyrimRoot}/SkyrimSELauncher.exe", "-windowed");

        var result = ExeSubstitution.Apply(argv, Path_(SkyrimRoot), Spec($"{SkyrimRoot}/skse64_loader.exe"), loaderResolved: true);

        result.Refusal.Should().BeNull();
        result.Argv.Should().Equal(SteamCommand("489830", $"{SkyrimRoot}/skse64_loader.exe", "-windowed"));
        result.AppId.Should().Be("489830");
    }

    [Fact]
    public void Test_LauncherReplacedByPrimaryExe()
    {
        var argv = SteamCommand("489830", $"{SkyrimRoot}/SkyrimSELauncher.exe");

        var result = ExeSubstitution.Apply(argv, Path_(SkyrimRoot), Spec($"{SkyrimRoot}/SkyrimSE.exe"), loaderResolved: false);

        result.Argv.Should().Equal(SteamCommand("489830", $"{SkyrimRoot}/SkyrimSE.exe"));
    }

    [Fact]
    public void Test_SlotAlreadyPrimaryIsUnchanged()
    {
        var argv = SteamCommand("489830", $"{SkyrimRoot}/SkyrimSE.exe", "-x");

        var result = ExeSubstitution.Apply(argv, Path_(SkyrimRoot), Spec($"{SkyrimRoot}/SkyrimSE.exe"), loaderResolved: false);

        result.Refusal.Should().BeNull();
        result.Argv.Should().Equal(argv);
    }

    [Fact]
    public void Test_SlotAlreadyPrimaryStillGetsExtraArgs()
    {
        // Dropping the tool's arguments here would start the game without the mods it was asked to load.
        var argv = SteamCommand("1091500", $"{CyberpunkRoot}/bin/x64/Cyberpunk2077.exe", "--launcher-skip");

        var result = ExeSubstitution.Apply(argv, Path_(CyberpunkRoot),
            Spec($"{CyberpunkRoot}/bin/x64/Cyberpunk2077.exe", "-modded"), loaderResolved: true);

        result.Argv.Should().Equal(SteamCommand("1091500", $"{CyberpunkRoot}/bin/x64/Cyberpunk2077.exe", "-modded", "--launcher-skip"));
    }

    [Fact]
    public void Test_SlotMatchingPrimaryAfterNormalisationIsUnchanged()
    {
        var argv = SteamCommand("489830", $"{SkyrimRoot}/./SkyrimSE.exe");

        var result = ExeSubstitution.Apply(argv, Path_(SkyrimRoot), Spec($"{SkyrimRoot}/SkyrimSE.exe"), loaderResolved: false);

        result.Argv.Should().Equal(argv);
    }

    [Fact]
    public void Test_ExeOutsideGameRootIsNotASlot()
    {
        var argv = SteamCommand("489830", "/elsewhere/SkyrimSELauncher.exe");

        var result = ExeSubstitution.Apply(argv, Path_(SkyrimRoot), Spec($"{SkyrimRoot}/SkyrimSE.exe"), loaderResolved: false);

        result.Argv.Should().Equal(argv);
    }

    [Fact]
    public void Test_SiblingFolderWithSamePrefixIsNotUnderRoot()
    {
        var argv = SteamCommand("489830", $"{SkyrimRoot} VR/SkyrimVR.exe");

        var result = ExeSubstitution.Apply(argv, Path_(SkyrimRoot), Spec($"{SkyrimRoot}/SkyrimSE.exe"), loaderResolved: true);

        result.IsRefused.Should().BeTrue();
    }

    [Fact]
    public void Test_NoSlotWithLoaderIsRefusalCarryingArgv()
    {
        string[] argv = ["/usr/bin/true", "--flag"];

        var result = ExeSubstitution.Apply(argv, Path_(SkyrimRoot), Spec($"{SkyrimRoot}/skse64_loader.exe"), loaderResolved: true);

        result.IsRefused.Should().BeTrue();
        result.Refusal.Should().Contain("/usr/bin/true --flag");
        result.Argv.Should().BeEmpty("the unchanged command must never be offered after a refusal");
    }

    [Fact]
    public void Test_NoSlotNoLoaderIsUnchanged()
    {
        string[] argv = ["/usr/bin/true", "--flag"];

        var result = ExeSubstitution.Apply(argv, Path_(SkyrimRoot), Spec($"{SkyrimRoot}/SkyrimSE.exe"), loaderResolved: false);

        result.Refusal.Should().BeNull();
        result.Argv.Should().Equal(argv);
    }

    [Fact]
    public void Test_ModdedInsertedAfterExeBeforeExistingArgs()
    {
        var argv = SteamCommand("1091500", $"{CyberpunkRoot}/REDprelauncher.exe", "--launcher-skip");

        var result = ExeSubstitution.Apply(argv, Path_(CyberpunkRoot),
            Spec($"{CyberpunkRoot}/bin/x64/Cyberpunk2077.exe", "-modded"), loaderResolved: true);

        result.Argv.Should().Equal(SteamCommand("1091500", $"{CyberpunkRoot}/bin/x64/Cyberpunk2077.exe", "-modded", "--launcher-skip"));
    }

    [Fact]
    public void Test_ModuleListInsertedWithStarsIntact()
    {
        const string modules = "_MODULES_*Native*SandBoxCore*Sandbox*StoryMode*_MODULES_";
        var argv = SteamCommand("261550", $"{BannerlordRoot}/bin/Win64_Shipping_Client/TaleWorlds.MountAndBlade.Launcher.exe", "last");

        var result = ExeSubstitution.Apply(argv, Path_(BannerlordRoot),
            Spec($"{BannerlordRoot}/bin/Win64_Shipping_Client/Bannerlord.exe", "/singleplayer", modules), loaderResolved: true);

        var exeIndex = result.Argv.ToList().IndexOf($"{BannerlordRoot}/bin/Win64_Shipping_Client/Bannerlord.exe");
        result.Argv.Skip(exeIndex).Should().Equal(
            $"{BannerlordRoot}/bin/Win64_Shipping_Client/Bannerlord.exe", "/singleplayer", modules, "last");
    }

    [Fact]
    public void Test_TerminalFormUsesFirstExeUnderGameRoot()
    {
        string[] argv = ["gamemoderun", "wine", $"{SkyrimRoot}/SkyrimSELauncher.exe", "-a"];

        var result = ExeSubstitution.Apply(argv, Path_(SkyrimRoot), Spec($"{SkyrimRoot}/skse64_loader.exe", "-b"), loaderResolved: true);

        result.Argv.Should().Equal("gamemoderun", "wine", $"{SkyrimRoot}/skse64_loader.exe", "-b", "-a");
        result.AppId.Should().BeNull();
    }

    [Fact]
    public void Test_AppIdFoundBehindWrapperPrefix()
    {
        // The launch option was "gamemoderun %command% --launcher-skip", so the prefix sits in front of Steam's wrapper.
        string[] argv = ["gamemoderun", .. SteamCommand("1091500", $"{CyberpunkRoot}/REDprelauncher.exe", "--launcher-skip")];

        var result = ExeSubstitution.Apply(argv, Path_(CyberpunkRoot), Spec($"{CyberpunkRoot}/REDprelauncher.exe"), loaderResolved: false);

        result.AppId.Should().Be("1091500");
        ExeSubstitution.ExtractAppId(argv).Should().Be("1091500");
    }

    [Fact]
    public void Test_AppIdIgnoresNonNumericAndMissing()
    {
        ExeSubstitution.ExtractAppId(["reaper", "SteamLaunch", "AppId=abc"]).Should().BeNull();
        ExeSubstitution.ExtractAppId(["AppId=5"]).Should().BeNull("only the value after SteamLaunch is the launch's app id");
        ExeSubstitution.ExtractAppId([]).Should().BeNull();
    }
}
