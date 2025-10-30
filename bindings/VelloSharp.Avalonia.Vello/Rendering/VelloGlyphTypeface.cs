using System;
using System.IO;
using Avalonia.Media;
using VelloSharp;
using AvaloniaGlyphMetrics = Avalonia.Media.GlyphMetrics;

namespace VelloSharp.Avalonia.Vello.Rendering;

internal sealed class VelloGlyphTypeface : IGlyphTypeface
{
    internal const float FauxItalicSkew = -0.3f;
    internal const double FauxBoldStrokeScale = 1.0 / 24.0;

    private readonly byte[] _fontData;
    private readonly Font _font;
    private readonly uint _fontIndex;
    private readonly FontMetrics _metrics;
    private readonly int _glyphCount;
    private readonly float _designFontSize;
    private readonly FontSimulations _simulations;
    private bool _disposed;

    public VelloGlyphTypeface(
        string familyName,
        FontStyle style,
        FontWeight weight,
        FontStretch stretch,
        byte[] fontData,
        uint fontIndex = 0,
        FontSimulations simulations = FontSimulations.None)
    {
        FamilyName = familyName ?? throw new ArgumentNullException(nameof(familyName));
        Style = style;
        Weight = weight;
        Stretch = stretch;
        ArgumentNullException.ThrowIfNull(fontData);

        _fontData = (byte[])fontData.Clone();
        _fontIndex = fontIndex;
        _font = Font.Load(_fontData, fontIndex);
        _simulations = simulations;

        (_metrics, _glyphCount, _designFontSize) = LoadMetrics();
    }

    public Font Font => _font;

    public string FamilyName { get; }

    public FontWeight Weight { get; }

    public FontStyle Style { get; }

    public FontStretch Stretch { get; }

    public int GlyphCount => _glyphCount;

    public FontSimulations FontSimulations => _simulations;

    public FontMetrics Metrics => _metrics;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _font.Dispose();
        _disposed = true;
    }

    public bool TryGetStream(out Stream stream)
    {
        if (_disposed)
        {
            stream = Stream.Null;
            return false;
        }

        stream = new MemoryStream(_fontData, writable: false);
        return true;
    }

    public ushort GetGlyph(uint codepoint)
    {
        ThrowIfDisposed();

        return NativeMethods.vello_font_get_glyph_index(_font.Handle, codepoint, out var glyph) == VelloStatus.Success
            ? glyph
            : (ushort)0;
    }

    public bool TryGetGlyph(uint codepoint, out ushort glyph)
    {
        ThrowIfDisposed();

        var status = NativeMethods.vello_font_get_glyph_index(_font.Handle, codepoint, out glyph);
        if (status != VelloStatus.Success)
        {
            glyph = 0;
            return false;
        }

        return glyph != 0;
    }

    public ushort[] GetGlyphs(ReadOnlySpan<uint> codepoints)
    {
        ThrowIfDisposed();

        var glyphs = new ushort[codepoints.Length];
        for (var i = 0; i < codepoints.Length; i++)
        {
            glyphs[i] = GetGlyph(codepoints[i]);
        }

        return glyphs;
    }

    public int GetGlyphAdvance(ushort glyph)
    {
        ThrowIfDisposed();

        return TryGetGlyphMetricsCore(glyph, out var metrics)
            ? (int)MathF.Round(metrics.Advance)
            : 0;
    }

    public int[] GetGlyphAdvances(ReadOnlySpan<ushort> glyphs)
    {
        ThrowIfDisposed();

        var advances = new int[glyphs.Length];
        for (var i = 0; i < glyphs.Length; i++)
        {
            advances[i] = GetGlyphAdvance(glyphs[i]);
        }

        return advances;
    }

    public bool TryGetGlyphMetrics(ushort glyph, out AvaloniaGlyphMetrics metrics)
    {
        ThrowIfDisposed();

        if (!TryGetGlyphMetricsCore(glyph, out var native))
        {
            metrics = default;
            return false;
        }

        metrics = new AvaloniaGlyphMetrics
        {
            XBearing = (int)MathF.Round(native.XBearing),
            YBearing = (int)MathF.Round(native.YBearing),
            Width = (int)MathF.Round(native.Width),
            Height = (int)MathF.Round(native.Height),
        };

        return true;
    }

    public bool TryGetTable(uint tag, out byte[] table)
    {
        // Font table access not yet exposed through vello_ffi.
        table = Array.Empty<byte>();
        return false;
    }

    private (FontMetrics Metrics, int GlyphCount, float DesignFontSize) LoadMetrics()
    {
        var status = NativeMethods.vello_font_get_metrics(_font.Handle, 1.0f, out var native);
        if (status != VelloStatus.Success)
        {
            throw new InvalidOperationException(NativeHelpers.GetLastErrorMessage() ?? "Failed to read font metrics.");
        }

        var unitsPerEm = native.UnitsPerEm != 0 ? native.UnitsPerEm : (ushort)2048;
        status = NativeMethods.vello_font_get_metrics(_font.Handle, unitsPerEm, out native);
        if (status != VelloStatus.Success)
        {
            throw new InvalidOperationException(NativeHelpers.GetLastErrorMessage() ?? "Failed to read font metrics.");
        }

        static int Round(float value) => (int)MathF.Round(value);

        var metrics = new FontMetrics
        {
            DesignEmHeight = unchecked((short)unitsPerEm),
            IsFixedPitch = native.IsMonospace,
            Ascent = -Round(native.Ascent),
            Descent = -Round(native.Descent),
            LineGap = Round(native.Leading),
            UnderlinePosition = -Round(native.UnderlinePosition),
            UnderlineThickness = Round(native.UnderlineThickness),
            StrikethroughPosition = -Round(native.StrikeoutPosition),
            StrikethroughThickness = Round(native.StrikeoutThickness),
        };

        var designFontSize = Math.Max(1.0f, metrics.DesignEmHeight == 0 ? unitsPerEm : Math.Abs(metrics.DesignEmHeight));

        return (metrics, native.GlyphCount, designFontSize);
    }

    private bool TryGetGlyphMetricsCore(ushort glyph, out VelloGlyphMetricsNative metrics)
    {
        var status = NativeMethods.vello_font_get_glyph_metrics(_font.Handle, glyph, _designFontSize, out metrics);
        return status == VelloStatus.Success;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(VelloGlyphTypeface));
        }
    }
}
