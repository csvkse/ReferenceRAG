using System;

namespace ReferenceRAG.DesktopHost;

internal static class StartupWindowSizing
{
    public static (int Width, int Height, int MinWidth, int MinHeight) Calculate(uint dpi, int workWidth, int workHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(workWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(workHeight);
        var scale = (dpi == 0 ? 96 : dpi) / 96d;
        int ScaleToFit(int logicalSize, int available) =>
            (int)Math.Min(available, Math.Round(logicalSize * scale, MidpointRounding.AwayFromZero));

        return (ScaleToFit(1360, workWidth), ScaleToFit(860, workHeight),
            ScaleToFit(960, workWidth), ScaleToFit(640, workHeight));
    }
}
