using System;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using Avalonia;
using Avalonia.Media;
using VelloSharp;

namespace VelloSharp.Avalonia.Vello.Rendering;

internal static class VelloFontManager
{
    private static readonly ConcurrentDictionary<IGlyphTypeface, Font> s_fonts = new();

    internal readonly record struct FontGlyphMetrics(
        double Advance,
        double XBearing,
        double YBearing,
        double Width,
        double Height);

    internal readonly record struct FontGlyphOutline(VelloPathElement[] Commands, Rect Bounds);

    public static Font GetFont(IGlyphTypeface glyphTypeface)
    {
        if (glyphTypeface is null)
        {
            throw new ArgumentNullException(nameof(glyphTypeface));
        }

        return s_fonts.GetOrAdd(glyphTypeface, static typeface =>
        {
            if (typeface is VelloGlyphTypeface glyph)
            {
                return glyph.Font;
            }
            else
            {
                throw new NotSupportedException($"Glyph typeface '{typeface.FamilyName}' does not expose a font stream compatible with Vello.");
            }
        });
    }

    public static bool TryGetGlyphMetrics(Font font, ushort glyphId, double fontSize, out FontGlyphMetrics metrics)
    {
        if (font is null)
        {
            throw new ArgumentNullException(nameof(font));
        }

        var status = NativeMethods.vello_font_get_glyph_metrics(font.Handle, glyphId, (float)fontSize, out var native);
        if (status != VelloStatus.Success)
        {
            metrics = default;
            return false;
        }

        metrics = new FontGlyphMetrics(
            native.Advance,
            native.XBearing,
            native.YBearing,
            native.Width,
            native.Height);
        return true;
    }

    public static bool TryGetGlyphOutline(Font font, ushort glyphId, double fontSize, out FontGlyphOutline outline)
    {
        if (font is null)
        {
            throw new ArgumentNullException(nameof(font));
        }

        var status = NativeMethods.vello_font_get_glyph_outline(font.Handle, glyphId, (float)fontSize, tolerance: 0.25f, out var outlineHandle);
        if (status != VelloStatus.Success || outlineHandle == IntPtr.Zero)
        {
            outline = default;
            return false;
        }

        try
        {
            status = NativeMethods.vello_glyph_outline_get_data(outlineHandle, out var data);
            if (status != VelloStatus.Success)
            {
                outline = default;
                return false;
            }

            var commandCount = data.CommandCount == 0 ? 0 : checked((int)data.CommandCount);
            var commands = commandCount == 0 ? Array.Empty<VelloPathElement>() : new VelloPathElement[commandCount];

            if (commandCount > 0)
            {
                unsafe
                {
                    var source = new ReadOnlySpan<VelloPathElement>((void*)data.Commands, commandCount);
                    source.CopyTo(commands);
                }
            }

            outline = new FontGlyphOutline(
                commands,
                new Rect(data.Bounds.X, data.Bounds.Y, data.Bounds.Width, data.Bounds.Height));
            return true;
        }
        finally
        {
            NativeMethods.vello_glyph_outline_destroy(outlineHandle);
        }
    }
}
