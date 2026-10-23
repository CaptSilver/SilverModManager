using R3;
using FluentAssertions;
using SilverModManager.Abstractions.Secrets;
using SilverModManager.Secrets.Overlay;
using Xunit;

namespace SilverModManager.Secrets.Tests.Overlay;

public class OverlaySecretStoreTests : IDisposable
{
    private static readonly SecretKey ApiKey = new("nexus.api-key");
    private static readonly SecretKey Token = new("github.token");
    private static readonly SecretKey Other = new("fromsoft.ersc-password");

    private static readonly Dictionary<SecretKey, string> Aliases = new()
    {
        [ApiKey] = "NEXUS_API_KEY",
        [Token] = "GITHUB_TOKEN",
    };

    private readonly string _scratch = Directory.CreateTempSubdirectory("overlay-secret-store-").FullName;
    private readonly InMemorySecretStore _inner = new();
    private readonly FakeEnvironment _env = new();

    public void Dispose() => Directory.Delete(_scratch, recursive: true);

    private OverlaySecretStore Create(SecretsFileReader? file = null) => new(_inner, _env, file, Aliases, [ApiKey, Token, Other]);

    private SecretsFileReader FileWith(string content, CapturingLogger? logger = null)
    {
        var path = Path.Combine(_scratch, "secrets.env");
        System.IO.File.WriteAllText(path, content);
        System.IO.File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return new SecretsFileReader(path, logger ?? new CapturingLogger());
    }

    private async Task<SecretResult> Get(OverlaySecretStore store, SecretKey key) => await store.GetAsync(key, CancellationToken.None);

    [Fact]
    public async Task The_inner_store_answers_when_nothing_overrides_the_key()
    {
        await _inner.SetAsync(ApiKey, "stored", CancellationToken.None);

        (await Get(Create(), ApiKey)).Should().Be(new SecretResult.Found("stored"));
    }

    [Fact]
    public async Task The_keys_own_environment_variable_beats_every_other_source()
    {
        _env.Values["SILVERMM_SECRET_NEXUS_API_KEY"] = "from-key-var";
        _env.Values["NEXUS_API_KEY"] = "from-alias";
        await _inner.SetAsync(ApiKey, "stored", CancellationToken.None);
        var file = FileWith("NEXUS_API_KEY=from-file");

        (await Get(Create(file), ApiKey)).Should().Be(new SecretResult.Found("from-key-var"));
    }

    [Fact]
    public async Task The_alias_beats_the_secrets_file_and_the_inner_store()
    {
        _env.Values["NEXUS_API_KEY"] = "from-alias";
        await _inner.SetAsync(ApiKey, "stored", CancellationToken.None);
        var file = FileWith("NEXUS_API_KEY=from-file");

        (await Get(Create(file), ApiKey)).Should().Be(new SecretResult.Found("from-alias"));
    }

    [Fact]
    public async Task The_secrets_file_beats_the_inner_store_and_is_found_by_either_name()
    {
        await _inner.SetAsync(ApiKey, "stored", CancellationToken.None);
        await _inner.SetAsync(Other, "stored-other", CancellationToken.None);
        var file = FileWith("NEXUS_API_KEY=file-alias\nSILVERMM_SECRET_FROMSOFT_ERSC_PASSWORD=file-key");
        var store = Create(file);

        (await Get(store, ApiKey)).Should().Be(new SecretResult.Found("file-alias"));
        (await Get(store, Other)).Should().Be(new SecretResult.Found("file-key"));
    }

    [Fact]
    public async Task A_key_with_an_alias_is_also_found_by_its_own_name_in_the_secrets_file()
    {
        var file = FileWith("SILVERMM_SECRET_GITHUB_TOKEN=file-key");

        (await Get(Create(file), Token)).Should().Be(new SecretResult.Found("file-key"));
    }

    [Fact]
    public async Task An_empty_environment_variable_does_not_override()
    {
        _env.Values["NEXUS_API_KEY"] = "";
        await _inner.SetAsync(ApiKey, "stored", CancellationToken.None);

        (await Get(Create(), ApiKey)).Should().Be(new SecretResult.Found("stored"));
    }

