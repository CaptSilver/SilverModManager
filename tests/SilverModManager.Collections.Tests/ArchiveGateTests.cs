using SilverModManager.Abstractions.Loadouts;
using FluentAssertions;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using Xunit;

namespace SilverModManager.Collections.Tests;

public class ArchiveGateTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<ArchiveGateTests>(helper)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private OfflineCollections Offline => new(ServiceProvider, Connection, LibraryService, TemporaryFileManager);

    /// <summary>The library item of <c>mod{index}.zip</c> with the given shape, ready to hand to an installer.</summary>
    private async Task<LibraryItem.ReadOnly> ModItem(int index, ModShape shape = ModShape.Plain)
    {
        var folder = TemporaryFileManager.CreateFolder();
        var (path, md5) = await Offline.CreateMod(folder.Path, index, shape);
        await Offline.AddDirectDownload(path, md5);
        return LibraryItem.All(Connection.Db).Single(i => i.Name == $"mod{index}.zip");
    }

    /// <summary>Runs <paramref name="installer"/> on the item into a transaction that is never committed.</summary>
    private async ValueTask<InstallerResult> Run(ILibraryItemInstaller installer, LibraryItem.ReadOnly item, CancellationToken token = default)
    {
        var loadout = await CreateLoadout();
        var parent = LoadoutItemGroupId.From(DefaultCollection.Of(Connection.Db, loadout.Id)?.Value ?? throw new InvalidOperationException("no collection"));
        using var tx = Connection.BeginTransaction();
        var group = new LoadoutItemGroup.New(tx, out var id)
        {
            IsGroup = true,
            LoadoutItem = new LoadoutItem.New(tx, id) { Name = item.Name, LoadoutId = loadout.Id, ParentId = parent },
        };
        return await installer.ExecuteAsync(item, group, tx, loadout, token);
    }

    [Fact]
    public void Install_SwapsTheSlot_AndDisposeRestoresIt()
    {
        var installers = Game.LibraryItemInstallers;
        var original = installers[1];

        using (var gate = ArchiveGate.Install(installers, 1, "mod1.zip"))
        {
            installers[1].Should().BeSameAs(gate);
            gate.Inner.Should().BeSameAs(original);
        }

        installers[1].Should().BeSameAs(original);
    }

    [Fact]
    public async Task AGatedArchive_WaitsForRelease_ThenDelegates()
    {
        var item = await ModItem(1);
        var inner = new ClaimingInstaller("mod1.zip");
        var installers = new ILibraryItemInstaller[] { inner };
        using var gate = ArchiveGate.Install(installers, 0, "mod1.zip");
        gate.IsSupportedLibraryItem(item).Should().BeTrue();

        var run = Run(gate, item).AsTask();
        await gate.Entered.WaitAsync(Timeout);
        run.IsCompleted.Should().BeFalse("the gate holds the archive until it is released");

        gate.Release();
        var result = await run.WaitAsync(Timeout);

        result.IsSuccess.Should().BeTrue("the released call goes on to the installer the gate replaced");
    }

    [Fact]
    public async Task Fail_MakesTheHeldCallThrowThatException()
    {
        var item = await ModItem(1);
        var installers = new ILibraryItemInstaller[] { new ClaimingInstaller("mod1.zip") };
        using var gate = ArchiveGate.Install(installers, 0, "mod1.zip");

        var run = Run(gate, item).AsTask();
        await gate.Entered.WaitAsync(Timeout);
        gate.Fail(new InvalidOperationException("broken on purpose"));

        var act = async () => await run.WaitAsync(Timeout);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("broken on purpose");
    }

    [Fact]
    public async Task ThrowsOnEntry_ThrowsWithoutHolding()
    {
        var item = await ModItem(1);
        var installers = new ILibraryItemInstaller[] { new ClaimingInstaller("mod1.zip") };
        using var gate = ArchiveGate.Install(installers, 0, "mod1.zip");
        gate.ThrowsOnEntry = new InvalidOperationException("at once");

        var act = async () => await Run(gate, item).AsTask().WaitAsync(Timeout);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("at once");
    }

    [Fact]
    public async Task CancellingTheToken_ThrowsOperationCanceled()
    {
        var item = await ModItem(1);
        var installers = new ILibraryItemInstaller[] { new ClaimingInstaller("mod1.zip") };
        using var gate = ArchiveGate.Install(installers, 0, "mod1.zip");
        using var cancel = new CancellationTokenSource();

        var run = Run(gate, item, cancel.Token).AsTask();
        await gate.Entered.WaitAsync(Timeout);
        await cancel.CancelAsync();

        var act = async () => await run.WaitAsync(Timeout);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task AnotherArchive_GoesStraightToTheInnerInstaller()
    {
        var item = await ModItem(2);
        var installers = new ILibraryItemInstaller[] { new ClaimingInstaller("mod2.zip") };
        using var gate = ArchiveGate.Install(installers, 0, "mod1.zip");

        var result = await Run(gate, item).AsTask().WaitAsync(Timeout);

        result.IsSuccess.Should().BeTrue();
        gate.Entered.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task ClaimingInstaller_InstallsOnlyTheNamedArchives()
    {
        var claimed = await ModItem(0);
        var other = await ModItem(1);
        var installer = new ClaimingInstaller("mod0.zip");

        installer.IsSupportedLibraryItem(claimed).Should().BeTrue();
        installer.IsSupportedLibraryItem(other).Should().BeFalse();
        (await Run(installer, claimed)).IsSuccess.Should().BeTrue();
        (await Run(installer, other)).IsNotSupported(out _).Should().BeTrue();
    }
}
