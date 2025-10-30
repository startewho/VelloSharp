using System;
using System.Collections.Generic;
using System.Numerics;
using VelloSharp;
using SkiaSharpShim;

namespace SkiaSharp;

public sealed class SKPath : IDisposable
{
    private readonly List<PathCommand> _commands = new();

    public SKPath()
    {
    }

    public SKPath(SKPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        FillType = path.FillType;
        _commands.AddRange(path._commands);
    }

    public SKPathFillType FillType { get; set; } = SKPathFillType.Winding;

    public bool IsEmpty => _commands.Count == 0;



    public SKRect TightBounds
    {
        get
        {
            if (_commands.Count == 0)
            {
                return new SKRect(0, 0, 0, 0);
            }

            var minX = float.PositiveInfinity;
            var minY = float.PositiveInfinity;
            var maxX = float.NegativeInfinity;
            var maxY = float.NegativeInfinity;
            var current = new SKPoint(0, 0);
            var hasPoint = false;

            void Include(SKPoint point)
            {
                minX = MathF.Min(minX, point.X);
                minY = MathF.Min(minY, point.Y);
                maxX = MathF.Max(maxX, point.X);
                maxY = MathF.Max(maxY, point.Y);
                hasPoint = true;
            }

            foreach (var command in _commands)
            {
                switch (command.Verb)
                {
                    case PathVerb.MoveTo:
                        current = command.Point0;
                        Include(current);
                        break;
                    case PathVerb.LineTo:
                        Include(current);
                        Include(command.Point0);
                        current = command.Point0;
                        break;
                    case PathVerb.QuadTo:
                    case PathVerb.ConicTo:
                        Include(current);
                        Include(command.Point0);
                        Include(command.Point1);
                        current = command.Point1;
                        break;
                    case PathVerb.CubicTo:
                        Include(current);
                        Include(command.Point0);
                        Include(command.Point1);
                        Include(command.Point2);
                        current = command.Point2;
                        break;
                    case PathVerb.Close:
                        break;
                }
            }

            if (!hasPoint || float.IsInfinity(minX) || float.IsInfinity(minY) || float.IsInfinity(maxX) || float.IsInfinity(maxY))
            {
                return new SKRect(0, 0, 0, 0);
            }

            return new SKRect(minX, minY, maxX, maxY);
        }
    }

    public bool Contains(float x, float y)
    {
        if (TryContainsNative(x, y, out var nativeResult))
        {
            return nativeResult;
        }

        return ContainsManaged(x, y);
    }

    private bool TryContainsNative(float x, float y, out bool result)
    {
        if (_commands.Count == 0)
        {
            result = FillType is SKPathFillType.InverseEvenOdd or SKPathFillType.InverseWinding;
            return true;
        }

        var builder = ToPathBuilder();
        using var nativePath = NativePathElements.Rent(builder);
        var span = nativePath.Span;
        if (span.IsEmpty)
        {
            var insideBounds = TightBounds.Contains(new SKPoint(x, y));
            result = FillType switch
            {
                SKPathFillType.InverseWinding => !insideBounds,
                SKPathFillType.InverseEvenOdd => !insideBounds,
                _ => insideBounds,
            };
            return true;
        }

        var fillRule = FillType switch
        {
            SKPathFillType.EvenOdd or SKPathFillType.InverseEvenOdd => FillRule.EvenOdd,
            _ => FillRule.NonZero,
        };

        bool contains;
        unsafe
        {
            fixed (VelloSharp.VelloPathElement* elementPtr = span)
            {
                var status = VelloSharp.NativeMethods.vello_path_contains_point(
                    elementPtr,
                    (nuint)span.Length,
                    (VelloSharp.VelloFillRule)fillRule,
                    new VelloSharp.VelloPoint { X = x, Y = y },
                    out contains);

                if (status != VelloSharp.VelloStatus.Success)
                {
                    result = false;
                    return false;
                }
            }
        }

        if (FillType is SKPathFillType.InverseWinding or SKPathFillType.InverseEvenOdd)
        {
            contains = !contains;
        }

        result = contains;
        return true;
    }

