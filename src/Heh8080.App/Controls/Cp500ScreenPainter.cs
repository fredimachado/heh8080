using System;
using System.Runtime.InteropServices;
using Heh8080.Devices;
using SkiaSharp;

namespace Heh8080.UI.Controls;

/// <summary>
/// Paints a <see cref="Cp500Raster"/> frame onto a canvas with the green phosphor look of the CP/M terminal.
/// The frame is scaled in two steps so pixels stay even: a nearest-neighbour upscale by whole factors, then a smooth
/// resize to the target. At a whole-number scale the second step changes nothing.
/// </summary>
internal static class Cp500ScreenPainter
{
    private static readonly SKColor Phosphor = new(0x33, 0xFF, 0x33);
    private static readonly SKColor Glow = new(0x33, 0xFF, 0x33, 0x60);

    /// <summary>Draws the frame into <paramref name="content"/>, whose edges should lie on whole canvas pixels.</summary>
    public static void Paint(SKCanvas canvas, byte[] frame, SKRect content, float glowOffset)
    {
        using SKImage pixels = CreateImage(frame);
        using SKImage upscaled = UpscaleByWholeFactors(pixels, content);
        using var paint = new SKPaint { FilterQuality = SKFilterQuality.Medium };
        using var glowPaint = new SKPaint
        {
            FilterQuality = SKFilterQuality.Medium,
            ColorFilter = SKColorFilter.CreateBlendMode(Glow, SKBlendMode.SrcIn)
        };

        // Same glow as the CP/M renderer: dim copies at the four diagonal offsets, then the picture itself.
        foreach (float dx in new[] { -glowOffset, glowOffset })
        {
            foreach (float dy in new[] { -glowOffset, glowOffset })
            {
                var shifted = SKRect.Create(content.Left + dx, content.Top + dy, content.Width, content.Height);
                canvas.DrawImage(upscaled, shifted, glowPaint);
            }
        }

        canvas.DrawImage(upscaled, content, paint);
    }

    /// <summary>Lit pixels become opaque phosphor and dark pixels stay transparent so the glow follows the lit ones.</summary>
    private static SKImage CreateImage(byte[] frame)
    {
        var info = new SKImageInfo(Cp500Raster.Width, Cp500Raster.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        uint[] pixels = new uint[frame.Length];
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = frame[i] != 0 ? (uint)Phosphor : 0;

        return SKImage.FromPixelCopy(info, MemoryMarshal.AsBytes(pixels.AsSpan()));
    }

    private static SKImage UpscaleByWholeFactors(SKImage source, SKRect target)
    {
        int factorX = Math.Max(1, (int)Math.Ceiling(target.Width / source.Width));
        int factorY = Math.Max(1, (int)Math.Ceiling(target.Height / source.Height));
        int width = source.Width * factorX;
        int height = source.Height * factorY;
        using SKSurface surface = SKSurface.Create(new SKImageInfo(width, height));
        using var paint = new SKPaint { FilterQuality = SKFilterQuality.None };
        surface.Canvas.DrawImage(source, SKRect.Create(width, height), paint);
        return surface.Snapshot();
    }
}
