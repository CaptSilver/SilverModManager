using R3;
using FluentAssertions;
using SilverModManager.Abstractions.Secrets;
using SilverModManager.Secrets.Chain;
using Xunit;

namespace SilverModManager.Secrets.Tests.Chain;

public class FallbackSecretStoreTests
{
    private static readonly SecretKey ApiKey = new("nexus.api-key");
    private static readonly SecretKey Token = new("github.token");

    private readonly InMemorySecretStore _service = new();
    private readonly InMemorySecretStore _file = new()
    {
        Status = new SecretStoreStatus(SecretBackendKind.File, "/tmp/secrets.json", Locked: false, Unencrypted: true, Writable: true, []),
    };
    private readonly CapturingLogger _logger = new();

    private FallbackSecretStore Create(SecretStoreMode mode = SecretStoreMode.NonInteractive) => new(_service, _file, mode, _logger);

    private static async Task<string?> Value(ISecretStore store, SecretKey key)
        => (await store.GetAsync(key, CancellationToken.None)) is SecretResult.Found found ? found.Value : null;

    private static void Down(InMemorySecretStore store, SecretUnavailableKind kind) => store.Availability = (kind, kind.ToString());

    [Fact]
    public async Task A_service_hit_with_no_file_copy_is_the_answer()
    {
        await _service.SetAsync(ApiKey, "from-service", CancellationToken.None);

        (await Value(Create(), ApiKey)).Should().Be("from-service");
        _file.Calls.Should().NotContain("Set nexus.api-key");
    }

    [Fact]
    public async Task A_service_hit_with_a_file_copy_moves_the_files_value_into_the_service_and_empties_the_file()
    {
        await _service.SetAsync(ApiKey, "older", CancellationToken.None);
        await _file.SetAsync(ApiKey, "newer", CancellationToken.None);

        (await Value(Create(), ApiKey)).Should().Be("newer");

        (await _service.GetAsync(ApiKey, CancellationToken.None)).Should().Be(new SecretResult.Found("newer"));
        (await _file.GetAsync(ApiKey, CancellationToken.None)).Should().BeOfType<SecretResult.NotFound>();
    }

    [Fact]
    public async Task A_service_miss_with_a_file_copy_moves_the_value_into_the_service_and_empties_the_file()
    {
        await _file.SetAsync(ApiKey, "only-in-file", CancellationToken.None);

        (await Value(Create(), ApiKey)).Should().Be("only-in-file");

        (await _service.GetAsync(ApiKey, CancellationToken.None)).Should().Be(new SecretResult.Found("only-in-file"));
        (await _file.GetAsync(ApiKey, CancellationToken.None)).Should().BeOfType<SecretResult.NotFound>();
    }

    [Fact]
    public async Task A_service_miss_with_no_file_copy_is_not_found()
    {
        (await Create().GetAsync(ApiKey, CancellationToken.None)).Should().BeOfType<SecretResult.NotFound>();
    }

    [Fact]
    public async Task The_file_copy_stays_when_the_move_into_the_service_fails()
    {
        await _file.SetAsync(ApiKey, "keep-me", CancellationToken.None);
        var store = new FallbackSecretStore(new FailingWriteStore(_service), _file, SecretStoreMode.NonInteractive, _logger);

        (await Value(store, ApiKey)).Should().Be("keep-me");
        (await _file.GetAsync(ApiKey, CancellationToken.None)).Should().Be(new SecretResult.Found("keep-me"));
    }

    [Theory]
    [InlineData(SecretUnavailableKind.Locked)]
    [InlineData(SecretUnavailableKind.NoBus)]
    [InlineData(SecretUnavailableKind.Timeout)]
    public async Task An_unavailable_service_with_a_file_copy_answers_from_the_file(SecretUnavailableKind kind)
    {
        Down(_service, kind);
        await _file.SetAsync(ApiKey, "from-file", CancellationToken.None);

        (await Value(Create(), ApiKey)).Should().Be("from-file");
        (await _file.GetAsync(ApiKey, CancellationToken.None)).Should().Be(new SecretResult.Found("from-file"));
    }

