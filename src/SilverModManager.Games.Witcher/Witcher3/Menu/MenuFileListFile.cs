using System.Text;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.Witcher.Witcher3.Menu;

/// <summary>
/// <c>dx12filelist.txt</c> or <c>dx11filelist.txt</c> in the menu config folder: the XMLs the game's menu loads for
/// that renderer. Built from the XMLs the sync tree holds there, so a mod's menu file is loaded without the user
/// editing the list. The two lists differ only in the graphics menu, which has a file of its own for DX11.
/// </summary>
public sealed class MenuFileListFile(bool dx12) : IIntrinsicFile
{
    private const string Dx11Graphics = "graphicsdx11";
    private const string Graphics = "graphics";

    public GamePath Path { get; } = WitcherConstants.W3MenuConfigFolder / (dx12 ? "dx12filelist.txt" : "dx11filelist.txt");

    /// <inheritdoc />
    public async Task Write(Stream stream, Loadout.ReadOnly loadout, Dictionary<GamePath, SyncNode> syncTree)
    {
        var names = syncTree
            .Where(entry => entry.Value.HaveLoadout || entry.Value.HaveDisk)
            .Select(entry => MenuXmlPaths.NameOf(entry.Key))
            .OfType<string>()
            .Select(ForThisList)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase);

        var text = new StringBuilder();
        foreach (var name in names) text.Append(name).Append(".xml;\r\n");
        await stream.WriteAsync(new UTF8Encoding(false).GetBytes(text.ToString()));
    }

    // graphics.xml is the DX12 menu and graphicsdx11.xml the DX11 one; each list names only its own.
    private string? ForThisList(string name)
    {
        if (name.Equals(Dx11Graphics, StringComparison.OrdinalIgnoreCase)) return dx12 ? null : name;
        return name.Equals(Graphics, StringComparison.OrdinalIgnoreCase) && !dx12 ? Dx11Graphics : name;
    }

    /// <inheritdoc />
    /// <remarks>The list is derived from the other files, so what is on disk carries nothing to keep.</remarks>
    public Task Ingest(Stream stream, Loadout.ReadOnly loadout, Dictionary<GamePath, SyncNode> syncTree, ITransaction tx) => Task.CompletedTask;
}
