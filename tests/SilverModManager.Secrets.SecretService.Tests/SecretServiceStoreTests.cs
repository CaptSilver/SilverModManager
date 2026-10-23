using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Abstractions.Secrets;
using SilverModManager.Sdk;
using R3;
using Xunit;

namespace SilverModManager.Secrets.SecretService.Tests;

public class SecretServiceStoreTests : IDisposable
{
    private const string Address = "unix:path=/run/user/1000/test-bus";
    private static readonly SecretKey ApiKey = new("nexus.api-key");
    private static readonly SecretKey Oauth = new("gog.oauth");
    private static readonly CancellationToken NoCancel = CancellationToken.None;

    private static readonly SecretServiceTimeouts ShortTimeouts = new()
    {
        Interactive = TimeSpan.FromMilliseconds(400),
        NonInteractive = TimeSpan.FromMilliseconds(250),
        Unlock = TimeSpan.FromMilliseconds(600),
    };

    private readonly FakeSecretServiceConnection _connection = new FakeSecretServiceConnection().WithUnlockedDefaultCollection();
    private readonly List<SecretServiceStore> _stores = [];

    public void Dispose()
    {
        foreach (var store in _stores) store.Dispose();
    }

    private SecretServiceStore CreateStore(SecretStoreMode mode = SecretStoreMode.NonInteractive, string? address = Address)
    {
        var env = new DictionaryEnvironmentReader(address is null ? [] : new() { ["DBUS_SESSION_BUS_ADDRESS"] = address });
        var store = new SecretServiceStore(_connection, env, new SecretStoreOptions { Mode = mode }, NullLogger.Instance, ShortTimeouts);
        _stores.Add(store);
        return store;
    }

    private static SearchResult Hits(params string[] unlocked) => new(unlocked, []);
    private static SearchResult LockedHits(params string[] locked) => new([], locked);
    private static readonly SearchResult NoHits = new([], []);