    [Fact]
    public async Task An_unavailable_service_with_no_file_copy_returns_the_services_unavailable_not_not_found()
    {
        Down(_service, SecretUnavailableKind.Locked);

        var result = await Create().GetAsync(ApiKey, CancellationToken.None);

        result.Should().BeOfType<SecretResult.Unavailable>().Which.Kind.Should().Be(SecretUnavailableKind.Locked);
    }

    [Fact]
    public async Task A_set_with_the_service_answering_writes_the_incoming_value_and_no_stale_copy_survives()
    {
        await _file.SetAsync(ApiKey, "stale", CancellationToken.None);

        (await Create().SetAsync(ApiKey, "incoming", CancellationToken.None)).Should().BeOfType<SecretWriteResult.Written>();

        (await _service.GetAsync(ApiKey, CancellationToken.None)).Should().Be(new SecretResult.Found("incoming"));
        (await _file.GetAsync(ApiKey, CancellationToken.None)).Should().BeOfType<SecretResult.NotFound>();
        (await _file.ListAsync(CancellationToken.None)).Should().BeEmpty();
    }

    [Theory]
    [InlineData(SecretUnavailableKind.Locked)]
    [InlineData(SecretUnavailableKind.Dismissed)]
    [InlineData(SecretUnavailableKind.Timeout)]
    [InlineData(SecretUnavailableKind.NoDefaultCollection)]
    public async Task A_set_while_a_keyring_exists_but_cannot_take_it_returns_that_result_and_writes_nothing(SecretUnavailableKind kind)
    {
        Down(_service, kind);

        var result = await Create().SetAsync(ApiKey, "v", CancellationToken.None);

        result.Should().BeOfType<SecretWriteResult.Unavailable>().Which.Kind.Should().Be(kind);
        _file.Calls.Should().NotContain(call => call.StartsWith("Set"));
        (await _file.ListAsync(CancellationToken.None)).Should().BeEmpty();
    }

    [Theory]
    [InlineData(SecretUnavailableKind.ServiceAbsent)]
    [InlineData(SecretUnavailableKind.NoBus)]
    public async Task A_set_with_no_keyring_writes_the_file_and_the_status_says_unencrypted(SecretUnavailableKind kind)
    {
        Down(_service, kind);
        var store = Create();

        (await store.SetAsync(ApiKey, "v", CancellationToken.None)).Should().BeOfType<SecretWriteResult.Written>();

        (await _file.GetAsync(ApiKey, CancellationToken.None)).Should().Be(new SecretResult.Found("v"));
        var status = await store.DescribeAsync(CancellationToken.None);
        status.Unencrypted.Should().BeTrue();
        status.Backend.Should().Be(SecretBackendKind.File);
        status.Writable.Should().BeTrue();
    }

    [Fact]
    public async Task Only_the_first_write_to_the_file_logs_a_warning()
    {
        Down(_service, SecretUnavailableKind.ServiceAbsent);
        var store = Create();

        await store.SetAsync(ApiKey, "1", CancellationToken.None);
        await store.SetAsync(Token, "2", CancellationToken.None);

        _logger.Warnings.Should().ContainSingle();
    }

    [Fact]
    public async Task A_healthy_service_reports_its_own_status_and_is_writable()
    {
        _service.Status = new SecretStoreStatus(SecretBackendKind.SecretService, "kdewallet", Locked: false, Unencrypted: false, Writable: true, []);

        var status = await Create().DescribeAsync(CancellationToken.None);

        status.Backend.Should().Be(SecretBackendKind.SecretService);
        status.Unencrypted.Should().BeFalse();
        status.Writable.Should().BeTrue();
    }

    [Fact]
    public async Task A_locked_service_is_not_writable_even_though_a_file_exists()
    {
        _service.Status = new SecretStoreStatus(SecretBackendKind.SecretService, "kdewallet", Locked: true, Unencrypted: false, Writable: false, []);

        var status = await Create().DescribeAsync(CancellationToken.None);

        status.Locked.Should().BeTrue();
        status.Writable.Should().BeFalse();
    }

    [Fact]
    public async Task A_file_holding_secrets_is_reported_unencrypted_before_any_read_reconciles_it()
    {
        _service.Status = new SecretStoreStatus(SecretBackendKind.SecretService, "kdewallet", Locked: false, Unencrypted: false, Writable: true, []);
        await _file.SetAsync(ApiKey, "left-over", CancellationToken.None);

        (await Create().DescribeAsync(CancellationToken.None)).Unencrypted.Should().BeTrue();
    }

