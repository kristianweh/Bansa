using System.Collections.Generic;
using Bansa.Services;
using Xunit;

namespace Bansa.Tests;

public class ChartFxTests
{
    [Fact]
    public void RadiusZeroReturnsInputUnchanged()
    {
        var series = new List<(long, long)> { (1, 2), (3, 4), (5, 6) };
        Assert.Same(series, ChartFx.Smooth(series, radius: 0));
    }

    [Fact]
    public void TinySeriesReturnsInputUnchanged()
    {
        var series = new List<(long, long)> { (100, 100), (200, 200) };
        Assert.Same(series, ChartFx.Smooth(series, radius: 3));
    }

    [Fact]
    public void ConstantSeriesStaysConstant()
    {
        var series = new List<(long, long)>();
        for (int i = 0; i < 20; i++) series.Add((1000, 500));
        var smooth = ChartFx.Smooth(series, radius: 3);
        foreach (var (down, up) in smooth)
        {
            Assert.Equal(1000, down);
            Assert.Equal(500, up);
        }
    }

    [Fact]
    public void CentredWindowAveragesNeighbours()
    {
        var series = new List<(long, long)> { (0, 0), (10, 100), (20, 200) };
        var smooth = ChartFx.Smooth(series, radius: 1);
        // Edges average the 2 available samples; the centre averages all 3.
        Assert.Equal((5, 50),    smooth[0]);
        Assert.Equal((10, 100),  smooth[1]);
        Assert.Equal((15, 150),  smooth[2]);
    }

    [Fact]
    public void SmoothingPreservesLength()
    {
        var series = new List<(long, long)>();
        for (int i = 0; i < 50; i++) series.Add((i * 100, i * 10));
        Assert.Equal(50, ChartFx.Smooth(series).Count);
    }
}
