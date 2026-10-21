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

    private static readonly (string File, string Line, string Reason)[] AllowedVariableNames = [];

    private static readonly (string File, string Line, string Reason)[] AllowedNativeOverrides = [];

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