    [Fact]
    public async Task Without_a_service_the_file_is_the_store()
    {
        var store = new FallbackSecretStore(null, _file, SecretStoreMode.NonInteractive, _logger);

        (await store.SetAsync(ApiKey, "v", CancellationToken.None)).Should().BeOfType<SecretWriteResult.Written>();
        (await Value(store, ApiKey)).Should().Be("v");
        var status = await store.DescribeAsync(CancellationToken.None);
        status.Unencrypted.Should().BeTrue();
        status.Writable.Should().BeTrue();
    }

    [Fact]
    public async Task Delete_removes_the_key_from_both_parts()
    {
        await _service.SetAsync(ApiKey, "a", CancellationToken.None);
        await _file.SetAsync(ApiKey, "b", CancellationToken.None);

        (await Create().DeleteAsync(ApiKey, CancellationToken.None)).Should().BeOfType<SecretWriteResult.Written>();

        (await _service.GetAsync(ApiKey, CancellationToken.None)).Should().BeOfType<SecretResult.NotFound>();
        (await _file.GetAsync(ApiKey, CancellationToken.None)).Should().BeOfType<SecretResult.NotFound>();
    }

    [Fact]
    public async Task Delete_with_a_locked_keyring_reports_it_but_still_removes_the_file_copy()
    {
        await _file.SetAsync(ApiKey, "b", CancellationToken.None);
        Down(_service, SecretUnavailableKind.Locked);

        var result = await Create().DeleteAsync(ApiKey, CancellationToken.None);

        result.Should().BeOfType<SecretWriteResult.Unavailable>().Which.Kind.Should().Be(SecretUnavailableKind.Locked);
        (await _file.GetAsync(ApiKey, CancellationToken.None)).Should().BeOfType<SecretResult.NotFound>();
    }

    [Fact]
    public async Task List_is_the_union_of_both_parts()
    {
        await _service.SetAsync(ApiKey, "a", CancellationToken.None);
        await _file.SetAsync(Token, "b", CancellationToken.None);
        await _file.SetAsync(ApiKey, "c", CancellationToken.None);

        (await Create().ListAsync(CancellationToken.None)).Should().BeEquivalentTo([ApiKey, Token]);
    }

    [Fact]
    public async Task Interactive_unlock_reaches_the_service()
    {
        await Create(SecretStoreMode.Interactive).UnlockAsync(CancellationToken.None);

        _service.Calls.Should().Contain("Unlock");
    }

    [Fact]
    public async Task Non_interactive_unlock_never_reaches_the_service()
    {
        await Create(SecretStoreMode.NonInteractive).UnlockAsync(CancellationToken.None);

        _service.Calls.Should().NotContain("Unlock");
    }

    [Fact]
    public async Task Status_changes_from_either_part_reach_the_subscriber()
    {
        var store = Create();
        var statuses = new List<SecretStoreStatus>();
        using var _ = store.StatusChanged.Subscribe(x => statuses.Add(x));

        _service.Status = _service.Status with { Detail = "service" };
        _file.Status = _file.Status with { Detail = "file" };

        statuses.Select(s => s.Detail).Should().Equal("service", "file");
    }

    [Fact]
    public async Task Changed_from_either_part_reaches_the_subscriber()
    {
        var store = Create();
        var keys = new List<SecretKey>();
        using var _ = store.Changed.Subscribe(x => keys.Add(x));

        await _service.SetAsync(ApiKey, "a", CancellationToken.None);
        await _file.SetAsync(Token, "b", CancellationToken.None);

        keys.Should().Equal(ApiKey, Token);
    }

    /// <summary>A service that reads normally but refuses every write.</summary>
    private sealed class FailingWriteStore(ISecretStore inner) : DelegatingSecretStore(inner)
    {
        public override ValueTask<SecretWriteResult> SetAsync(SecretKey key, string value, CancellationToken cancellationToken)
            => new(new SecretWriteResult.Unavailable(SecretUnavailableKind.Faulted, "write refused"));
    }
}
