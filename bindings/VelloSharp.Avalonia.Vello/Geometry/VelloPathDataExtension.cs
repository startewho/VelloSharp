using SkiaSharp;

namespace VelloSharp.Avalonia.Vello.Geometry
{
    internal static class VelloPathDataExtension
    {
        public static SKPath ToSKPath(this VelloPathData.PathCommand[] cmds)
        {
            var skPath = new SKPath();
            foreach (var command in cmds)
            {
                switch (command.Verb)
                {
                    case VelloPathVerb.MoveTo:
                        skPath.MoveTo((float)command.X0, (float)command.Y0);
                        break;
                    case VelloPathVerb.LineTo:
                        skPath.LineTo((float)command.X0, (float)command.Y0);
                        break;
                    case VelloPathVerb.QuadTo:
                        skPath.QuadTo((float)command.X0, (float)command.Y0, (float)command.X1, (float)command.Y1);
                        break;
                    case VelloPathVerb.CubicTo:
                        skPath.CubicTo((float)command.X0, (float)command.Y0, (float)command.X1, (float)command.Y1, (float)command.X2, (float)command.Y2);
                        break;
                    case VelloPathVerb.Close:
                        skPath.Close();
                        break;
                }
            }
            return skPath;
        }
    }
}
