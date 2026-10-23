using FluentAssertions;
using System.Diagnostics;
using Xunit;
using SilverModManager.Abstractions.Secrets;

namespace SilverModManager.Secrets.Tests;

// Each switch lists every case and throws on anything else, so a case added to one of these types
// fails here until the tests (and the consumers they stand for) are updated.
public class ContractShapeTests
{
    private static string Describe(SecretResult result) => result switch
    {
        SecretResult.Found found => $"found:{found.Value}",
        SecretResult.NotFound => "not-found",
        SecretResult.Unavailable unavailable => $"unavailable:{unavailable.Kind}:{unavailable.Reason}",
        _ => throw new UnreachableException(),
    };

    private static string Describe(SecretWriteResult result) => result switch
    {
        SecretWriteResult.Written => "written",
        SecretWriteResult.Unavailable unavailable => $"unavailable:{unavailable.Kind}:{unavailable.Reason}",
        SecretWriteResult.ReadOnly readOnly => $"read-only:{readOnly.Source}",
        _ => throw new UnreachableException(),
    };

    private static bool NeedsUserAction(SecretUnavailableKind kind) => kind switch
    {
        SecretUnavailableKind.NoBus => false,
        SecretUnavailableKind.ServiceAbsent => false,
        SecretUnavailableKind.NoDefaultCollection => true,
        SecretUnavailableKind.Locked => true,
        SecretUnavailableKind.Dismissed => true,
        SecretUnavailableKind.Timeout => false,
        SecretUnavailableKind.Malformed => true,
        SecretUnavailableKind.Faulted => false,
        _ => throw new UnreachableException(),
    };

    [Fact]
    public void Read_results_match_exhaustively()
    {
        Describe(new SecretResult.Found("v")).Should().Be("found:v");
        Describe(new SecretResult.NotFound()).Should().Be("not-found");
        Describe(new SecretResult.Unavailable(SecretUnavailableKind.Locked, "locked")).Should().Be("unavailable:Locked:locked");
    }

    [Fact]
    public void Write_results_match_exhaustively()
    {
        Describe(new SecretWriteResult.Written()).Should().Be("written");
        Describe(new SecretWriteResult.Unavailable(SecretUnavailableKind.NoBus, "no bus")).Should().Be("unavailable:NoBus:no bus");
        Describe(new SecretWriteResult.ReadOnly("env")).Should().Be("read-only:env");
    }

    [Fact]
    public void Every_unavailable_kind_is_handled()
    {
        foreach (var kind in Enum.GetValues<SecretUnavailableKind>())
        {
            var act = () => NeedsUserAction(kind);
            act.Should().NotThrow();
        }
    }

    [Fact]
    public void Migration_outcomes_match_exhaustively()
    {
        static string Describe(SecretMigrationOutcome outcome) => outcome switch
        {
            SecretMigrationOutcome.Moved => "moved",
            SecretMigrationOutcome.NothingToMove => "nothing",
            SecretMigrationOutcome.Deferred deferred => $"deferred:{deferred.Reason}",
            _ => throw new UnreachableException(),
        };

        Describe(new SecretMigrationOutcome.Moved()).Should().Be("moved");
        Describe(new SecretMigrationOutcome.NothingToMove()).Should().Be("nothing");
        Describe(new SecretMigrationOutcome.Deferred("locked")).Should().Be("deferred:locked");
    }

    [Fact]
    public void Options_default_to_unset_paths_and_choice()
    {
        var options = new SecretStoreOptions { Mode = SecretStoreMode.NonInteractive };
        options.SecretsFile.Should().BeNull();
        options.Backend.Should().BeNull();
    }
}
