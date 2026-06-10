using System.Globalization;
using System.Threading;
using Bansa.ViewModels;
using Xunit;

namespace Bansa.Tests;

public class FormatTests
{
    public FormatTests()
    {
        // FormatScaled uses CurrentCulture for the decimal separator.
        Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
        App.Settings.RateUnit = "Bytes";
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1048576, "1 MB")]
    [InlineData(1073741824, "1 GB")]
    public void BytesUsesBinaryScaling(long bytes, string expected)
        => Assert.Equal(expected, Format.Bytes(bytes));

    [Fact]
    public void RateAppendsPerSecond()
        => Assert.Equal("2 KB/s", Format.Rate(2048));

    [Fact]
    public void BitsUnitMultipliesByEight()
    {
        App.Settings.RateUnit = "Bits";
        try
        {
            Assert.Equal("8 Kb/s", Format.Rate(1024));
        }
        finally { App.Settings.RateUnit = "Bytes"; }
    }

    [Theory]
    [InlineData(0, "unlimited")]
    [InlineData(-5, "unlimited")]
    [InlineData(512, "512 KB/s")]
    [InlineData(1024, "1 MB/s")]
    [InlineData(1536, "1.5 MB/s")]
    public void KBpsFormatsLimits(int kbs, string expected)
        => Assert.Equal(expected, Format.KBps(kbs));
}
