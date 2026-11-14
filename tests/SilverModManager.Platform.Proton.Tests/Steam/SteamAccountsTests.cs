using FluentAssertions;
using NexusMods.Paths;
using SilverModManager.Platform.Proton.Steam.Shortcuts;

namespace SilverModManager.Platform.Proton.Tests.Steam;

public class SteamAccountsTests : IDisposable
{
    private const ulong Base = 76561197960265728;
    private readonly string _root = Directory.CreateTempSubdirectory("silvermod-accounts-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private AbsolutePath Root => FileSystem.Shared.FromUnsanitizedFullPath(_root);
    private AbsolutePath Registry => FileSystem.Shared.FromUnsanitizedFullPath(System.IO.Path.Combine(_root, "registry.vdf"));

    private record User(uint Id, string Name, string Persona, bool MostRecent = false, bool AutoLogin = false, bool HasConfig = true);

    private void Users(params User[] users)
    {
        Directory.CreateDirectory(System.IO.Path.Combine(_root, "config"));
        var text = "\"users\"\n{\n" + string.Concat(users.Select(u =>
            $"\t\"{Base + u.Id}\"\n\t{{\n\t\t\"AccountName\"\t\t\"{u.Name}\"\n\t\t\"PersonaName\"\t\t\"{u.Persona}\"\n" +
            $"\t\t\"MostRecent\"\t\t\"{(u.MostRecent ? 1 : 0)}\"\n\t\t\"AutoLogin\"\t\t\"{(u.AutoLogin ? 1 : 0)}\"\n\t\t\"Timestamp\"\t\t\"1700000000\"\n\t}}\n")) + "}\n";
        File.WriteAllText(System.IO.Path.Combine(_root, "config", "loginusers.vdf"), text);
        foreach (var user in users.Where(u => u.HasConfig)) Directory.CreateDirectory(System.IO.Path.Combine(_root, "userdata", user.Id.ToString(), "config"));
    }

    private void AutoLoginUser(string name)
        => File.WriteAllText(Registry.ToString(),
            $"\"Registry\"\n{{\n\t\"HKCU\"\n\t{{\n\t}}\n\t\"HKLM\"\n\t{{\n\t\t\"Software\"\n\t\t{{\n\t\t\t\"Valve\"\n\t\t\t{{\n\t\t\t\t\"Steam\"\n\t\t\t\t{{\n\t\t\t\t\t\"AutoLoginUser\"\t\t\"{name}\"\n\t\t\t\t}}\n\t\t\t}}\n\t\t}}\n\t}}\n}}\n");

    private static SteamAccount Chosen(SteamAccountChoice choice) => choice.Should().BeOfType<SteamAccountChoice.Chosen>().Subject.Account;

    [Fact]
    public void AutoLoginUserFromTheRegistryWins()
    {
        Users(new User(1, "alice", "Alice", MostRecent: true), new User(2, "bob", "Bob"));
        AutoLoginUser("bob");

        Chosen(SteamAccounts.Pick(Root, Registry)).AccountName.Should().Be("bob");
    }

    [Fact]
    public void WithoutARegistryASingleMostRecentWins()
    {
        Users(new User(1, "alice", "Alice"), new User(2, "bob", "Bob", MostRecent: true));

        Chosen(SteamAccounts.Pick(Root, null)).PersonaName.Should().Be("Bob");
    }

    [Fact]
    public void WithoutMostRecentASingleAutoLoginWins()
    {
        Users(new User(1, "alice", "Alice", AutoLogin: true), new User(2, "bob", "Bob"));

        Chosen(SteamAccounts.Pick(Root, null)).AccountName.Should().Be("alice");
    }

    [Fact]
    public void TwoCandidatesAndNoTieBreakerIsAmbiguousListingBoth()
    {
        Users(new User(1, "alice", "Alice"), new User(2, "bob", "Bob"));

        var choice = SteamAccounts.Pick(Root, Registry);

        choice.Should().BeOfType<SteamAccountChoice.Ambiguous>().Which.Candidates.Select(c => c.PersonaName).Should().Equal("Alice", "Bob");
    }

    [Fact]
    public void ARequestedAccountIdWinsWhenItHasAConfigFolder()
    {
        Users(new User(1, "alice", "Alice", MostRecent: true), new User(2, "bob", "Bob"));

        Chosen(SteamAccounts.Pick(Root, null, requested: 2)).AccountId.Should().Be(2u);
    }

    [Fact]
    public void ARequestedAccountIdWithoutAConfigFolderIsNotChosen()
    {
        Users(new User(1, "alice", "Alice", MostRecent: true), new User(2, "bob", "Bob", HasConfig: false));

        Chosen(SteamAccounts.Pick(Root, null, requested: 2)).AccountId.Should().Be(1u);
    }

    [Fact]
    public void TheAccountIdIsTheSteamId64MinusTheBase()
    {
        Users(new User(65369667, "alice", "Alice", MostRecent: true));

        Chosen(SteamAccounts.Pick(Root, null)).AccountId.Should().Be(65369667u);
    }

    [Fact]
    public void AnAutoLoginUserWithoutAConfigFolderFallsThrough()
    {
        Users(new User(1, "alice", "Alice", MostRecent: true), new User(2, "bob", "Bob", HasConfig: false));
        AutoLoginUser("bob");

        Chosen(SteamAccounts.Pick(Root, Registry)).AccountName.Should().Be("alice");
    }

    [Fact]
    public void NoLoginUsersFileMeansNone()
    {
        SteamAccounts.Pick(Root, null).Should().BeOfType<SteamAccountChoice.None>();
    }

    [Fact]
    public void ADriveWithNoConfigFoldersMeansNone()
    {
        Users(new User(1, "alice", "Alice", HasConfig: false));

        SteamAccounts.Pick(Root, null).Should().BeOfType<SteamAccountChoice.None>();
    }
}
