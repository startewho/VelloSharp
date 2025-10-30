using System;
using Avalonia;
using VelloSharp;

namespace VelloSharp.Avalonia.Vello.Geometry;

internal static class VelloPathBuilderExtensions
{
    private const double ArcApproximationConstant = 0.5522847498307936;

    public static PathBuilder AddRectangle(this PathBuilder builder, Rect rect)
    {
        builder.MoveTo(rect.X, rect.Y);
        builder.LineTo(rect.Right, rect.Y);
        builder.LineTo(rect.Right, rect.Bottom);
        builder.LineTo(rect.X, rect.Bottom);
        return builder.Close();
    }

    public static PathBuilder AddRoundedRectangle(this PathBuilder builder, RoundedRect roundedRect)
    {
        if (!roundedRect.IsRounded)
        {
            return builder.AddRectangle(roundedRect.Rect);
        }

        var rect = roundedRect.Rect;
        var topLeft = NormalizeCorner(roundedRect.RadiiTopLeft, rect.Width, rect.Height);
        var topRight = NormalizeCorner(roundedRect.RadiiTopRight, rect.Width, rect.Height);
        var bottomRight = NormalizeCorner(roundedRect.RadiiBottomRight, rect.Width, rect.Height);
        var bottomLeft = NormalizeCorner(roundedRect.RadiiBottomLeft, rect.Width, rect.Height);

        var kappaTL = ArcApproximationConstant;
        var kappaTR = ArcApproximationConstant;
        var kappaBR = ArcApproximationConstant;
        var kappaBL = ArcApproximationConstant;

        builder.MoveTo(rect.X + topLeft.X, rect.Y);
        builder.LineTo(rect.Right - topRight.X, rect.Y);
        builder.CubicTo(
            rect.Right - topRight.X + topRight.X * kappaTR,
            rect.Y,
            rect.Right,
            rect.Y + topRight.Y - topRight.Y * kappaTR,
            rect.Right,
            rect.Y + topRight.Y);
        builder.LineTo(rect.Right, rect.Bottom - bottomRight.Y);
        builder.CubicTo(
            rect.Right,
            rect.Bottom - bottomRight.Y + bottomRight.Y * kappaBR,
            rect.Right - bottomRight.X + bottomRight.X * kappaBR,
            rect.Bottom,
            rect.Right - bottomRight.X,
            rect.Bottom);
        builder.LineTo(rect.X + bottomLeft.X, rect.Bottom);
        builder.CubicTo(
            rect.X + bottomLeft.X - bottomLeft.X * kappaBL,
            rect.Bottom,
            rect.X,
            rect.Bottom - bottomLeft.Y + bottomLeft.Y * kappaBL,
            rect.X,
            rect.Bottom - bottomLeft.Y);
        builder.LineTo(rect.X, rect.Y + topLeft.Y);
        builder.CubicTo(
            rect.X,
            rect.Y + topLeft.Y - topLeft.Y * kappaTL,
            rect.X + topLeft.X - topLeft.X * kappaTL,
            rect.Y,
            rect.X + topLeft.X,
            rect.Y);

        return builder.Close();
    }

    public static PathBuilder AddEllipse(this PathBuilder builder, Rect rect)
    {
        var rx = rect.Width / 2;
        var ry = rect.Height / 2;
        var cx = rect.X + rx;
        var cy = rect.Y + ry;
        var kappaX = rx * ArcApproximationConstant;
        var kappaY = ry * ArcApproximationConstant;

        builder.MoveTo(cx, rect.Y);
        builder.CubicTo(cx + kappaX, rect.Y, rect.Right, cy - kappaY, rect.Right, cy);
        builder.CubicTo(rect.Right, cy + kappaY, cx + kappaX, rect.Bottom, cx, rect.Bottom);
        builder.CubicTo(cx - kappaX, rect.Bottom, rect.X, cy + kappaY, rect.X, cy);
        builder.CubicTo(rect.X, cy - kappaY, cx - kappaX, rect.Y, cx, rect.Y);
        return builder.Close();
    }

    private static Vector NormalizeCorner(Vector corner, double width, double height)
    {
        var min = Math.Min(width, height);
        var rx = Math.Clamp(corner.X, 0, min / 2);
        var ry = Math.Clamp(corner.Y, 0, min / 2);
        return new Vector(rx, ry);
    }
}
