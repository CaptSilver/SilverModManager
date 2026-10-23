using FluentAssertions;
using Xunit;
using NexusMods.Abstractions.Secrets;
using R3;

namespace NexusMods.Secrets.Tests;

public class InMemorySecretStoreTests
{
    private static readonly SecretKey ApiKey = new("nexus.api-key");
    private static readonly SecretKey Oauth = new("gog.oauth");

    private readonly InMemorySecretStore _store = new();

    [Fact]
    public async Task Set_then_get_round_trips()
    {
        (await _store.SetAsync(ApiKey, "abc", CancellationToken.None)).Should().BeOfType<SecretWriteResult.Written>();

        var result = await _store.GetAsync(ApiKey, CancellationToken.None);
        result.Should().Be(new SecretResult.Found("abc"));
    }

    [Fact]
    public async Task Get_of_an_absent_key_is_not_found()
    {
        (await _store.GetAsync(ApiKey, CancellationToken.None)).Should().BeOfType<SecretResult.NotFound>();
    }

    [Fact]
    public async Task Set_replaces_the_previous_value()
    {
        await _store.SetAsync(ApiKey, "one", CancellationToken.None);
        await _store.SetAsync(ApiKey, "two", CancellationToken.None);

        (await _store.GetAsync(ApiKey, CancellationToken.None)).Should().Be(new SecretResult.Found("two"));
    }

    [Fact]
    public async Task Delete_removes_the_value()
    {
        await _store.SetAsync(ApiKey, "abc", CancellationToken.None);

        (await _store.DeleteAsync(ApiKey, CancellationToken.None)).Should().BeOfType<SecretWriteResult.Written>();
        (await _store.GetAsync(ApiKey, CancellationToken.None)).Should().BeOfType<SecretResult.NotFound>();
    }

    [Fact]
    public async Task List_returns_the_stored_keys()
    {
        await _store.SetAsync(ApiKey, "a", CancellationToken.None);
        await _store.SetAsync(Oauth, "b", CancellationToken.None);

        (await _store.ListAsync(CancellationToken.None)).Should().BeEquivalentTo([ApiKey, Oauth]);
    }

    [Fact]
    public async Task Changed_fires_once_per_set_and_once_per_delete_of_a_present_key()
    {
        var changed = new List<SecretKey>();
        using var subscription = _store.Changed.Subscribe(changed.Add);

        await _store.SetAsync(ApiKey, "a", CancellationToken.None);
        changed.Should().Equal(ApiKey);

        await _store.DeleteAsync(ApiKey, CancellationToken.None);
        changed.Should().Equal(ApiKey, ApiKey);
    }

    [Fact]
    public async Task Changed_does_not_fire_when_deleting_an_absent_key()
    {
        var changed = new List<SecretKey>();
        using var subscription = _store.Changed.Subscribe(changed.Add);

        (await _store.DeleteAsync(ApiKey, CancellationToken.None)).Should().BeOfType<SecretWriteResult.Written>();

        changed.Should().BeEmpty();
    }

    [Fact]
    public void Default_status_is_a_writable_unencrypted_free_memory_store()
    {
        _store.Status.Should().Match<SecretStoreStatus>(s =>
            s.Backend == SecretBackendKind.Memory && s.Writable && !s.Unencrypted && !s.Locked && s.OverriddenKeys.Count == 0
        );
    }

    [Fact]
    public async Task Describe_returns_the_current_status()
    {
        (await _store.DescribeAsync(CancellationToken.None)).Should().Be(_store.Status);
    }

    [Fact]
    public async Task Setting_the_status_fires_StatusChanged_and_unlock_returns_it()
    {
        var seen = new List<SecretStoreStatus>();
        using var subscription = _store.StatusChanged.Subscribe(seen.Add);

        var unlocked = _store.Status with { Locked = false, Detail = "unlocked" };
        _store.Status = unlocked;

        seen.Should().Equal(unlocked);
        (await _store.UnlockAsync(CancellationToken.None)).Should().Be(unlocked);
    }

    [Theory]
    [InlineData(SecretUnavailableKind.Locked)]
    [InlineData(SecretUnavailableKind.NoBus)]
    [InlineData(SecretUnavailableKind.Timeout)]
    public async Task Availability_makes_every_data_call_unavailable_with_the_configured_kind(SecretUnavailableKind kind)
    {
        await _store.SetAsync(ApiKey, "stored-before", CancellationToken.None);
        _store.Availability = (kind, "because");

        (await _store.GetAsync(ApiKey, CancellationToken.None)).Should().Be(new SecretResult.Unavailable(kind, "because"));
        (await _store.SetAsync(ApiKey, "x", CancellationToken.None)).Should().Be(new SecretWriteResult.Unavailable(kind, "because"));
        (await _store.DeleteAsync(ApiKey, CancellationToken.None)).Should().Be(new SecretWriteResult.Unavailable(kind, "because"));
    }

    [Fact]
    public async Task Unavailable_calls_change_nothing_and_fire_nothing()
    {
        await _store.SetAsync(ApiKey, "kept", CancellationToken.None);
        var changed = new List<SecretKey>();
        using var subscription = _store.Changed.Subscribe(changed.Add);

        _store.Availability = (SecretUnavailableKind.Locked, "locked");
        await _store.SetAsync(ApiKey, "lost", CancellationToken.None);
        await _store.DeleteAsync(ApiKey, CancellationToken.None);
        _store.Availability = null;

        changed.Should().BeEmpty();
        (await _store.GetAsync(ApiKey, CancellationToken.None)).Should().Be(new SecretResult.Found("kept"));
    }

    [Fact]
    public async Task List_is_empty_while_unavailable()
    {
        await _store.SetAsync(ApiKey, "a", CancellationToken.None);
        _store.Availability = (SecretUnavailableKind.Faulted, "broken");

        (await _store.ListAsync(CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task Describe_and_unlock_report_the_status_even_while_unavailable()
    {
        _store.Availability = (SecretUnavailableKind.Locked, "locked");
        _store.Status = _store.Status with { Locked = true, Writable = false };

        (await _store.DescribeAsync(CancellationToken.None)).Should().Be(_store.Status);
        (await _store.UnlockAsync(CancellationToken.None)).Should().Be(_store.Status);
    }

    [Fact]
    public async Task Calls_records_every_call_in_order()
    {
        await _store.GetAsync(ApiKey, CancellationToken.None);
        await _store.SetAsync(ApiKey, "v", CancellationToken.None);
        await _store.DeleteAsync(ApiKey, CancellationToken.None);
        await _store.ListAsync(CancellationToken.None);
        await _store.DescribeAsync(CancellationToken.None);
        await _store.UnlockAsync(CancellationToken.None);

        _store.Calls.Should().Equal("Get nexus.api-key", "Set nexus.api-key", "Delete nexus.api-key", "List", "Describe", "Unlock");
    }

    [Fact]
    public async Task Calls_never_contain_the_value()
    {
        await _store.SetAsync(ApiKey, "super-secret", CancellationToken.None);

        _store.Calls.Should().NotContain(c => c.Contains("super-secret"));
    }
}
