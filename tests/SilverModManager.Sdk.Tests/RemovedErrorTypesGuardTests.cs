using System.Text.RegularExpressions;
using FluentAssertions;

namespace SilverModManager.Sdk.Tests;

/// <summary>
/// Errors reach the player through <c>UserFacingException</c> and the one error presenter. These two types were the
/// older paths for the same job and were removed; a second route to a dialog would bring back the split between
/// refusals and crashes, so the names must not return.
/// </summary>
public partial class RemovedErrorTypesGuardTests
{
    [GeneratedRegex(@"\b(UnhandledExceptionReporter|LoadoutApplierRefusal)\b")]
    private static partial Regex RemovedTypes();

    [Fact]
    public void Test_NoRemovedErrorTypeInSource()
    {
        var root = RepoScanGuard.FindRepoRoot();
        var files = RepoScanGuard.EnumerateFiles(root, "src", "*.cs", "*.axaml");
        var found = RepoScanGuard.FindLines(root, files, line => RemovedTypes().IsMatch(line));

        RepoScanGuard.AssertMatchesAllowlist(found, [], "report errors through UserFacingException and the error presenter, not the removed types");
    }
}
