using System.Reflection;
using FluentAssertions;
using NexusMods.App;
using NexusMods.Sdk;

namespace NexusMods.DataModel.SchemaVersions.Tests;

public class FirstPartyAssemblyScanTests
{
    [Fact]
    public void ScanFindsAtLeastTheFloor()
    {
        var assemblies = PersistedNameGuards.LoadAppAssemblies();

        // Record the real count so the floor can be re-measured: cat bin/Debug/net*/first-party-assemblies.txt
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "first-party-assemblies.txt"), assemblies.Length.ToString());

        assemblies.Length.Should().BeGreaterThanOrEqualTo(PersistedNameGuards.MinimumFirstPartyAssemblies);
    }

    [Fact]
    public void ScanThrowsWhenItMatchesNothing()
    {
        var act = () => PersistedNameGuards.LoadAppAssemblies("Nothing.Matches.This.*.dll", "Nothing.Matches.This.", PersistedNameGuards.MinimumFirstPartyAssemblies);

        act.Should().Throw<InvalidOperationException>().WithMessage("*first-party assembly scan found*");
    }

    [Fact]
    public void EveryScannedAssemblyIsFirstParty()
    {
        PersistedNameGuards.LoadAppAssemblies()
            .Select(a => a.GetName().Name!)
            .Should().OnlyContain(name => name.StartsWith(FirstPartyNames.AssemblyPrefix, StringComparison.Ordinal) || name == FirstPartyNames.AppAssemblyName);
    }

    [Fact]
    public void ScanIncludesTheAppAssembly()
    {
        // The App assembly has no prefix, so a prefix-only predicate never sees it.
        var assemblies = PersistedNameGuards.LoadAppAssemblies();

        assemblies.Should().Contain(a => a.GetName().Name == FirstPartyNames.AppAssemblyName);
        assemblies.Should().Contain(typeof(StartupMode).Assembly);
    }

    [Fact]
    public void UpstreamPersistedPrefixStaysTheUpstreamPrefix()
    {
        // This is the prefix a stored name must NOT have. It must never be tidied into AssemblyPrefix: the
        // rename flips that one, and the persisted-name guards would then forbid the fork's own prefix.
        FirstPartyNames.UpstreamPersistedPrefix.Should().Be("NexusMods.");
    }
}