    [Fact]
    public async Task Set_and_delete_of_a_key_pinned_by_the_environment_are_read_only_and_leave_the_inner_store_alone()
    {
        _env.Values["NEXUS_API_KEY"] = "pinned";
        var store = Create();

        (await store.SetAsync(ApiKey, "new", CancellationToken.None)).Should().Be(new SecretWriteResult.ReadOnly("environment variable NEXUS_API_KEY"));
        (await store.DeleteAsync(ApiKey, CancellationToken.None)).Should().Be(new SecretWriteResult.ReadOnly("environment variable NEXUS_API_KEY"));
        _inner.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Set_of_a_key_pinned_by_its_own_variable_names_that_variable()
    {
        _env.Values["SILVERMM_SECRET_FROMSOFT_ERSC_PASSWORD"] = "pinned";

        (await Create().SetAsync(Other, "new", CancellationToken.None))
            .Should().Be(new SecretWriteResult.ReadOnly("environment variable SILVERMM_SECRET_FROMSOFT_ERSC_PASSWORD"));
    }

    [Fact]
    public async Task Set_of_a_key_pinned_by_the_secrets_file_names_the_file()
    {
        var file = FileWith("NEXUS_API_KEY=pinned");
        var store = Create(file);

        (await store.SetAsync(ApiKey, "new", CancellationToken.None))
            .Should().Be(new SecretWriteResult.ReadOnly($"--secrets-file {file.Path}"));
        (await store.DeleteAsync(ApiKey, CancellationToken.None))
            .Should().Be(new SecretWriteResult.ReadOnly($"--secrets-file {file.Path}"));
        _inner.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Set_and_delete_of_an_unpinned_key_reach_the_inner_store()
    {
        _env.Values["NEXUS_API_KEY"] = "pinned";
        var store = Create();

        (await store.SetAsync(Token, "t", CancellationToken.None)).Should().BeOfType<SecretWriteResult.Written>();
        (await store.DeleteAsync(Token, CancellationToken.None)).Should().BeOfType<SecretWriteResult.Written>();
        _inner.Calls.Should().Equal("Set github.token", "Delete github.token");
    }

    [Fact]
    public async Task A_group_readable_secrets_file_reads_with_one_warning()
    {
        var path = Path.Combine(_scratch, "loose.env");
        System.IO.File.WriteAllText(path, "NEXUS_API_KEY=abc");
        System.IO.File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        var logger = new CapturingLogger();
        var store = Create(new SecretsFileReader(path, logger));

        (await Get(store, ApiKey)).Should().Be(new SecretResult.Found("abc"));
        (await Get(store, ApiKey)).Should().Be(new SecretResult.Found("abc"));

        logger.Warnings.Should().ContainSingle();
    }

    [Fact]
    public async Task A_malformed_secrets_file_makes_every_get_unavailable_naming_the_line()
    {
        await _inner.SetAsync(ApiKey, "stored", CancellationToken.None);
        var store = Create(FileWith("OK=1\nthis is not an assignment\n"));

        foreach (var key in new[] { ApiKey, Token })
        {
            var result = await Get(store, key);
            var unavailable = result.Should().BeOfType<SecretResult.Unavailable>().Subject;
            unavailable.Kind.Should().Be(SecretUnavailableKind.Malformed);
            unavailable.Reason.Should().Contain("line 2");
        }
    }

    [Fact]
    public async Task A_malformed_secrets_file_does_not_hide_an_environment_override()
    {
        _env.Values["NEXUS_API_KEY"] = "from-env";
        var store = Create(FileWith("not an assignment"));

        (await Get(store, ApiKey)).Should().Be(new SecretResult.Found("from-env"));
    }

    [Fact]
    public async Task A_malformed_secrets_file_makes_writes_unavailable_rather_than_guessing()
    {
        var store = Create(FileWith("not an assignment"));

        var result = await store.SetAsync(ApiKey, "v", CancellationToken.None);

        result.Should().BeOfType<SecretWriteResult.Unavailable>().Which.Kind.Should().Be(SecretUnavailableKind.Malformed);
        _inner.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Describe_lists_the_overridden_keys_and_keeps_the_inner_status()
    {
        _env.Values["NEXUS_API_KEY"] = "a";
        var file = FileWith("SILVERMM_SECRET_FROMSOFT_ERSC_PASSWORD=b");

        var status = await Create(file).DescribeAsync(CancellationToken.None);

        status.OverriddenKeys.Should().BeEquivalentTo([ApiKey, Other]);
        status.Backend.Should().Be(SecretBackendKind.Memory);
    }

    [Fact]
    public async Task Changed_status_changed_and_unlock_pass_through_to_the_inner_store()
    {
        var store = Create();
        var changed = new List<SecretKey>();
        var statuses = new List<SecretStoreStatus>();
        using var _ = store.Changed.Subscribe(x => changed.Add(x));
        using var __ = store.StatusChanged.Subscribe(x => statuses.Add(x));

        await _inner.SetAsync(Token, "t", CancellationToken.None);
        _inner.Status = _inner.Status with { Locked = true };
        await store.UnlockAsync(CancellationToken.None);

        changed.Should().Equal(Token);
        statuses.Should().ContainSingle();
        _inner.Calls.Should().Contain("Unlock");
    }

    [Fact]
    public void Overrides_name_each_pinned_key_with_where_its_value_comes_from()
    {
        _env.Values["NEXUS_API_KEY"] = "from-env";
        var file = FileWith("GITHUB_TOKEN=from-file");

        Create(file).Overrides().Should().Equal(
            new SecretOverride(ApiKey, "environment variable NEXUS_API_KEY"),
            new SecretOverride(Token, $"--secrets-file {System.IO.Path.Combine(_scratch, "secrets.env")}"));
    }

    [Fact]
    public void Overrides_are_empty_when_nothing_pins_a_key()
    {
        Create().Overrides().Should().BeEmpty();
    }
}
