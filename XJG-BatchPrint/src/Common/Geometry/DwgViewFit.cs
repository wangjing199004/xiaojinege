using System;

namespace ZwcadBatchPlot;

internal static class DwgViewFit
{
    internal readonly struct Result
    {
        internal Result(double x, double y, double width, double height)
        { CenterX = x; CenterY = y; Width = width; Height = height; }
        internal double CenterX { get; }
        internal double CenterY { get; }
        internal double Width { get; }
        internal double Height { get; }
    }

    internal static Result Calculate(double minX, double minY, double maxX, double maxY, double aspect)
    {
        var width = maxX - minX;
        var height = maxY - minY;
        if (!Finite(minX) || !Finite(minY) || !Finite(maxX) || !Finite(maxY)
            || !Finite(width) || !Finite(height) || width <= 0 || height <= 0)
            throw new InvalidOperationException("拆图图框边界无效，无法设置打开视图。");
        if (!Finite(aspect) || aspect <= 0) aspect = 16d / 9d;
        var viewHeight = Math.Max(height, width / aspect) * 1.1d;
        var viewWidth = viewHeight * aspect;
        if (!Finite(viewWidth) || !Finite(viewHeight))
            throw new InvalidOperationException("拆图显示范围过大，无法设置打开视图。");
        return new Result(minX + width / 2d, minY + height / 2d, viewWidth, viewHeight);
    }

    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