    private bool ContainsManaged(float x, float y)
    {
        if (_commands.Count == 0)
        {
            return FillType is SKPathFillType.InverseEvenOdd or SKPathFillType.InverseWinding;
        }

        var segments = ListPool<Segment>.Shared.Rent(Math.Max(_commands.Count, 4));
        try
        {
            BuildSegments(segments);

            if (segments.Count == 0)
            {
                var insideBounds = TightBounds.Contains(new SKPoint(x, y));
                var contains = FillType switch
                {
                    SKPathFillType.InverseWinding => !insideBounds,
                    SKPathFillType.InverseEvenOdd => !insideBounds,
                    _ => insideBounds,
                };
                return contains;
            }

            var point = new Vector2(x, y);
            bool inside;

            if (PointOnBoundary(segments, point))
            {
                inside = true;
            }
            else
            {
                var evenOddInside = IsPointInsideEvenOdd(segments, point);
                var windingNumber = ComputeWindingNumber(segments, point);
                inside = FillType switch
                {
                    SKPathFillType.Winding or SKPathFillType.InverseWinding => windingNumber != 0,
                    SKPathFillType.EvenOdd or SKPathFillType.InverseEvenOdd => evenOddInside,
                    _ => evenOddInside,
                };
            }

            if (FillType is SKPathFillType.InverseWinding or SKPathFillType.InverseEvenOdd)
            {
                inside = !inside;
            }

            return inside;
        }
        finally
        {
            segments.Clear();
            ListPool<Segment>.Shared.Return(segments);
        }
    }

    private void BuildSegments(List<Segment> segments)
    {
        const int quadraticSteps = 8;
        const int cubicSteps = 16;
        const int conicSteps = 8;

        var current = Vector2.Zero;
        var subpathStart = Vector2.Zero;
        var haveCurrent = false;
        var haveStart = false;

        foreach (var command in _commands)
        {
            switch (command.Verb)
            {
                case PathVerb.MoveTo:
                    current = command.Point0.ToVector2();
                    subpathStart = current;
                    haveCurrent = true;
                    haveStart = true;
                    break;

                case PathVerb.LineTo:
                    if (haveCurrent)
                    {
                        var next = command.Point0.ToVector2();
                        AddSegment(segments, current, next);
                        current = next;
                    }
                    break;

                case PathVerb.QuadTo:
                    if (haveCurrent)
                    {
                        var ctrl = command.Point0.ToVector2();
                        var end = command.Point1.ToVector2();
                        FlattenQuadratic(current, ctrl, end, quadraticSteps, segments);
                        current = end;
                    }
                    break;

                case PathVerb.ConicTo:
                    if (haveCurrent)
                    {
                        var ctrl = command.Point0.ToVector2();
                        var end = command.Point1.ToVector2();
                        FlattenConic(current, ctrl, end, command.Weight, conicSteps, segments);
                        current = end;
                    }
                    break;

                case PathVerb.CubicTo:
                    if (haveCurrent)
                    {
                        var ctrl1 = command.Point0.ToVector2();
                        var ctrl2 = command.Point1.ToVector2();
                        var end = command.Point2.ToVector2();
                        FlattenCubic(current, ctrl1, ctrl2, end, cubicSteps, segments);
                        current = end;
                    }
                    break;

                case PathVerb.Close:
                    if (haveCurrent && haveStart)
                    {
                        AddSegment(segments, current, subpathStart);
                        current = subpathStart;
                    }
                    haveStart = false;
                    break;
            }
        }
    }

    private static void AddSegment(List<Segment> segments, Vector2 start, Vector2 end)
    {
        if (Vector2.DistanceSquared(start, end) <= SegmentEpsilon * SegmentEpsilon)
        {
            return;
        }

        segments.Add(new Segment(start, end));
    }

    private static void FlattenQuadratic(Vector2 start, Vector2 control, Vector2 end, int steps, List<Segment> segments)
    {
        var previous = start;
        for (var i = 1; i <= steps; i++)
        {
            var t = i / (float)steps;
            var point = EvaluateQuadratic(start, control, end, t);
            AddSegment(segments, previous, point);
            previous = point;
        }
    }

    private static void FlattenConic(Vector2 start, Vector2 control, Vector2 end, float weight, int steps, List<Segment> segments)
    {
        if (float.IsNaN(weight) || float.IsInfinity(weight) || MathF.Abs(weight) < 1e-5f)
        {
            weight = 1f;
        }

        var previous = start;
        for (var i = 1; i <= steps; i++)
        {
            var t = i / (float)steps;
            var point = EvaluateConic(start, control, end, weight, t);
            AddSegment(segments, previous, point);
            previous = point;
        }
    }

    private static void FlattenCubic(Vector2 start, Vector2 control1, Vector2 control2, Vector2 end, int steps, List<Segment> segments)
    {
        var previous = start;
        for (var i = 1; i <= steps; i++)
        {
            var t = i / (float)steps;
            var point = EvaluateCubic(start, control1, control2, end, t);
            AddSegment(segments, previous, point);
            previous = point;
        }
    }

