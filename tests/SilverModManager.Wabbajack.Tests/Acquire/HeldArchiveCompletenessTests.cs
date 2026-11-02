using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Library;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Library;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Wabbajack.Library;

namespace SilverModManager.Wabbajack.Tests.Acquire;

/// <summary>
/// A library that imported an archive before it kept the bytes of archives nested inside it holds the outer
/// archive's rows but not the nested archive's file, so a directive that copies the nested archive whole cannot
/// install. Such an archive is not complete for that list and has to be fetched again.
/// </summary>
public class HeldArchiveCompletenessTests(ITestOutputHelper helper) : AcquireTestBase<HeldArchiveCompletenessTests>(helper)
{
    private static readonly RelativePath InnerZip = RelativePath.FromUnsanitizedInput("pack/inner.zip");
    private static readonly RelativePath InnerMember = RelativePath.FromUnsanitizedInput("deep/c.txt");

    private IArchiveAcquirer Acquirer => ServiceProvider.GetRequiredService<IArchiveAcquirer>();
    private ArchiveInbox Inbox => ServiceProvider.GetRequiredService<ArchiveInbox>();
    private ArchiveInventory Inventory => ServiceProvider.GetRequiredService<ArchiveInventory>();
    private ILibraryArchiveLocator Locator => ServiceProvider.GetRequiredService<ILibraryArchiveLocator>();
    private AbsolutePath InboxFolder => ServiceProvider.GetRequiredService<WabbajackPaths>().Inbox;

    /// <summary>A zip holding <c>pack/inner.zip</c>, which holds <c>deep/c.txt</c>.</summary>
    private sealed record NestedZip(TemporaryPath Outer, byte[] OuterBytes, byte[] InnerBytes) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Outer.DisposeAsync();

        public FileIdentity OuterHash => Xx64.Of(OuterBytes);

