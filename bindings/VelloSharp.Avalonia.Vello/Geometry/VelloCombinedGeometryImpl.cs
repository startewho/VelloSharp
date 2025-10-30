using Avalonia.Media;
using Avalonia.Platform;
using SkiaSharp;
using VelloSharp.Avalonia.Vello.Geometry;
using VelloSharp.Avalonia.Vello.Rendering;

namespace VelloSharp.Avalonia.Vello;

internal sealed class VelloCombinedGeometryImpl : VelloGeometryImplBase
{
    public VelloCombinedGeometryImpl(GeometryCombineMode combineMode, IGeometryImpl g1, IGeometryImpl g2)
        : base(CreateData(combineMode, g1, g2))
    {
        CombineMode = combineMode;
        First = g1;
        Second = g2;
    }

    public GeometryCombineMode CombineMode { get; }

    public IGeometryImpl First { get; }

    public IGeometryImpl Second { get; }

    private static VelloPathData CreateData(GeometryCombineMode combineMode, IGeometryImpl g1, IGeometryImpl g2)
    {
        var data = new VelloPathData();

        if (g1 is VelloGeometryImplBase b1 && g2 is VelloGeometryImplBase b2)
        {
            using var skpath1 = b1.GetCommandsSnapshot().ToSKPath();
            using var skpath2 = b2.GetCommandsSnapshot().ToSKPath();

             var cSkpath = new SKPath();

            switch (combineMode)
            {
                case GeometryCombineMode.Union:
                    cSkpath = skpath1.Op(skpath2, SKPathOp.Union);
                    break;
                case GeometryCombineMode.Intersect:
                    cSkpath = skpath1.Op(skpath2, SKPathOp.Intersect);
                    break;
                case GeometryCombineMode.Xor:
                    cSkpath = skpath1.Op(skpath2, SKPathOp.Xor);
                    break;
                case GeometryCombineMode.Exclude:
                    cSkpath = skpath1.Op(skpath2, SKPathOp.Difference);
                    break;
                default:
                    break;
            }
            if (cSkpath != null)
            {
                var pathBuilder = cSkpath.ToPathBuilder();
                var elements = pathBuilder.AsSpan();
                foreach (var element in elements)
                {
                    switch (element.Verb)
                    {
                        case PathVerb.MoveTo:
                            data.MoveTo(element.X0, element.Y0);
                            break;
                        case PathVerb.LineTo:
                            data.LineTo(element.X0, element.Y0);
                            break;
                        case PathVerb.QuadTo:
                            data.QuadraticTo(element.X0, element.Y0, element.X1, element.Y1);
                            break;
                        case PathVerb.CubicTo:
                            data.CubicTo(element.X0, element.Y0, element.X1, element.Y1, element.X2, element.Y2);
                            break;
                        case PathVerb.Close:
                            data.Close();
                            break;
                        default:
                            break;
                    }
                }
                return data;
            }

        }

        return data;
    }





}
