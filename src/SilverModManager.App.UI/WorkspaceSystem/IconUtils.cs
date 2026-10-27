using System.Diagnostics;
using Avalonia;
using Avalonia.Media.Imaging;
using SilverModManager.App.UI.Extensions;
using SkiaSharp;
using Svg.Skia;

namespace SilverModManager.App.UI.WorkspaceSystem;

internal static class IconUtils
{
    private const float IconSize = 30f;
    private const float CornerRadius = 2f;

    private const float OuterPadding = 1.25f;
    private const float InnerPadding = OuterPadding + 2.5f;

    private const float Scale = 5f;

    private static readonly SKColor FillColor = SKColor.Parse("#D4D4D8");

    /// <summary>
    /// Generates a <see cref="Bitmap"/> for the given state.
    /// </summary>
    internal static Bitmap StateToBitmap(WorkspaceGridState state)
    {
        using var skPicture = GeneratePicture(state);
        using var skBitmap = skPicture.ToBitmap(
            background: SKColors.Transparent,
            scaleX: Scale,
            scaleY: Scale,
            skColorType: SKColorType.Bgra8888,
            skAlphaType: SKAlphaType.Premul,
            skColorSpace: SKColorSpace.CreateSrgb()
        );

        Debug.Assert(skBitmap is not null);
        return skBitmap.ToAvaloniaImage();
    }

    private static SKPicture GeneratePicture(WorkspaceGridState state)
    {
        using var skPictureRecorder = new SKPictureRecorder();
        using var skCanvas = skPictureRecorder.BeginRecording(new SKRect(0f, 0f, IconSize, IconSize));

        // A hollow panel is an outline: its inner rectangle is a hole in the same even-odd path, so whatever is behind
        // the picture (Glass included) shows through it.
        using var skPath = new SKPath { FillType = SKPathFillType.EvenOdd };

        foreach (var panel in state)
        {
            var (panelId, rect) = panel;
            DrawRect(skPath, rect, isHollow: panelId != PanelId.DefaultValue);
        }

        using (var skPaint = new SKPaint())
        {
            using var skShader = SKShader.CreateColor(
                FillColor,
                SKColorSpace.CreateRgb(SKColorSpaceTransferFn.Srgb, SKColorSpaceXyz.Srgb)
            );

            skPaint.IsAntialias = true;
            skPaint.Shader = skShader;
            skCanvas.DrawPath(skPath, skPaint);
        }

        var skPicture = skPictureRecorder.EndRecording();
        return skPicture;
    }

    private static void DrawRect(SKPath skPath, Rect rect, bool isHollow)
    {
        var x = (float)(rect.X * IconSize);
        var y = (float)(rect.Y * IconSize);
        var width = (float)(rect.Width * IconSize);
        var height = (float)(rect.Height * IconSize);

        var skRect = new SKRect(
            left: x + OuterPadding,
            right: x + width - OuterPadding,
            top: y + OuterPadding,
            bottom: y + height - OuterPadding
        );

        skPath.AddRoundRect(skRect, rx: CornerRadius, ry: CornerRadius);
        if (!isHollow) return;

        skRect = new SKRect(
            left: x + InnerPadding,
            right: x + width - InnerPadding,
            top: y + InnerPadding,
            bottom: y + height - InnerPadding
        );

        skPath.AddRect(skRect);
    }
}
