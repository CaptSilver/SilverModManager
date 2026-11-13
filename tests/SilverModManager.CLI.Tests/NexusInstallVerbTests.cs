using System.IO.Compression;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Extensions;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.DataModel.Undo;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.CLI.Tests;

/// <summary>
/// <c>nexus install</c> against a Nexus side the test answers for: the file lists, the account and the download are
/// stand-ins, while the install itself runs through the real install core into a real loadout.
/// </summary>
[Collection("ConsoleOutput")]
public class NexusInstallVerbTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<NexusInstallVerbTests>(helper)
{
    private static readonly NexusModsGameId Game = NexusModsGameId.From(3333);
    private static readonly ModId Mod = ModId.From(9);
    private static readonly Uri FilePage = new("https://www.nexusmods.com/cyberpunk2077/mods/9?tab=files&file_id=1&nmm=1");

    private readonly INexusModsInstaller _nexus = Substitute.For<INexusModsInstaller>();
    private readonly ILoginManager _login = Substitute.For<ILoginManager>();
    private int _downloads;

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        _nexus.GameOf(Arg.Any<LoadoutId>(), out Arg.Any<string?>()).Returns(call =>
        {
            call[1] = null;
            return Game;
        });
        _nexus.DownloadPage(Game, Arg.Any<ModId>(), Arg.Any<FileId>()).Returns(FilePage);
        return base.AddServices(services).AddVerbs().AddSingleton(_nexus).AddSingleton(_login);
    }

    private static NexusModsFileRow FileRow(uint id, string name, NexusModsFileCategory category) =>
        new(FileId.From(id), name, "1.0", category, 2048, DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));

    private void FilesOf(params NexusModsFileRow[] files) =>
        _nexus.Files(Game, Mod, Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<NexusModsFileRow>>(files));

    private void LoginAs(UserRole? role) => _login.GetUserInfoAsync(Arg.Any<CancellationToken>()).Returns(new ValueTask<UserInfo?>(
        role is { } known ? new UserInfo { UserId = UserId.From(1), Name = "silver", UserRole = known, AvatarUrl = null } : null));

    // A real zip in the library, so the install core has something to install.
    private async Task<LibraryItem.ReadOnly> LibraryZip(string name)
    {
        var path = TemporaryFileManager.CreateFolder().Path / $"{name}.zip";
        await using (var stream = path.Create())
        {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
            await using var entry = zip.CreateEntry($"archive/pc/mod/{name}.archive").Open();
            await entry.WriteAsync(System.Text.Encoding.UTF8.GetBytes(name));
        }

        return (await RegisterLocalArchive(path)).AsLibraryFile().AsLibraryItem();
    }

    // The file is in the library: nothing to download, whatever the account.
    private async Task ResolveToLibrary(uint file, string name)
    {
        // The archive is made first: NSubstitute reads Returns from the last call on this thread, which an await would lose.
        var item = await LibraryZip(name);
        _nexus.Resolve(Game, Mod, FileId.From(file), name, false, Arg.Any<LoadoutId>())
            .Returns(new ResolvedInstall(name, false, item, _ => throw new InvalidOperationException("must not download")));
    }

    // The file is not in the library: the premium download API would fetch it.
    private async Task ResolveToDownload(uint file, string name)
    {
        var item = await LibraryZip(name);
        _nexus.Resolve(Game, Mod, FileId.From(file), name, false, Arg.Any<LoadoutId>())
            .Returns(new ResolvedInstall(name, false, null, _ =>
            {
                _downloads++;
                return Task.FromResult(item);
            }));
    }

    private int Links(LoadoutId loadout) =>
        LibraryLinkedLoadoutItem.All(Connection.Db).Count(link => link.AsLoadoutItem().LoadoutId == loadout);

    private Task<(int ExitCode, string Output)> Run(string loadout, params string[] args) =>
        NexusVerbHost.Run(ServiceProvider, ["nexus", "install", "--loadout", loadout, .. args]);

    [Fact]
    public async Task Install_ByMod_PicksTheMainFile()
    {
        var loadout = await CreateLoadout();
        FilesOf(FileRow(1, "Core", NexusModsFileCategory.Main), FileRow(2, "Extras", NexusModsFileCategory.Optional));
        await ResolveToDownload(1, "Core");
        LoginAs(UserRole.Premium);

        var (code, output) = await Run(loadout.LoadoutId.ToString(), "--modId", "9");

        code.Should().Be(0, output);
        output.Should().Contain("[downloaded] Core").And.Contain("[installed] Core");
        _downloads.Should().Be(1);
        Links(loadout.LoadoutId).Should().Be(1);
    }

    [Fact]
    public async Task Install_ByFile_LooksTheModUp()
    {
        var loadout = await CreateLoadout();
        _nexus.ModOf(Game, FileId.From(2), Arg.Any<CancellationToken>()).Returns(Task.FromResult(Mod));
        FilesOf(FileRow(1, "Core", NexusModsFileCategory.Main), FileRow(2, "Extras", NexusModsFileCategory.Optional));
        await ResolveToDownload(2, "Extras");
        LoginAs(UserRole.Premium);

        var (code, output) = await Run(loadout.LoadoutId.ToString(), "--fileId", "2");

        code.Should().Be(0, output);
        output.Should().Contain("[installed] Extras");
    }

    [Fact]
    public async Task Install_ByModAndFile_TheFileMustBelongToTheMod()
    {
        var loadout = await CreateLoadout();
        FilesOf(FileRow(1, "Core", NexusModsFileCategory.Main));
        LoginAs(UserRole.Premium);

        var (code, output) = await Run(loadout.LoadoutId.ToString(), "--modId", "9", "--fileId", "77");

        code.Should().Be(1, output);
        output.Should().Contain("File 77 is not a file of mod 9");
        Links(loadout.LoadoutId).Should().Be(0);
    }

    [Fact]
    public async Task Install_NeitherModNorFile_AsksForOne()
    {
        var loadout = await CreateLoadout();

        var (code, output) = await Run(loadout.LoadoutId.ToString());

        code.Should().Be(1, output);
        output.Should().Contain("--modId").And.Contain("--fileId");
    }

    [Fact]
    public async Task Install_SeveralMainFiles_RefusesWithTheCount()
    {
        var loadout = await CreateLoadout();
        FilesOf(FileRow(1, "Core A", NexusModsFileCategory.Main), FileRow(2, "Core B", NexusModsFileCategory.Main));
        LoginAs(UserRole.Premium);

        var (code, output) = await Run(loadout.LoadoutId.ToString(), "--modId", "9");

        code.Should().Be(1, output);
        output.Should().Contain("has 2 main files").And.Contain("nexus files");
        Links(loadout.LoadoutId).Should().Be(0);
    }

    [Fact]
    public async Task Install_FreeAccount_PrintsTheUrlAndExitsTwo()
    {
        var loadout = await CreateLoadout();
        FilesOf(FileRow(1, "Core", NexusModsFileCategory.Main));
        await ResolveToDownload(1, "Core");
        LoginAs(UserRole.Free);

        var (code, output) = await Run(loadout.LoadoutId.ToString(), "--modId", "9");

        code.Should().Be(2, output);
        output.Should().Contain("nmm=1").And.Contain("Download with manager");
        _downloads.Should().Be(0);
        Links(loadout.LoadoutId).Should().Be(0);
    }

    [Fact]
    public async Task Install_FreeAccount_FileInLibrary_InstallsWithoutAUrl()
    {
        var loadout = await CreateLoadout();
        FilesOf(FileRow(1, "Core", NexusModsFileCategory.Main));
        await ResolveToLibrary(1, "Core");
        LoginAs(UserRole.Free);

        var (code, output) = await Run(loadout.LoadoutId.ToString(), "--modId", "9");

        code.Should().Be(0, output);
        output.Should().Contain("[installed] Core").And.NotContain("nmm=1");
        await _login.DidNotReceiveWithAnyArgs().GetUserInfoAsync();
    }

    [Fact]
    public async Task Install_SignedOut_FileInLibrary_Installs()
    {
        var loadout = await CreateLoadout();
        FilesOf(FileRow(1, "Core", NexusModsFileCategory.Main));
        await ResolveToLibrary(1, "Core");
        LoginAs(null);

        var (code, output) = await Run(loadout.LoadoutId.ToString(), "--modId", "9");

        code.Should().Be(0, output);
        output.Should().Contain("[installed] Core");
    }

    [Fact]
    public async Task Install_NotSignedIn_Refuses()
    {
        var loadout = await CreateLoadout();
        FilesOf(FileRow(1, "Core", NexusModsFileCategory.Main));
        await ResolveToDownload(1, "Core");
        LoginAs(null);

        var (code, output) = await Run(loadout.LoadoutId.ToString(), "--modId", "9");

        code.Should().Be(1, output);
        output.Should().Contain("nexus-login");
        Links(loadout.LoadoutId).Should().Be(0);
    }

    [Fact]
    public async Task Install_LoginCheckThrows_SaysNexusDidNotAnswer()
    {
        var loadout = await CreateLoadout();
        FilesOf(FileRow(1, "Core", NexusModsFileCategory.Main));
        await ResolveToDownload(1, "Core");
        _login.GetUserInfoAsync(Arg.Any<CancellationToken>()).Throws(new HttpRequestException("Connection refused"));

        var (code, output) = await Run(loadout.LoadoutId.ToString(), "--modId", "9");

        code.Should().Be(1, output);
        output.Should().Contain("Nexus Mods did not answer").And.Contain("Connection refused").And.NotContain("Not signed in");
    }

    [Fact]
    public async Task Install_FileListThrows_SaysNexusDidNotAnswer()
    {
        var loadout = await CreateLoadout();
        _nexus.Files(Game, Mod, Arg.Any<CancellationToken>()).Throws(new HttpRequestException("Connection refused"));
        LoginAs(UserRole.Premium);

        var (code, output) = await Run(loadout.LoadoutId.ToString(), "--modId", "9");

        code.Should().Be(1, output);
        output.Should().Contain("Nexus Mods did not answer").And.Contain("Connection refused").And.NotContain("main files");
    }

    [Fact]
    public async Task Install_UnknownFile_SaysWhatNexusSaid()
    {
        var loadout = await CreateLoadout();
        _nexus.ModOf(Game, FileId.From(404), Arg.Any<CancellationToken>()).Throws(new InvalidOperationException("Nexus Mods has no file 404 for this game."));
        LoginAs(UserRole.Premium);

        var (code, output) = await Run(loadout.LoadoutId.ToString(), "--fileId", "404");

        code.Should().Be(1, output);
        output.Should().Contain("Nexus Mods has no file 404 for this game.");
    }

    [Fact]
    public async Task Install_RemovedFile_IsRefused()
    {
        var loadout = await CreateLoadout();
        FilesOf(FileRow(1, "Gone", NexusModsFileCategory.Removed));
        LoginAs(UserRole.Premium);

        var (code, output) = await Run(loadout.LoadoutId.ToString(), "--modId", "9", "--fileId", "1");

        code.Should().Be(1, output);
        output.Should().Contain("Nexus Mods has removed this file");
    }

    [Theory]
    [InlineData(NexusModsFileCategory.Archived, "[note] Old is archived")]
    [InlineData(NexusModsFileCategory.OldVersion, "[note] Old is an old version")]
    public async Task Install_ArchivedFile_InstallsWithANote(NexusModsFileCategory category, string note)
    {
        var loadout = await CreateLoadout();
        FilesOf(FileRow(1, "Old", category));
        await ResolveToDownload(1, "Old");
        LoginAs(UserRole.Premium);

        var (code, output) = await Run(loadout.LoadoutId.ToString(), "--modId", "9", "--fileId", "1");

        code.Should().Be(0, output);
        output.Should().StartWith(note).And.Contain("[installed] Old");
    }

    [Fact]
    public async Task Install_ThenRevert_RemovesTheGroup()
    {
        var loadout = await CreateLoadout();
        FilesOf(FileRow(1, "Core", NexusModsFileCategory.Main));
        await ResolveToLibrary(1, "Core");
        var linksBefore = Links(loadout.LoadoutId);

        var (code, output) = await Run(loadout.LoadoutId.ToString(), "--modId", "9");
        code.Should().Be(0, output);
        Links(loadout.LoadoutId).Should().Be(linksBefore + 1);

        var restorePoint = ServiceProvider.GetRequiredService<UndoService>().RevisionsFor(loadout.Id).Count - 1;
        var (revertCode, revertOutput) = await NexusVerbHost.Run(ServiceProvider, "loadout", "revert", "-l", loadout.LoadoutId.ToString(), "-r", restorePoint.ToString());

        revertCode.Should().Be(0, revertOutput);
        Links(loadout.LoadoutId).Should().Be(linksBefore);
    }

    [Fact]
    public async Task Install_Json_Success_IsTheOutcomesWithTheirIds()
    {
        var loadout = await CreateLoadout();
        FilesOf(FileRow(1, "Core", NexusModsFileCategory.Main));
        await ResolveToLibrary(1, "Core");

        var (code, output) = await Run(loadout.LoadoutId.ToString(), "--modId", "9", "--json");

        code.Should().Be(0, output);
        var row = JsonDocument.Parse(output).RootElement[0];
        row.GetProperty("name").GetString().Should().Be("Core");
        row.GetProperty("outcome").GetString().Should().Be("installed");
        row.GetProperty("modId").GetUInt32().Should().Be(9);
        row.GetProperty("fileId").GetUInt32().Should().Be(1);
    }

    [Fact]
    public async Task Install_Json_FreeAccount_SaysNeedsBrowser()
    {
        var loadout = await CreateLoadout();
        FilesOf(FileRow(1, "Core", NexusModsFileCategory.Main));
        await ResolveToDownload(1, "Core");
        LoginAs(UserRole.Free);

        var (code, output) = await Run(loadout.LoadoutId.ToString(), "--modId", "9", "--json");

        code.Should().Be(2, output);
        var root = JsonDocument.Parse(output).RootElement;
        root.GetProperty("needsBrowser").GetBoolean().Should().BeTrue();
        root.GetProperty("url").GetString().Should().Be(FilePage.ToString());
    }
}