        public FileIdentity InnerStoreIdentity => FileIdentity.XxHash3(InnerBytes.xxHash3());
    }

    private async Task<NestedZip> BuildNestedZip()
    {
        await using var inner = await CreateTestArchive(new Dictionary<RelativePath, byte[]> { [InnerMember] = Guid.NewGuid().ToByteArray() });
        var innerBytes = await inner.Path.ReadAllBytesAsync();
        var outer = await CreateTestArchive(new Dictionary<RelativePath, byte[]> { [InnerZip] = innerBytes });
        return new NestedZip(outer, await outer.Path.ReadAllBytesAsync(), innerBytes);
    }

    /// <summary>
    /// The rows an import leaves when it keeps no bytes for the nested archive: the outer archive and the inner one
    /// as its member, with nothing in the file store for the inner.
    /// </summary>
    private async Task AddWithoutNestedBytes(NestedZip zip)
    {
        using var tx = Connection.BeginTransaction();
        var outer = NewFile(tx, "outer.zip", zip.OuterBytes);
        var outerArchive = new LibraryArchive.New(tx, outer.Id) { IsArchive = true, LibraryFile = outer };
        var inner = NewFile(tx, "inner.zip", zip.InnerBytes);
        _ = new LibraryArchive.New(tx, inner.Id) { IsArchive = true, LibraryFile = inner };
        _ = new LibraryArchiveFileEntry.New(tx, inner.Id) { Path = InnerZip, LibraryFile = inner, ParentId = outerArchive.Id };
        await tx.Commit();

        (await FileStore.HaveFile(zip.InnerStoreIdentity)).Should().BeFalse("this is how an earlier import left the library");
    }

    private static LibraryFile.New NewFile(ITransaction tx, string name, byte[] bytes)
    {
        var file = new LibraryFile.New(tx, out var id)
        {
            FileName = name,
            Hash = bytes.xxHash3(),
            Size = Size.FromLong(bytes.Length),
            LibraryItem = new LibraryItem.New(tx, id) { Name = name },
        };
        tx.Add(id, LibraryFile.XxHash64, Xx64.Of(bytes).XxHash64Value());
        return file;
    }

    private static FromArchive Copy(NestedZip zip, params RelativePath[] parts)
        => new(RelativePath.FromUnsanitizedInput("mods/A/x.bin"), Xx64.From(1), 1, new ArchiveHashPath(zip.OuterHash, parts));

    private static ArchiveEntry Archive(NestedZip zip, DownloadState state)
        => new(zip.OuterHash, zip.OuterBytes.Length, "outer.zip", "", state);

    private static readonly DownloadState Http = new HttpState(new Uri("https://example.invalid/outer.zip"), []);
    private static readonly DownloadState Prompted = new ManualState(new Uri("https://example.test/m"), "get it");

    private async Task<ArchivePlan> Plan(NestedZip zip, DownloadState source, params Directive[] directives)
    {
        var list = ListWith(Archive(zip, source)) with { Directives = directives };
        return await Inventory.Build(list, Connection.Db, await CreateLoadout());
    }

    [Fact]
    public async Task AHeldArchiveMissingTheNestedArchiveTheListCopiesWholeIsFetchedAgain_OverPlainHttp()
    {
        await using var zip = await BuildNestedZip();
        await AddWithoutNestedBytes(zip);

        var plan = await Plan(zip, Http, Copy(zip, InnerZip));

        var row = plan.Rows.Should().ContainSingle().Subject;
        row.State.Should().Be(ArchiveState.Automatic);
        row.Library.Should().BeNull();
    }

    [Fact]
    public async Task AHeldArchiveMissingTheNestedArchiveTheListCopiesWholeWaitsForTheUser_WhenTheSourceCannotBeFetchedByTheApp()
    {
        await using var zip = await BuildNestedZip();
        await AddWithoutNestedBytes(zip);

        var plan = await Plan(zip, Prompted, Copy(zip, InnerZip));

        plan.Rows.Should().ContainSingle().Which.State.Should().Be(ArchiveState.Manual);
    }

    [Fact]
    public async Task AHeldArchiveMissingTheNestedArchiveIsABrowserRowForANexusFileWithoutPremium()
    {
        await using var zip = await BuildNestedZip();
        await AddWithoutNestedBytes(zip);

        var plan = await Plan(zip, new NexusState("SkyrimSpecialEdition", ModId: 77, FileId: 4242, null, null, null, false), Copy(zip, InnerZip));

        var row = plan.Rows.Should().ContainSingle().Subject;
        row.State.Should().Be(ArchiveState.Browser);
        row.Page.Should().NotBeNull();
    }

    [Fact]
    public async Task AHeldArchiveIsStillHeldWhenTheListOnlyReadsFilesInsideTheNestedArchive()
    {
        await using var zip = await BuildNestedZip();
        await AddWithoutNestedBytes(zip);

        var plan = await Plan(zip, Http, Copy(zip, InnerZip, InnerMember));

        plan.Rows.Should().ContainSingle().Which.State.Should().Be(ArchiveState.Have);
    }

    [Fact]
    public async Task AHeldArchiveIsStillHeldWhenNoDirectiveReadsTheNestedArchive()
    {
        await using var zip = await BuildNestedZip();
        await AddWithoutNestedBytes(zip);

        var plan = await Plan(zip, Http);

        plan.Rows.Should().ContainSingle().Which.State.Should().Be(ArchiveState.Have);
    }

    [Fact]
    public async Task AHeldArchiveWhoseNestedArchiveBytesAreKeptIsHeld()
    {
        await using var zip = await BuildNestedZip();
        await RegisterLocalArchive(zip.Outer.Path);
        (await FileStore.HaveFile(zip.InnerStoreIdentity)).Should().BeTrue("an import keeps the nested archive's bytes");

        var plan = await Plan(zip, Http, Copy(zip, InnerZip));

        plan.Rows.Should().ContainSingle().Which.State.Should().Be(ArchiveState.Have);
    }

    [Fact]
    public async Task AnIncompleteHeldArchiveDoesNotTurnItsWaitingRowIntoHeld()
    {
        await using var zip = await BuildNestedZip();
        await AddWithoutNestedBytes(zip);
        var plan = await Plan(zip, Prompted, Copy(zip, InnerZip));

        using var page = Acquirer.Attach();
        Acquirer.Show(plan);
        Acquirer.Recheck();

        Acquirer.Plan.Rows.Should().ContainSingle().Which.State.Should().Be(ArchiveState.Manual);
    }

    [Fact]
    public async Task ADroppedCopyOfAnIncompleteHeldArchiveIsAddedAgainAndCompletesTheRow()
    {
        await using var zip = await BuildNestedZip();
        await AddWithoutNestedBytes(zip);
        using var page = Acquirer.Attach();
        Acquirer.Show(await Plan(zip, Prompted, Copy(zip, InnerZip)));
        await InboxFolder.Combine("outer.zip").WriteAllBytesAsync(zip.OuterBytes);

        var unmatched = await Inbox.Scan(CancellationToken.None);

        unmatched.Should().BeEmpty();
        (await FileStore.HaveFile(zip.InnerStoreIdentity)).Should().BeTrue("the drop was added to the library, not just deleted");
        Acquirer.Plan.Rows.Should().ContainSingle().Which.State.Should().Be(ArchiveState.Have);
        InboxFolder.Combine("outer.zip").FileExists.Should().BeFalse("a matched file now lives in the library");
    }

    [Fact]
    public async Task ADroppedCopyOfACompleteHeldArchiveIsNotAddedAgain()
    {
        await using var zip = await BuildNestedZip();
        await RegisterLocalArchive(zip.Outer.Path);
        using var page = Acquirer.Attach();
        Acquirer.Show(await Plan(zip, Prompted, Copy(zip, InnerZip)));
        await InboxFolder.Combine("outer.zip").WriteAllBytesAsync(zip.OuterBytes);

        await Inbox.Scan(CancellationToken.None);

        Connection.Db.Datoms(LibraryFile.XxHash64, zip.OuterHash.XxHash64Value()).Count.Should().Be(1);
    }

    [Fact]
    public async Task ANestedArchiveTheListCopiesWholeResolvesOnceTheArchiveIsImportedAgain()
    {
        await using var zip = await BuildNestedZip();
        await AddWithoutNestedBytes(zip);
        var loadout = await CreateLoadout();
        var plan = await Plan(zip, Prompted, Copy(zip, InnerZip));
        var path = new ArchiveHashPath(zip.OuterHash, [InnerZip]);

        using (var before = Resolver(plan, loadout))
        {
            var skipped = async () => await before.Resolve(path, CancellationToken.None);
            await skipped.Should().ThrowAsync<MemberNotFoundException>().WithMessage("*is an archive the library expanded*");
        }

        using var page = Acquirer.Attach();
        Acquirer.Show(plan);
        await InboxFolder.Combine("outer.zip").WriteAllBytesAsync(zip.OuterBytes);
        await Inbox.Scan(CancellationToken.None);

        using var after = Resolver(plan, loadout);
        var member = await after.Resolve(path, CancellationToken.None);
        await using var stream = await member.Open();
        using var read = new MemoryStream();
        await stream.CopyToAsync(read);
        read.ToArray().Should().Equal(zip.InnerBytes);
    }

    private ArchivePathResolver Resolver(ArchivePlan plan, SilverModManager.Sdk.Loadouts.Loadout.ReadOnly loadout)
        => new(plan, Connection.Db, FileStore, Inventory.GameFilesFor(loadout), [], NullLogger.Instance);
}