    private static Vector2 EvaluateQuadratic(Vector2 p0, Vector2 p1, Vector2 p2, float t)
    {
        var mt = 1f - t;
        var mt2 = mt * mt;
        var t2 = t * t;
        return new Vector2(
            mt2 * p0.X + 2f * mt * t * p1.X + t2 * p2.X,
            mt2 * p0.Y + 2f * mt * t * p1.Y + t2 * p2.Y);
    }

    private static Vector2 EvaluateCubic(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
    {
        var mt = 1f - t;
        var mt2 = mt * mt;
        var mt3 = mt2 * mt;
        var t2 = t * t;
        var t3 = t2 * t;

        return new Vector2(
            mt3 * p0.X + 3f * mt2 * t * p1.X + 3f * mt * t2 * p2.X + t3 * p3.X,
            mt3 * p0.Y + 3f * mt2 * t * p1.Y + 3f * mt * t2 * p2.Y + t3 * p3.Y);
    }

    private static Vector2 EvaluateConic(Vector2 p0, Vector2 p1, Vector2 p2, float weight, float t)
    {
        if (MathF.Abs(weight - 1f) < 1e-3f)
        {
            return EvaluateQuadratic(p0, p1, p2, t);
        }

        var mt = 1f - t;
        var w = weight;
        var denom = mt * mt + 2f * w * mt * t + t * t;
        if (MathF.Abs(denom) < 1e-6f)
        {
            return p2;
        }

        var x = (mt * mt * p0.X + 2f * w * mt * t * p1.X + t * t * p2.X) / denom;
        var y = (mt * mt * p0.Y + 2f * w * mt * t * p1.Y + t * t * p2.Y) / denom;
        return new Vector2(x, y);
    }

    private static bool PointOnBoundary(List<Segment> segments, Vector2 point)
    {
        foreach (var segment in segments)
        {
            if (IsPointOnSegment(segment.Start, segment.End, point))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPointOnSegment(Vector2 a, Vector2 b, Vector2 point)
    {
        var ab = b - a;
        var ap = point - a;

        if (ab.LengthSquared() <= SegmentEpsilon * SegmentEpsilon)
        {
            return Vector2.DistanceSquared(a, point) <= SegmentEpsilon * SegmentEpsilon;
        }

        var cross = ab.X * ap.Y - ab.Y * ap.X;
        if (MathF.Abs(cross) > BoundaryEpsilon)
        {
            return false;
        }

        var dot = Vector2.Dot(ap, ab);
        if (dot < -BoundaryEpsilon)
        {
            return false;
        }

        var abLengthSquared = ab.LengthSquared();
        if (dot - abLengthSquared > BoundaryEpsilon)
        {
            return false;
        }

        return true;
    }

    private static bool IsPointInsideEvenOdd(List<Segment> segments, Vector2 point)
    {
        var inside = false;
        foreach (var segment in segments)
        {
            if (RayIntersectsSegment(point, segment.Start, segment.End))
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static bool RayIntersectsSegment(Vector2 point, Vector2 a, Vector2 b)
    {
        if (a.Y > b.Y)
        {
            (a, b) = (b, a);
        }

        if (point.Y <= a.Y || point.Y > b.Y)
        {
            return false;
        }

        if (MathF.Abs(a.Y - b.Y) < BoundaryEpsilon)
        {
            return false;
        }

        var xIntersect = a.X + (point.Y - a.Y) * (b.X - a.X) / (b.Y - a.Y);
        return xIntersect > point.X;
    }

    private static int ComputeWindingNumber(List<Segment> segments, Vector2 point)
    {
        var winding = 0;
        foreach (var segment in segments)
        {
            var a = segment.Start;
            var b = segment.End;

            if (MathF.Abs(a.Y - b.Y) < BoundaryEpsilon)
            {
                continue;
            }

            if (a.Y <= point.Y)
            {
                if (b.Y > point.Y && IsLeft(a, b, point) > 0f)
                {
                    winding++;
                }
            }
            else if (b.Y <= point.Y && IsLeft(a, b, point) < 0f)
            {
                winding--;
            }
        }

        return winding;
    }

    private static float IsLeft(Vector2 a, Vector2 b, Vector2 point)
        => (b.X - a.X) * (point.Y - a.Y) - (point.X - a.X) * (b.Y - a.Y);

    private const float SegmentEpsilon = 1e-4f;
    private const float BoundaryEpsilon = 1e-4f;

    private readonly record struct Segment(Vector2 Start, Vector2 End);

    public void MoveTo(float x, float y) => MoveTo(new SKPoint(x, y));

    public void MoveTo(SKPoint point)
    {
        _commands.Add(new PathCommand(PathVerb.MoveTo, point));
    }

    public void LineTo(float x, float y) => LineTo(new SKPoint(x, y));

    public void LineTo(SKPoint point)
    {
        _commands.Add(new PathCommand(PathVerb.LineTo, point));
    }

    public void QuadTo(SKPoint control, SKPoint end)
    {
        _commands.Add(new PathCommand(PathVerb.QuadTo, control, end));
    }

    public void QuadTo(float x1, float y1, float x2, float y2) =>
        QuadTo(new SKPoint(x1, y1), new SKPoint(x2, y2));

    public void ConicTo(SKPoint control, SKPoint end, float weight)
    {
        _commands.Add(new PathCommand(PathVerb.ConicTo, control, end, default, weight));
    }

    public void ConicTo(float x1, float y1, float x2, float y2, float weight) =>
        ConicTo(new SKPoint(x1, y1), new SKPoint(x2, y2), weight);

    public void CubicTo(SKPoint control1, SKPoint control2, SKPoint end)
    {
        _commands.Add(new PathCommand(PathVerb.CubicTo, control1, control2, end));
    }

    public void CubicTo(float x1, float y1, float x2, float y2, float x3, float y3) =>
        CubicTo(new SKPoint(x1, y1), new SKPoint(x2, y2), new SKPoint(x3, y3));

    public void AddRect(SKRect rect) => AddRect(rect, SKPathDirection.Clockwise);

    public void AddRect(SKRect rect, SKPathDirection direction)
    {
        if (rect.IsEmpty)
        {
            return;
        }

        var topLeft = new SKPoint(rect.Left, rect.Top);
        var topRight = new SKPoint(rect.Right, rect.Top);
        var bottomRight = new SKPoint(rect.Right, rect.Bottom);
        var bottomLeft = new SKPoint(rect.Left, rect.Bottom);

        MoveTo(topLeft);
        if (direction == SKPathDirection.Clockwise)
        {
            LineTo(topRight);
            LineTo(bottomRight);
            LineTo(bottomLeft);
        }
        else
        {
            LineTo(bottomLeft);
            LineTo(bottomRight);
            LineTo(topRight);
        }

        Close();
    }

    public void AddOval(SKRect rect) => AddOval(rect, SKPathDirection.Clockwise);

    public void AddOval(SKRect rect, SKPathDirection direction)
    {
        if (rect.IsEmpty)
        {
            return;
        }

        var cx = (rect.Left + rect.Right) * 0.5f;
        var cy = (rect.Top + rect.Bottom) * 0.5f;
        var rx = rect.Width * 0.5f;
        var ry = rect.Height * 0.5f;
        if (rx == 0f || ry == 0f)
        {
            return;
        }

        const float kappa = 0.552284749831f;
        var ox = rx * kappa;
        var oy = ry * kappa;

        var start = new SKPoint(rect.Right, cy);
        MoveTo(start);

        if (direction == SKPathDirection.Clockwise)
        {
            CubicTo(
                new SKPoint(rect.Right, cy + oy),
                new SKPoint(cx + ox, rect.Bottom),
                new SKPoint(cx, rect.Bottom));
            CubicTo(
                new SKPoint(cx - ox, rect.Bottom),
                new SKPoint(rect.Left, cy + oy),
                new SKPoint(rect.Left, cy));
            CubicTo(
                new SKPoint(rect.Left, cy - oy),
                new SKPoint(cx - ox, rect.Top),
                new SKPoint(cx, rect.Top));
            CubicTo(
                new SKPoint(cx + ox, rect.Top),
                new SKPoint(rect.Right, cy - oy),
                start);
        }
        else
        {
            CubicTo(
                new SKPoint(rect.Right, cy - oy),
                new SKPoint(cx + ox, rect.Top),
                new SKPoint(cx, rect.Top));
            CubicTo(
                new SKPoint(cx - ox, rect.Top),
                new SKPoint(rect.Left, cy - oy),
                new SKPoint(rect.Left, cy));
            CubicTo(
                new SKPoint(rect.Left, cy + oy),
                new SKPoint(cx - ox, rect.Bottom),
                new SKPoint(cx, rect.Bottom));
            CubicTo(
                new SKPoint(cx + ox, rect.Bottom),
                new SKPoint(rect.Right, cy + oy),
                start);
        }

        Close();
    }

    public void AddPath(SKPath path)
    {
        AppendPath(path, null);
    }

    public void AddPath(SKPath path, float dx, float dy)
    {
        var transform = Matrix3x2.CreateTranslation(dx, dy);
        AppendPath(path, transform);
    }

    public void AddPath(SKPath path, SKMatrix matrix)
    {
        AppendPath(path, matrix.ToMatrix3x2());
    }

    public void ArcTo(float rx, float ry, float xAxisRotate, SKPathArcSize arcSize, SKPathDirection direction, float x, float y)
    {
        var endPoint = new SKPoint(x, y);
        if (!TryGetCurrentPoint(out var current))
        {
            MoveTo(endPoint);
            return;
        }

        if (Math.Abs(rx) < float.Epsilon || Math.Abs(ry) < float.Epsilon || Approximately(current, endPoint))
        {
            LineTo(endPoint);
            return;
        }

        var sweepPositive = direction == SKPathDirection.Clockwise;
        var largeArc = arcSize == SKPathArcSize.Large;

        var x0 = current.X;
        var y0 = current.Y;
        var x1 = endPoint.X;
        var y1 = endPoint.Y;

        var rxAbs = Math.Abs((double)rx);
        var ryAbs = Math.Abs((double)ry);

        var phi = DegreesToRadians(xAxisRotate);
        var cosPhi = Math.Cos(phi);
        var sinPhi = Math.Sin(phi);

        var dx2 = (x0 - x1) / 2.0;
        var dy2 = (y0 - y1) / 2.0;

        var x1Prime = cosPhi * dx2 + sinPhi * dy2;
        var y1Prime = -sinPhi * dx2 + cosPhi * dy2;

        if (Math.Abs(x1Prime) < 1e-12 && Math.Abs(y1Prime) < 1e-12)
        {
            LineTo(endPoint);
            return;
        }

        var rxSq = rxAbs * rxAbs;
        var rySq = ryAbs * ryAbs;
        var x1PrimeSq = x1Prime * x1Prime;
        var y1PrimeSq = y1Prime * y1Prime;

        var radiiCheck = x1PrimeSq / rxSq + y1PrimeSq / rySq;
        if (radiiCheck > 1)
        {
            var scale = Math.Sqrt(radiiCheck);
            rxAbs *= scale;
            ryAbs *= scale;
            rxSq = rxAbs * rxAbs;
            rySq = ryAbs * ryAbs;
        }

        var sign = (largeArc == sweepPositive) ? -1.0 : 1.0;
        var numerator = Math.Max(0.0, rxSq * rySq - rxSq * y1PrimeSq - rySq * x1PrimeSq);
        var denom = rxSq * y1PrimeSq + rySq * x1PrimeSq;
        var coef = denom == 0 ? 0 : sign * Math.Sqrt(numerator / denom);

        var cxPrime = coef * (rxAbs * y1Prime / ryAbs);
        var cyPrime = coef * (-ryAbs * x1Prime / rxAbs);

        var cx = cosPhi * cxPrime - sinPhi * cyPrime + (x0 + x1) / 2.0;
        var cy = sinPhi * cxPrime + cosPhi * cyPrime + (y0 + y1) / 2.0;

        var startVector = new Vector2d((x1Prime - cxPrime) / rxAbs, (y1Prime - cyPrime) / ryAbs);
        var endVector = new Vector2d((-x1Prime - cxPrime) / rxAbs, (-y1Prime - cyPrime) / ryAbs);

        var startAngle = AngleBetween(new Vector2d(1, 0), startVector);
        var sweepAngle = AngleBetween(startVector, endVector);

        if (!largeArc && Math.Abs(sweepAngle) > Math.PI)
        {
            sweepAngle += sweepAngle > 0 ? -2 * Math.PI : 2 * Math.PI;
        }
        else if (largeArc && Math.Abs(sweepAngle) < Math.PI)
        {
            sweepAngle += sweepAngle > 0 ? 2 * Math.PI : -2 * Math.PI;
        }

        if (!sweepPositive && sweepAngle > 0)
        {
            sweepAngle -= 2 * Math.PI;
        }
        else if (sweepPositive && sweepAngle < 0)
        {
            sweepAngle += 2 * Math.PI;
        }

        var segments = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweepAngle) / (Math.PI / 2.0)));
        var delta = sweepAngle / segments;
        var t = startAngle;

        for (var i = 0; i < segments; i++)
        {
            var t1 = t;
            var t2 = t1 + delta;
            AddArcSegment(cx, cy, rxAbs, ryAbs, cosPhi, sinPhi, t1, t2);
            t = t2;
        }
    }

    public void Reset() => _commands.Clear();

    public void Close() => _commands.Add(PathCommand.ClosePath);

    public void Dispose()
    {
        _commands.Clear();
    }

    public SKPath Clone() => new(this);

    public void Transform(SKMatrix matrix) => Transform(in matrix);

    public void Transform(in SKMatrix matrix)
    {
        var transform = matrix.ToMatrix3x2();
        for (var i = 0; i < _commands.Count; i++)
        {
            var command = _commands[i];
            _commands[i] = TransformCommand(command, transform);
        }
    }

    public Iterator CreateIterator(bool forceClose) => new(this, forceClose);

    public Iterator CreateIterator() => CreateIterator(false);

    public bool Op(SKPath other, SKPathOp operation, SKPath result)
    {
        ArgumentNullException.ThrowIfNull(other);
        ArgumentNullException.ThrowIfNull(result);

        var clipBounds = ComputeClipBounds(this, other);
        using var left = NormalizeOperandForOp(this, clipBounds);
        using var right = NormalizeOperandForOp(other, clipBounds);

        if (!PathOps.TryCompute(left, right, operation, out var computed) || computed is null)
        {
            return false;
        }

        try
        {
            result.Reset();
            result.FillType = computed.FillType;
            result.AddPath(computed);
            return !result.IsEmpty;
        }
        finally
        {
            if (!ReferenceEquals(result, computed))
            {
                computed.Dispose();
            }
        }
    }

    public SKPath? Op(SKPath other, SKPathOp operation)
    {
        ArgumentNullException.ThrowIfNull(other);
        var clipBounds = ComputeClipBounds(this, other);
        using var left = NormalizeOperandForOp(this, clipBounds);
        using var right = NormalizeOperandForOp(other, clipBounds);

        if (!PathOps.TryCompute(left, right, operation, out var result))
        {
            return null;
        }

        return result;
    }

    public PathBuilder ToPathBuilder()
    {
        var builder = new PathBuilder();
        foreach (var command in _commands)
        {
            switch (command.Verb)
            {
                case PathVerb.MoveTo:
                    builder.MoveTo(command.Point0.X, command.Point0.Y);
                    break;
                case PathVerb.LineTo:
                    builder.LineTo(command.Point0.X, command.Point0.Y);
                    break;
                case PathVerb.QuadTo:
                    builder.QuadraticTo(command.Point0.X, command.Point0.Y, command.Point1.X, command.Point1.Y);
                    break;
                case PathVerb.CubicTo:
                    builder.CubicTo(
                        command.Point0.X,
                        command.Point0.Y,
                        command.Point1.X,
                        command.Point1.Y,
                        command.Point2.X,
                        command.Point2.Y);
                    break;
                case PathVerb.Close:
                    builder.Close();
                    break;
            }
        }

        return builder;
    }

    public sealed class Iterator : IDisposable
    {
        private readonly SKPath _path;
        private readonly bool _forceClose;
        private int _index;
        private SKPoint _current;
        private SKPoint _first;
        private float _conicWeight;

        internal Iterator(SKPath path, bool forceClose)
        {
            _path = path;
            _forceClose = forceClose;
            _current = default;
            _first = default;
        }

        public SKPathVerb Next(SKPoint[] points)
        {
            ArgumentNullException.ThrowIfNull(points);
            if (points.Length < 4)
            {
                throw new ArgumentException("Iterator requires at least four points.", nameof(points));
            }

            if (_index >= _path._commands.Count)
            {
                return SKPathVerb.Done;
            }

            ShimNotImplemented.Throw($"{nameof(Iterator)}.{nameof(Next)}");

            var command = _path._commands[_index++];
            switch (command.Verb)
            {
                case PathVerb.MoveTo:
                    _current = command.Point0;
                    _first = _current;
                    points[0] = _current;
                    return SKPathVerb.Move;
                case PathVerb.LineTo:
                    points[0] = _current;
                    points[1] = command.Point0;
                    _current = command.Point0;
                    return SKPathVerb.Line;
                case PathVerb.QuadTo:
                    points[0] = _current;
                    points[1] = command.Point0;
                    points[2] = command.Point1;
                    _current = command.Point1;
                    return SKPathVerb.Quad;
                case PathVerb.ConicTo:
                    points[0] = _current;
                    points[1] = command.Point0;
                    points[2] = command.Point1;
                    _current = command.Point1;
                    _conicWeight = command.Weight;
                    return SKPathVerb.Conic;
                case PathVerb.CubicTo:
                    points[0] = _current;
                    points[1] = command.Point0;
                    points[2] = command.Point1;
                    points[3] = command.Point2;
                    _current = command.Point2;
                    return SKPathVerb.Cubic;
                case PathVerb.Close:
                    points[0] = _first;
                    if (_forceClose)
                    {
                        _current = _first;
                    }
                    return SKPathVerb.Close;
                default:
                    return SKPathVerb.Done;
            }
        }

        public float ConicWeight()
        {
            ShimNotImplemented.Throw($"{nameof(Iterator)}.{nameof(ConicWeight)}");
            return _conicWeight;
        }

        public void Dispose()
        {
        }
    }

    private readonly record struct PathCommand
    {
        public PathCommand(PathVerb verb, SKPoint point0)
            : this(verb, point0, default, default, 0f)
        {
        }

        public PathCommand(PathVerb verb, SKPoint point0, SKPoint point1)
            : this(verb, point0, point1, default, 0f)
        {
        }

        public PathCommand(PathVerb verb, SKPoint point0, SKPoint point1, SKPoint point2, float weight = 0f)
        {
            Verb = verb;
            Point0 = point0;
            Point1 = point1;
            Point2 = point2;
            Weight = weight;
        }

        public static PathCommand ClosePath => new(PathVerb.Close, default);

        public PathVerb Verb { get; }
        public SKPoint Point0 { get; }
        public SKPoint Point1 { get; }
        public SKPoint Point2 { get; }
        public float Weight { get; }
    }

    private void AppendPath(SKPath path, Matrix3x2? transform)
    {
        ArgumentNullException.ThrowIfNull(path);

        var source = ReferenceEquals(path, this)
            ? new List<PathCommand>(_commands)
            : path._commands;

        if (transform is Matrix3x2 matrix)
        {
            foreach (var command in source)
            {
                _commands.Add(TransformCommand(command, matrix));
            }
        }
        else
        {
            _commands.AddRange(source);
        }
    }

    private static PathCommand TransformCommand(PathCommand command, Matrix3x2 transform) => command.Verb switch
    {
        PathVerb.MoveTo => new PathCommand(PathVerb.MoveTo, TransformPoint(command.Point0, transform)),
        PathVerb.LineTo => new PathCommand(PathVerb.LineTo, TransformPoint(command.Point0, transform)),
        PathVerb.QuadTo => new PathCommand(PathVerb.QuadTo, TransformPoint(command.Point0, transform), TransformPoint(command.Point1, transform)),
        PathVerb.ConicTo => new PathCommand(PathVerb.ConicTo, TransformPoint(command.Point0, transform), TransformPoint(command.Point1, transform), default, command.Weight),
        PathVerb.CubicTo => new PathCommand(PathVerb.CubicTo, TransformPoint(command.Point0, transform), TransformPoint(command.Point1, transform), TransformPoint(command.Point2, transform)),
        PathVerb.Close => PathCommand.ClosePath,
        _ => command,
    };

    private static SKPoint TransformPoint(SKPoint point, Matrix3x2 transform)
    {
        var vector = Vector2.Transform(point.ToVector2(), transform);
        return new SKPoint(vector.X, vector.Y);
    }

    private static SKRect ComputeClipBounds(SKPath first, SKPath second)
    {
        var bounds = first.TightBounds;
        bounds.Union(second.TightBounds);

        if (bounds.IsEmpty)
        {
            bounds = SKRect.Create(-1024f, -1024f, 2048f, 2048f);
        }

        var padding = MathF.Max(MathF.Max(bounds.Width, bounds.Height), 1f) * 4f + 32f;
        bounds.Inflate(padding, padding);
        return bounds;
    }

    private static SKPath NormalizeOperandForOp(SKPath source, SKRect clipBounds)
    {
        ArgumentNullException.ThrowIfNull(source);

        var originalFill = source.FillType;
        var normalizedFill = NormalizeFillType(originalFill);
        var clone = new SKPath(source)
        {
            FillType = normalizedFill,
        };

        if (!PathOps.HasInverseFill(originalFill))
        {
            return clone;
        }

        var boundsPath = CreateBoundsPath(clipBounds);
        try
        {
            var complement = PathOps.Compute(boundsPath, clone, SKPathOp.Difference);
            clone.Dispose();

            if (complement is null)
            {
                return new SKPath
                {
                    FillType = SKPathFillType.Winding,
                };
            }

            complement.FillType = SKPathFillType.Winding;
            return complement;
        }
        finally
        {
            boundsPath.Dispose();
        }
    }

    private static SKPathFillType NormalizeFillType(SKPathFillType fillType) => fillType switch
    {
        SKPathFillType.InverseEvenOdd => SKPathFillType.EvenOdd,
        SKPathFillType.InverseWinding => SKPathFillType.Winding,
        _ => fillType,
    };

    private static SKPath CreateBoundsPath(SKRect rect)
    {
        var path = new SKPath
        {
            FillType = SKPathFillType.Winding,
        };
        path.AddRect(rect);
        return path;
    }

    private static double DegreesToRadians(double degrees) => degrees * (Math.PI / 180.0);

    private static bool Approximately(in SKPoint a, in SKPoint b)
        => Math.Abs(a.X - b.X) < 1e-4f && Math.Abs(a.Y - b.Y) < 1e-4f;

    private bool TryGetCurrentPoint(out SKPoint point)
    {
        point = default;
        if (_commands.Count == 0)
        {
            return false;
        }

        var current = new SKPoint(0, 0);
        var first = new SKPoint(0, 0);
        var haveCurrent = false;
        var haveFirst = false;

        foreach (var command in _commands)
        {
            switch (command.Verb)
            {
                case PathVerb.MoveTo:
                    current = command.Point0;
                    first = current;
                    haveCurrent = true;
                    haveFirst = true;
                    break;
                case PathVerb.LineTo:
                    if (!haveFirst)
                    {
                        first = current;
                        haveFirst = true;
                    }
                    current = command.Point0;
                    haveCurrent = true;
                    break;
                case PathVerb.QuadTo:
                case PathVerb.ConicTo:
                    if (!haveFirst)
                    {
                        first = current;
                        haveFirst = true;
                    }
                    current = command.Point1;
                    haveCurrent = true;
                    break;
                case PathVerb.CubicTo:
                    if (!haveFirst)
                    {
                        first = current;
                        haveFirst = true;
                    }
                    current = command.Point2;
                    haveCurrent = true;
                    break;
                case PathVerb.Close:
                    if (haveFirst)
                    {
                        current = first;
                        haveCurrent = true;
                    }
                    break;
            }
        }

        point = current;
        return haveCurrent;
    }

    private void AddArcSegment(double cx, double cy, double rx, double ry, double cosPhi, double sinPhi, double startAngle, double endAngle)
    {
        var delta = endAngle - startAngle;
        var alpha = (4.0 / 3.0) * Math.Tan(delta / 4.0);

        var cosStart = Math.Cos(startAngle);
        var sinStart = Math.Sin(startAngle);
        var cosEnd = Math.Cos(endAngle);
        var sinEnd = Math.Sin(endAngle);

        var p1x = cosStart;
        var p1y = sinStart;
        var p2x = cosEnd;
        var p2y = sinEnd;

        var cp1x = p1x - alpha * p1y;
        var cp1y = p1y + alpha * p1x;
        var cp2x = p2x + alpha * p2y;
        var cp2y = p2y - alpha * p2x;

        var cp1 = TransformArcPoint(cx, cy, rx, ry, cosPhi, sinPhi, cp1x, cp1y);
        var cp2 = TransformArcPoint(cx, cy, rx, ry, cosPhi, sinPhi, cp2x, cp2y);
        var end = TransformArcPoint(cx, cy, rx, ry, cosPhi, sinPhi, p2x, p2y);

        CubicTo(cp1, cp2, end);
    }

    private static SKPoint TransformArcPoint(double cx, double cy, double rx, double ry, double cosPhi, double sinPhi, double x, double y)
    {
        var xr = rx * x;
        var yr = ry * y;
        var xp = cosPhi * xr - sinPhi * yr;
        var yp = sinPhi * xr + cosPhi * yr;
        return new SKPoint((float)(xp + cx), (float)(yp + cy));
    }

    private static double AngleBetween(Vector2d u, Vector2d v)
    {
        var dot = u.X * v.X + u.Y * v.Y;
        var len = Math.Sqrt((u.X * u.X + u.Y * u.Y) * (v.X * v.X + v.Y * v.Y));
        if (len == 0)
        {
            return 0;
        }

        var cos = Math.Clamp(dot / len, -1.0, 1.0);
        var angle = Math.Acos(cos);
        var cross = u.X * v.Y - u.Y * v.X;
        return cross >= 0 ? angle : -angle;
    }

    private readonly struct Vector2d
    {
        public Vector2d(double x, double y)
        {
            X = x;
            Y = y;
        }

        public double X { get; }
        public double Y { get; }
    }

    private enum PathVerb
    {
        MoveTo,
        LineTo,
        QuadTo,
        ConicTo,
        CubicTo,
        Close,
    }
}
