using FluentAssertions;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.DataModel.SchemaVersions.Tests;

public class BareHashApiGuard
{
    [Fact]
    public void FileIdentity_DeclaresNoEqualityOperators()
    {
        // Two identities of different kinds can name one file, so == would answer "different" for the same bytes.
        // Callers spell the question with MatchSameKind (or the resolver) instead.
        var operators = typeof(FileIdentity).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            .Select(m => m.Name)
            .Where(n => n is "op_Equality" or "op_Inequality");

        operators.Should().BeEmpty();
    }
}
