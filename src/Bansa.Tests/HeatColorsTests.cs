using Bansa.Services;
using Xunit;
using Color = System.Windows.Media.Color;

namespace Bansa.Tests;

public class HeatColorsTests
{
    private static readonly Color Cool = Color.FromRgb(0x00, 0x00, 0xFF);
    private static readonly Color Warm = Color.FromRgb(0xFF, 0xFF, 0x00);
    private static readonly Color Hot  = Color.FromRgb(0xFF, 0x00, 0x00);

    public HeatColorsTests()
    {
        App.Settings.TempWarmThresholdC   = 60;
        App.Settings.TempHotThresholdC    = 80;
        App.Settings.TempBandCoolColorHex = "#0000FF";
        App.Settings.TempBandWarmColorHex = "#FFFF00";
        App.Settings.TempBandHotColorHex  = "#FF0000";
    }

    [Theory]
    [InlineData(0)]
    [InlineData(40)]
    [InlineData(58.4)]   // just below the eased window (60 − 1.5)
    public void BelowWarmIsFlatCool(double t) => Assert.Equal(Cool, HeatColors.Temp(t));

    [Theory]
    [InlineData(61.6)]   // just above the eased window (60 + 1.5)
    [InlineData(70)]
    [InlineData(78.4)]
    public void MiddleBandIsFlatWarm(double t) => Assert.Equal(Warm, HeatColors.Temp(t));

    [Theory]
    [InlineData(81.6)]
    [InlineData(95)]
    [InlineData(150)]
    public void AboveHotIsFlatHot(double t) => Assert.Equal(Hot, HeatColors.Temp(t));

    [Fact]
    public void ThresholdCrossingIsEasedNotSnapped()
    {
        // Exactly at the warm threshold = midpoint of the eased window → a true blend.
        var c = HeatColors.Temp(60);
        Assert.NotEqual(Cool, c);
        Assert.NotEqual(Warm, c);
    }

    [Fact]
    public void InvertedThresholdsDoNotThrow()
    {
        App.Settings.TempWarmThresholdC = 80;
        App.Settings.TempHotThresholdC  = 70;   // user error: hot below warm
        try
        {
            _ = HeatColors.Temp(75);            // guard clamps hot to warm+1 — must not throw
        }
        finally
        {
            App.Settings.TempWarmThresholdC = 60;
            App.Settings.TempHotThresholdC  = 80;
        }
    }
}
