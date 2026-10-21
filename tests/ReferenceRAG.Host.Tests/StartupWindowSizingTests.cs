using ReferenceRAG.DesktopHost;
using Xunit;

namespace ReferenceRAG.Host.Tests;

public sealed class StartupWindowSizingTests
{
    [Theory]
    [InlineData(96, 1360, 860, 960, 640)]
    [InlineData(120, 1700, 1075, 1200, 800)]
    [InlineData(144, 2040, 1290, 1440, 960)]
    [InlineData(192, 2720, 1720, 1920, 1280)]
    [InlineData(0, 1360, 860, 960, 640)]
    public void ConvertsLogicalDimensionsToPhysicalPixels(uint dpi, int width, int height, int minWidth, int minHeight)
    {
        Assert.Equal((width, height, minWidth, minHeight), StartupWindowSizing.Calculate(dpi, 3840, 2160));
    }

    [Theory]
    [InlineData(144, 1920, 1040, 1920, 1040, 1440, 960)]
    [InlineData(192, 1280, 720, 1280, 720, 1280, 720)]
    [InlineData(96, 800, 480, 800, 480, 800, 480)]
    public void FitsWindowAndMinimumInsideWorkArea(uint dpi, int workWidth, int workHeight, int width, int height, int minWidth, int minHeight)
    {
        Assert.Equal((width, height, minWidth, minHeight), StartupWindowSizing.Calculate(dpi, workWidth, workHeight));
    }
}
