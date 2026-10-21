namespace NexusMods.Sdk.Tests;

/// <summary>
/// A game's required Wine DLL overrides are declared once, on the game, and turned into a
/// WINEDLLOVERRIDES value only by <c>WineParser</c>. These scans stop diagnostics and the launch
/// line from growing a second list or hard-coding the variable name.
/// </summary>
public class WineDllOverrideGuardTests
{
    private const string VariableName = "WINEDLLOVERRIDES";
    private const string NativeOverride = "WineDllOverrideType.Native";
    private const string RequirementsInterface = "IWineDllOverrideRequirements";

    private const string WineParserFile = "src/NexusMods.Sdk/WineParser.cs";
    private const string WineDllOverrideFile = "src/NexusMods.Sdk/WineDllOverride.cs";

    private static readonly (string File, string Line, string Reason)[] AllowedVariableNames =
    [
        ("src/NexusMods.Games.Generic/WineDiagnosticHelper.cs", "WINEDLLOVERRIDES=\"{dllOverridesString}\" %command%", "instruction text; the games declare their overrides and the helper spells the variable through WineParser"),
        ("src/NexusMods.Games.Generic/WineDiagnosticHelper.cs", "WINEDLLOVERRIDES=\"{dllOverridesString}\"", "instruction text; the games declare their overrides and the helper spells the variable through WineParser"),
        ("src/NexusMods.Games.Generic/WineDiagnosticHelper.cs", "Update the `WINEDLLOVERRIDES` environment variable to be the following:", "instruction text; the games declare their overrides and the helper spells the variable through WineParser"),
        ("src/NexusMods.Games.Larian/BaldursGate3/Diagnostics.cs", "`WINEDLLOVERRIDES=\"DWrite=n,b\" %command%`", "instruction text; the games declare their overrides and the helper spells the variable through WineParser"),
    ];

    private static readonly (string File, string Line, string Reason)[] AllowedNativeOverrides =
    [
        ("src/NexusMods.Games.RedEngine/Cyberpunk2077/Emitters/WinePrefixRequirementsEmitter.cs", "new(\"winmm\", [WineDllOverrideType.Native, WineDllOverrideType.BuiltIn]),", "moves to Cyberpunk2077Game"),
        ("src/NexusMods.Games.RedEngine/Cyberpunk2077/Emitters/WinePrefixRequirementsEmitter.cs", "new(\"version\", [WineDllOverrideType.Native, WineDllOverrideType.BuiltIn]),", "moves to Cyberpunk2077Game"),
    ];

    [Fact]
    public void Test_TheWineDllOverridesVariableNameIsSpelledOnlyInWineParser()
    {
        var root = RepoScanGuard.FindRepoRoot();
        var files = RepoScanGuard.EnumerateFiles(root, "src", "*.cs").Where(f => f != WineParserFile);
        var found = RepoScanGuard.FindLines(root, files, line => !line.StartsWith("//") && line.Contains(VariableName, StringComparison.Ordinal));

        RepoScanGuard.AssertMatchesAllowlist(found, AllowedVariableNames, $"the variable name belongs to WineParser.WineDllOverridesEnvironmentVariableName; use it instead of spelling {VariableName}");
    }

    [Fact]
    public void Test_DllOverrideRequirementsAreDeclaredOnlyByGames()
    {
        var root = RepoScanGuard.FindRepoRoot();
        var files = RepoScanGuard.EnumerateFiles(root, "src", "*.cs")
            .Where(f => f != WineParserFile && f != WineDllOverrideFile)
            .Where(f => !File.ReadAllText(Path.Combine(root, f)).Contains(RequirementsInterface, StringComparison.Ordinal));
        var found = RepoScanGuard.FindLines(root, files, line => !line.StartsWith("//") && line.Contains(NativeOverride, StringComparison.Ordinal));

        RepoScanGuard.AssertMatchesAllowlist(found, AllowedNativeOverrides, $"a native DLL override is declared by the game class that implements {RequirementsInterface}; read it from there instead of building another list");
    }
}
