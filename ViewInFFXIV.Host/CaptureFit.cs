namespace ViewInFFXIV.Host;

internal static class CaptureFit
{
    public static (int Width, int Height) FitInside(int srcW, int srcH, int maxW, int maxH)
    {
        srcW = Math.Max(2, srcW);
        srcH = Math.Max(2, srcH);
        if (srcW <= maxW && srcH <= maxH)
            return (srcW, srcH);

        var dest = DestRect(srcW, srcH, maxW, maxH);
        return (Math.Max(2, dest.Width), Math.Max(2, dest.Height));
    }

    public static (int X, int Y, int Width, int Height) DestRect(int srcW, int srcH, int dstW, int dstH)
    {
        srcW = Math.Max(1, srcW);
        srcH = Math.Max(1, srcH);
        dstW = Math.Max(1, dstW);
        dstH = Math.Max(1, dstH);
        var scale = Math.Min(dstW / (float)srcW, dstH / (float)srcH);
        var width = Math.Max(1, (int)MathF.Round(srcW * scale));
        var height = Math.Max(1, (int)MathF.Round(srcH * scale));
        if (width > dstW)
            width = dstW;
        if (height > dstH)
            height = dstH;
        var x = (dstW - width) / 2;
        var y = (dstH - height) / 2;
        return (x, y, width, height);
    }
}