    // --- happy paths --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Set_creates_an_item_with_the_agreed_attribute_layout()
    {
        _connection.CreateItem.Always(new CreateItemResult(FakeSecretServiceConnection.Item1, null));

        var result = await CreateStore().SetAsync(ApiKey, "s3cret", NoCancel);

        result.Should().BeOfType<SecretWriteResult.Written>();
        var call = _connection.CreateItemCalls.Should().ContainSingle().Subject;
        call.Collection.Should().Be(FakeSecretServiceConnection.DefaultCollection);
        call.Label.Should().Be("SilverModManager: nexus.api-key");
        call.Replace.Should().BeTrue();
        call.Value.Should().Be("s3cret");
        call.Attributes.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["xdg:schema"] = "io.github.CaptSilver.SilverModManager.Secret",
            ["application"] = "SilverModManager",
            ["key"] = "nexus.api-key",
        });
    }

    [Fact]
    public async Task Item_attributes_never_carry_the_secret_value()
    {
        _connection.CreateItem.Always(new CreateItemResult(FakeSecretServiceConnection.Item1, null));
        _connection.Search.Always(NoHits);
        var store = CreateStore();

        await store.SetAsync(ApiKey, "hunter2-value", NoCancel);
        await store.GetAsync(ApiKey, NoCancel);

        var allAttributes = _connection.CreateItemCalls.Select(c => c.Attributes)
            .Concat(_connection.SearchAttributes)
            .SelectMany(a => a.Values);
        allAttributes.Should().NotContain(v => v.Contains("hunter2-value"));
        // No account name or user name either: nothing but the three agreed values.
        _connection.CreateItemCalls.Single().Attributes.Keys.Should().BeEquivalentTo("xdg:schema", "application", "key");
        _connection.CreateItemCalls.Single().Label.Should().NotContain("hunter2-value");
    }

    [Fact]
    public async Task Get_reads_the_first_unlocked_hit()
    {
        _connection.Search.Always(Hits("/item/a", "/item/b"));
        _connection.GetSecret.Always("the-value");

        var result = await CreateStore().GetAsync(ApiKey, NoCancel);

        result.Should().Be(new SecretResult.Found("the-value"));
        _connection.CountCalls("GetSecret").Should().Be(1);
        _connection.SearchAttributes.Should().ContainSingle().Which.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["xdg:schema"] = "io.github.CaptSilver.SilverModManager.Secret",
            ["application"] = "SilverModManager",
            ["key"] = "nexus.api-key",
        });
    }

    [Fact]
    public async Task Get_of_an_absent_key_is_not_found()
    {
        _connection.Search.Always(NoHits);

        (await CreateStore().GetAsync(ApiKey, NoCancel)).Should().BeOfType<SecretResult.NotFound>();
        _connection.CountCalls("GetSecret").Should().Be(0);
    }

    [Fact]
    public async Task Delete_removes_every_matching_item()
    {
        _connection.Search.Always(Hits("/item/a", "/item/b"));
        _connection.Delete.Always((string?)null);

        var result = await CreateStore().DeleteAsync(ApiKey, NoCancel);

        result.Should().BeOfType<SecretWriteResult.Written>();
        _connection.CountCalls("Delete").Should().Be(2);
    }

    [Fact]
    public async Task Delete_of_an_absent_key_succeeds_without_deleting()
    {
        _connection.Search.Always(NoHits);

        (await CreateStore().DeleteAsync(ApiKey, NoCancel)).Should().BeOfType<SecretWriteResult.Written>();
        _connection.CountCalls("Delete").Should().Be(0);
    }

    [Fact]
    public async Task Changed_fires_after_a_written_set_and_delete()
    {
        _connection.CreateItem.Always(new CreateItemResult(FakeSecretServiceConnection.Item1, null));
        _connection.Search.Always(Hits("/item/a"));
        _connection.Delete.Always((string?)null);
        var store = CreateStore();
        var changed = new List<SecretKey>();
        using var _ = store.Changed.Subscribe(changed.Add);

        await store.SetAsync(ApiKey, "v", NoCancel);
        await store.DeleteAsync(Oauth, NoCancel);

        changed.Should().Equal(ApiKey, Oauth);
    }

    [Fact]
    public async Task Changed_stays_quiet_when_the_write_did_not_land()
    {
        _connection.CreateItem.Always(new CreateItemResult(null, "/prompt/1"));
        var store = CreateStore();
        var changed = new List<SecretKey>();
        using var _ = store.Changed.Subscribe(changed.Add);

        await store.SetAsync(ApiKey, "v", NoCancel);

        changed.Should().BeEmpty();
    }

    [Fact]
    public async Task List_returns_the_key_attribute_of_every_hit()
    {
        _connection.Search.Always(Hits("/item/a", "/item/b"));
        _connection.GetAttributes
            .Then(new Dictionary<string, string> { ["key"] = "nexus.api-key", ["application"] = "SilverModManager" })
            .Then(new Dictionary<string, string> { ["key"] = "gog.oauth", ["application"] = "SilverModManager" });

        var keys = await CreateStore().ListAsync(NoCancel);

        keys.Should().BeEquivalentTo([ApiKey, Oauth]);
        // Searching by key would defeat the purpose: only the schema and application narrow it.
        _connection.SearchAttributes.Should().ContainSingle().Which.Keys.Should().BeEquivalentTo("xdg:schema", "application");
    }

    [Fact]
    public async Task List_skips_items_whose_key_is_not_a_valid_secret_key()
    {
        _connection.Search.Always(Hits("/item/a", "/item/b"));
        _connection.GetAttributes
            .Then(new Dictionary<string, string> { ["key"] = "Not A Key" })
            .Then(new Dictionary<string, string> { ["key"] = "gog.oauth" });

        (await CreateStore().ListAsync(NoCancel)).Should().Equal(Oauth);
    }

    [Fact]
    public async Task List_is_empty_when_the_service_cannot_be_read()
    {
        (await CreateStore(address: null).ListAsync(NoCancel)).Should().BeEmpty();
    }

    // --- availability -------------------------------------------------------------------------------------------

    [Fact]
    public async Task No_bus_address_is_unavailable_without_connecting()
    {
        var store = CreateStore(address: null);

        (await store.GetAsync(ApiKey, NoCancel)).Should().BeOfType<SecretResult.Unavailable>()
            .Which.Kind.Should().Be(SecretUnavailableKind.NoBus);
        (await store.SetAsync(ApiKey, "v", NoCancel)).Should().BeOfType<SecretWriteResult.Unavailable>()
            .Which.Kind.Should().Be(SecretUnavailableKind.NoBus);
        (await store.DeleteAsync(ApiKey, NoCancel)).Should().BeOfType<SecretWriteResult.Unavailable>()
            .Which.Kind.Should().Be(SecretUnavailableKind.NoBus);

        _connection.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Connects_to_exactly_the_address_in_the_environment()
    {
        _connection.Search.Always(NoHits);

        await CreateStore().GetAsync(ApiKey, NoCancel);

        _connection.ConnectedAddresses.Should().Equal(Address);
    }

    [Fact]
    public async Task A_bus_that_refuses_the_connection_is_no_bus_and_is_tried_again_next_call()
    {
        // A stale DBUS_SESSION_BUS_ADDRESS has to read as "no bus", or the fallback chain never writes to the file.
        _connection.Connect.AlwaysThrow(new SecretServiceException(SecretServiceFailure.Other, "Connection refused"));
        var store = CreateStore();

        (await store.SetAsync(ApiKey, "v", NoCancel)).Should().BeOfType<SecretWriteResult.Unavailable>()
            .Which.Kind.Should().Be(SecretUnavailableKind.NoBus);
        (await store.GetAsync(ApiKey, NoCancel)).Should().BeOfType<SecretResult.Unavailable>()
            .Which.Kind.Should().Be(SecretUnavailableKind.NoBus);

        _connection.CountCalls("Connect").Should().Be(2);
        _connection.Calls.Should().NotContain(c => c.StartsWith("StartServiceByName"));
    }

    [Fact]
    public async Task Connects_once_for_many_calls()
    {
        _connection.Search.Always(NoHits);
        var store = CreateStore();

        await store.GetAsync(ApiKey, NoCancel);
        await store.GetAsync(Oauth, NoCancel);

        _connection.CountCalls("Connect").Should().Be(1);
    }

    [Fact]
    public async Task Reconnects_after_the_connection_dropped()
    {
        _connection.Search
            .ThenThrow(new SecretServiceException(SecretServiceFailure.Disconnected, "closed"))
            .Always(NoHits);
        var store = CreateStore();

        (await store.GetAsync(ApiKey, NoCancel)).Should().BeOfType<SecretResult.Unavailable>()
            .Which.Kind.Should().Be(SecretUnavailableKind.Faulted);
        (await store.GetAsync(ApiKey, NoCancel)).Should().BeOfType<SecretResult.NotFound>();

        _connection.CountCalls("Connect").Should().Be(2);
    }

    [Theory]
    [InlineData(SecretStoreMode.Interactive)]
    [InlineData(SecretStoreMode.NonInteractive)]
    public async Task A_connect_that_hangs_is_a_timeout_within_the_modes_limit(SecretStoreMode mode)
    {
        _connection.Connect.Always(async ct => { await Task.Delay(Timeout.Infinite, ct); return true; });
        var limit = mode == SecretStoreMode.Interactive ? ShortTimeouts.Interactive : ShortTimeouts.NonInteractive;

        var watch = Stopwatch.StartNew();
        var result = await CreateStore(mode).GetAsync(ApiKey, NoCancel);
        watch.Stop();

        result.Should().BeOfType<SecretResult.Unavailable>().Which.Kind.Should().Be(SecretUnavailableKind.Timeout);
        watch.Elapsed.Should().BeGreaterThanOrEqualTo(limit - TimeSpan.FromMilliseconds(30));
        watch.Elapsed.Should().BeLessThan(limit + TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task A_call_that_hangs_is_a_timeout_for_writes_too()
    {
        _connection.CreateItem.Always(async ct => { await Task.Delay(Timeout.Infinite, ct); return null!; });

        var result = await CreateStore().SetAsync(ApiKey, "v", NoCancel);

        result.Should().BeOfType<SecretWriteResult.Unavailable>().Which.Kind.Should().Be(SecretUnavailableKind.Timeout);
    }

    [Fact]
    public async Task Cancelling_the_callers_token_cancels_instead_of_reporting_a_timeout()
    {
        _connection.Search.Always(async ct => { await Task.Delay(Timeout.Infinite, ct); return NoHits; });
        using var cts = new CancellationTokenSource();
        var pending = CreateStore(SecretStoreMode.Interactive).GetAsync(ApiKey, cts.Token).AsTask();

        await cts.CancelAsync();

        await pending.Invoking(p => p).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ServiceUnknown_starts_the_service_once_and_retries()
    {
        _connection.ReadDefaultCollection
            .ThenThrow(new SecretServiceException(SecretServiceFailure.ServiceUnknown, "not provided"))
            .Always(FakeSecretServiceConnection.DefaultCollection);
        _connection.StartServiceByName.Always(true);
        _connection.Search.Always(Hits("/item/a"));
        _connection.GetSecret.Always("v");

        var result = await CreateStore().GetAsync(ApiKey, NoCancel);

        result.Should().Be(new SecretResult.Found("v"));
        _connection.Calls.Should().Contain("StartServiceByName(org.kde.kwalletd6)");
        _connection.Calls.Count(c => c.StartsWith("StartServiceByName")).Should().Be(1);
    }

    [Fact]
    public async Task ServiceUnknown_twice_is_service_absent_after_one_start_attempt()
    {
        _connection.ReadDefaultCollection.AlwaysThrow(new SecretServiceException(SecretServiceFailure.ServiceUnknown, "not provided"));
        _connection.StartServiceByName.Always(true);
        var store = CreateStore();

        (await store.GetAsync(ApiKey, NoCancel)).Should().BeOfType<SecretResult.Unavailable>()
            .Which.Kind.Should().Be(SecretUnavailableKind.ServiceAbsent);

        _connection.Calls.Count(c => c.StartsWith("StartServiceByName")).Should().Be(1);
        _connection.CountCalls("ReadDefaultCollection").Should().Be(2);
    }

    [Fact]
    public async Task A_failing_start_attempt_is_service_absent()
    {
        _connection.ReadDefaultCollection.AlwaysThrow(new SecretServiceException(SecretServiceFailure.ServiceUnknown, "not provided"));
        _connection.StartServiceByName.AlwaysThrow(new SecretServiceException(SecretServiceFailure.ServiceUnknown, "no such unit"));

        (await CreateStore().SetAsync(ApiKey, "v", NoCancel)).Should().BeOfType<SecretWriteResult.Unavailable>()
            .Which.Kind.Should().Be(SecretUnavailableKind.ServiceAbsent);
    }

    [Fact]
    public async Task No_default_collection_is_unavailable_and_nothing_is_created()
    {
        _connection.ReadDefaultCollection.Always((string?)null);
        var store = CreateStore();

        (await store.SetAsync(ApiKey, "v", NoCancel)).Should().BeOfType<SecretWriteResult.Unavailable>()
            .Which.Kind.Should().Be(SecretUnavailableKind.NoDefaultCollection);
        (await store.GetAsync(ApiKey, NoCancel)).Should().BeOfType<SecretResult.Unavailable>()
            .Which.Kind.Should().Be(SecretUnavailableKind.NoDefaultCollection);

        _connection.Calls.Should().NotContain("CreateItem");
    }

    [Fact]
    public void The_connection_interface_cannot_create_a_collection()
    {
        // A store that never makes a collection can't create the user's wallet by accident.
        typeof(ISecretServiceConnection).GetMethods().Select(m => m.Name)
            .Should().NotContain(n => n.Contains("CreateCollection", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(SecretServiceFailure.NoReply)]
    [InlineData(SecretServiceFailure.Disconnected)]
    [InlineData(SecretServiceFailure.Other)]
    public async Task Transport_failures_are_faulted(SecretServiceFailure failure)
    {
        _connection.Search.AlwaysThrow(new SecretServiceException(failure, "boom"));

        (await CreateStore().GetAsync(ApiKey, NoCancel)).Should().BeOfType<SecretResult.Unavailable>()
            .Which.Kind.Should().Be(SecretUnavailableKind.Faulted);
    }

    [Fact]
    public async Task An_unexpected_exception_is_faulted_with_its_message_and_never_thrown()
    {
        _connection.Search.AlwaysThrow(new InvalidCastException("odd reply shape"));
        var store = CreateStore();

        var get = (SecretResult.Unavailable) await store.GetAsync(ApiKey, NoCancel);
        get.Kind.Should().Be(SecretUnavailableKind.Faulted);
        get.Reason.Should().Contain("odd reply shape");

        (await store.DeleteAsync(ApiKey, NoCancel)).Should().BeOfType<SecretWriteResult.Unavailable>()
            .Which.Kind.Should().Be(SecretUnavailableKind.Faulted);

        _connection.IsLocked.AlwaysThrow(new InvalidCastException("odd reply shape"));
        (await store.DescribeAsync(NoCancel)).Writable.Should().BeFalse();
    }

    // --- locks --------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(SecretStoreMode.Interactive)]
    [InlineData(SecretStoreMode.NonInteractive)]
    public async Task A_prompt_from_create_item_is_locked_and_never_prompted(SecretStoreMode mode)
    {
        _connection.CreateItem.Always(new CreateItemResult(null, "/prompt/1"));

        var result = await CreateStore(mode).SetAsync(ApiKey, "v", NoCancel);

        result.Should().BeOfType<SecretWriteResult.Unavailable>().Which.Kind.Should().Be(SecretUnavailableKind.Locked);
        _connection.Calls.Should().NotContain("Prompt");
    }

    [Theory]
    [InlineData(SecretStoreMode.Interactive)]
    [InlineData(SecretStoreMode.NonInteractive)]
    public async Task A_locked_error_from_create_item_is_locked(SecretStoreMode mode)
    {
        _connection.CreateItem.AlwaysThrow(new SecretServiceException(SecretServiceFailure.Locked, "IsLocked"));

        var result = await CreateStore(mode).SetAsync(ApiKey, "v", NoCancel);

        result.Should().BeOfType<SecretWriteResult.Unavailable>().Which.Kind.Should().Be(SecretUnavailableKind.Locked);
    }

    [Theory]
    [InlineData(SecretStoreMode.Interactive)]
    [InlineData(SecretStoreMode.NonInteractive)]
    public async Task A_prompt_from_delete_is_locked_and_never_prompted(SecretStoreMode mode)
    {
        _connection.Search.Always(Hits("/item/a"));
        _connection.Delete.Always("/prompt/2");

        var result = await CreateStore(mode).DeleteAsync(ApiKey, NoCancel);

        result.Should().BeOfType<SecretWriteResult.Unavailable>().Which.Kind.Should().Be(SecretUnavailableKind.Locked);
        _connection.Calls.Should().NotContain("Prompt");
    }

    [Theory]
    [InlineData(SecretStoreMode.Interactive)]
    [InlineData(SecretStoreMode.NonInteractive)]
    public async Task Search_hits_that_are_all_locked_are_locked_not_missing(SecretStoreMode mode)
    {
        _connection.Search.Always(LockedHits("/item/a"));
        var store = CreateStore(mode);

        (await store.GetAsync(ApiKey, NoCancel)).Should().BeOfType<SecretResult.Unavailable>()
            .Which.Kind.Should().Be(SecretUnavailableKind.Locked);
        (await store.DeleteAsync(ApiKey, NoCancel)).Should().BeOfType<SecretWriteResult.Unavailable>()
            .Which.Kind.Should().Be(SecretUnavailableKind.Locked);

        _connection.Calls.Should().NotContain("Prompt").And.NotContain("GetSecret");
    }

    [Fact]
    public async Task Describe_reports_the_collection_and_whether_it_is_locked()
    {
        var unlocked = await CreateStore().DescribeAsync(NoCancel);

        unlocked.Backend.Should().Be(SecretBackendKind.SecretService);
        unlocked.Detail.Should().Be(FakeSecretServiceConnection.DefaultCollection);
        unlocked.Locked.Should().BeFalse();
        unlocked.Writable.Should().BeTrue();
        unlocked.Unencrypted.Should().BeFalse();
        unlocked.OverriddenKeys.Should().BeEmpty();

        _connection.IsLocked.Always(true);
        var locked = await CreateStore().DescribeAsync(NoCancel);
        locked.Locked.Should().BeTrue();
        locked.Writable.Should().BeFalse();
    }

    [Fact]
    public async Task Describe_without_a_bus_is_not_writable()
    {
        var status = await CreateStore(address: null).DescribeAsync(NoCancel);

        status.Backend.Should().Be(SecretBackendKind.SecretService);
        status.Writable.Should().BeFalse();
        status.Locked.Should().BeFalse();
    }

    [Fact]
    public async Task Interactive_unlock_runs_unlock_then_prompt_and_an_accepted_prompt_unlocks()
    {
        _connection.IsLocked.Then(true).Always(false);
        _connection.Unlock.Always(new UnlockResult([], "/prompt/3"));
        _connection.Prompt.Always(false);
        var store = CreateStore(SecretStoreMode.Interactive);
        var emitted = new List<SecretStoreStatus>();
        using var _ = store.StatusChanged.Subscribe(emitted.Add);

        var status = await store.UnlockAsync(NoCancel);

        status.Locked.Should().BeFalse();
        status.Writable.Should().BeTrue();
        _connection.Calls.Should().ContainInOrder("Unlock", "Prompt");
        emitted.Should().ContainSingle().Which.Locked.Should().BeFalse();
    }

    [Fact]
    public async Task A_dismissed_unlock_prompt_leaves_the_store_locked()
    {
        _connection.IsLocked.Always(true);
        _connection.Unlock.Always(new UnlockResult([], "/prompt/3"));
        _connection.Prompt.Always(true);
        var store = CreateStore(SecretStoreMode.Interactive);
        var emitted = new List<SecretStoreStatus>();
        using var _ = store.StatusChanged.Subscribe(emitted.Add);

        var status = await store.UnlockAsync(NoCancel);

        status.Locked.Should().BeTrue();
        status.Writable.Should().BeFalse();
        status.Detail.Should().Contain("dismissed");
        emitted.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unlock_prompt_nobody_answers_gives_up_and_stays_locked()
    {
        _connection.IsLocked.Always(true);
        _connection.Unlock.Always(new UnlockResult([], "/prompt/3"));
        _connection.Prompt.Always(async ct => { await Task.Delay(Timeout.Infinite, ct); return false; });

        var watch = Stopwatch.StartNew();
        var status = await CreateStore(SecretStoreMode.Interactive).UnlockAsync(NoCancel);

        status.Locked.Should().BeTrue();
        watch.Elapsed.Should().BeLessThan(ShortTimeouts.Unlock + TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Unlock_without_a_prompt_is_enough_when_the_service_unlocks_directly()
    {
        _connection.IsLocked.Then(true).Always(false);
        _connection.Unlock.Always(new UnlockResult([FakeSecretServiceConnection.DefaultCollection], null));

        var status = await CreateStore(SecretStoreMode.Interactive).UnlockAsync(NoCancel);

        status.Locked.Should().BeFalse();
        _connection.Calls.Should().NotContain("Prompt");
    }

    [Fact]
    public async Task Non_interactive_unlock_is_describe_and_makes_no_unlock_or_prompt_call()
    {
        _connection.IsLocked.Always(true);
        var store = CreateStore(SecretStoreMode.NonInteractive);

        var status = await store.UnlockAsync(NoCancel);

        status.Should().BeEquivalentTo(await store.DescribeAsync(NoCancel));
        status.Locked.Should().BeTrue();
        _connection.Calls.Should().NotContain("Unlock").And.NotContain("Prompt");
    }

    [Fact]
    public async Task A_locked_state_change_on_the_bus_emits_the_new_status()
    {
        var store = CreateStore();
        var emitted = new List<SecretStoreStatus>();
        using var _ = store.StatusChanged.Subscribe(emitted.Add);
        await store.DescribeAsync(NoCancel); // first contact subscribes to the collection

        _connection.LockedChangedSubject.OnNext(true);
        _connection.LockedChangedSubject.OnNext(false);

        emitted.Select(s => s.Locked).Should().Equal(true, false);
        emitted.Select(s => s.Writable).Should().Equal(false, true);
    }

    [Fact]
    public async Task The_locked_signal_is_watched_once_per_store()
    {
        _connection.Search.Always(NoHits);
        var store = CreateStore();

        await store.GetAsync(ApiKey, NoCancel);
        await store.GetAsync(ApiKey, NoCancel);
        await store.DescribeAsync(NoCancel);

        _connection.CountCalls("LockedChanged").Should().Be(1);
    }

    private sealed class DictionaryEnvironmentReader(Dictionary<string, string> values) : IEnvironmentReader
    {
        public string? Get(string name) => values.GetValueOrDefault(name);
    }
}
