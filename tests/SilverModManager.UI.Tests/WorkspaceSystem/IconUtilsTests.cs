using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FluentAssertions;
using Avalonia;
using SilverModManager.App.UI.WorkspaceSystem;

namespace SilverModManager.UI.Tests.WorkspaceSystem;

public class IconUtilsTests(IServiceProvider provider) : AUiTest(provider)
{
    [Fact]
    public Task Test_StateToBitmap_TwoColumns()
    {
        var state = WorkspaceGridState.From(
            isHorizontal: true,
            new PanelGridState(PanelId.NewId(),new Rect(0, 0, 0.5, 1)),
            new PanelGridState(PanelId.DefaultValue, new Rect(0.5, 0, 0.5, 1))
        );

        return RunVerify(state);
    }

    [Fact]
    public Task Test_StateToBitmap_TwoRows()
    {
        var state = WorkspaceGridState.From(
            isHorizontal: true,
            new PanelGridState(PanelId.NewId(), new Rect(0, 0, 1, 0.5)),
            new PanelGridState(PanelId.DefaultValue,  new Rect(0, 0.5, 1, 0.5))
        );

        return RunVerify(state);
    }

    [Fact]
    public Task Test_StateToBitmap_ThreePanels_OneLargeColumn()
    {
        var state = WorkspaceGridState.From(
            isHorizontal: true,
            new PanelGridState(PanelId.NewId(),new Rect(0, 0, 0.5, 0.5)),
            new PanelGridState(PanelId.DefaultValue, new Rect(0.5, 0, 0.5, 1)),
            new PanelGridState(PanelId.NewId(), new Rect(0, 0.5, 0.5, 0.5))
        );

        return RunVerify(state);
    }

    [Fact]
    public Task Test_StateToBitmap_ThreePanels_OneLargeRow()
    {
        var state = WorkspaceGridState.From(
            isHorizontal: true,
            new PanelGridState(PanelId.NewId(), new Rect(0, 0, 0.5, 0.5)),
            new PanelGridState(PanelId.NewId(), new Rect(0.5, 0, 0.5, 0.5)),
            new PanelGridState(PanelId.DefaultValue,new Rect(0, 0.5, 1, 0.5))
        );

        return RunVerify(state);
    }

    [Fact]
    public Task Test_StateToBitmap_FourPanels()
    {
        var state = WorkspaceGridState.From(
            isHorizontal: true,
            new PanelGridState(PanelId.NewId(), new Rect(0, 0, 0.5, 0.5)),
            new PanelGridState(PanelId.NewId(), new Rect(0, 0.5, 0.5, 0.5)),
            new PanelGridState(PanelId.NewId(), new Rect(0.5, 0, 0.5, 0.5)),
            new PanelGridState(PanelId.DefaultValue, new Rect(0.5, 0.5, 0.5, 0.5))
        );

        return RunVerify(state);
    }

    [Fact]
    public void Test_StateToBitmap_OnlyThePanelOutlinesArePainted()
    {
        // The picture sits on the top bar, which Glass tints; a painted background or hollow fill shows as a dark block.
        var state = WorkspaceGridState.From(
            isHorizontal: true,
            new PanelGridState(PanelId.NewId(), new Rect(0, 0, 0.5, 1)),
            new PanelGridState(PanelId.DefaultValue, new Rect(0.5, 0, 0.5, 1))
        );

        using var bitmap = IconUtils.StateToBitmap(state);
        byte AlphaAt(int x, int y)
        {
            var buffer = new byte[4];
            var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try { bitmap.CopyPixels(new PixelRect(x, y, 1, 1), handle.AddrOfPinnedObject(), 4, 4); }
            finally { handle.Free(); }
            return buffer[3];
        }

        var size = bitmap.PixelSize.Width;
        AlphaAt(0, 0).Should().Be(0, "outside the panels is see-through");
        AlphaAt(size / 4, size / 2).Should().Be(0, "inside a hollow panel is see-through");
        AlphaAt(size * 2 / 25, size / 2).Should().Be(255, "a hollow panel's outline is painted");
        AlphaAt(size * 3 / 4, size / 2).Should().Be(255, "the filled panel is painted");
    }

    private static Task RunVerify(WorkspaceGridState state, [CallerFilePath] string sourceFile = "")
    {
        using var stream = new MemoryStream();
        using (var bitmap = IconUtils.StateToBitmap(state))
        {
            bitmap.Save(stream);
            stream.Position = 0;
        }

        // ReSharper disable once ExplicitCallerInfoArgument
        return Verify(stream, extension: "png", sourceFile: sourceFile).DisableDiff();
    }
}
