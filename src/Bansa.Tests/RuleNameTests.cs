using Bansa.Services;
using Xunit;

namespace Bansa.Tests;

public class RuleNameTests
{
    [Theory]
    [InlineData(@"C:\Program Files\Google\Chrome\chrome.exe", "Bansa-Throttle-chrome")]
    [InlineData(@"C:\apps\my tool$2.exe", "Bansa-Throttle-mytool2")]
    [InlineData(@"C:\x\node.v18.exe", "Bansa-Throttle-node.v18")]
    [InlineData(@"C:\x\under_score-dash.exe", "Bansa-Throttle-under_score-dash")]
    public void SanitizesToPrefixPlusFilename(string path, string expected)
        => Assert.Equal(expected, DownloadThrottler.MakeRuleName("Bansa-Throttle-", path));

    [Fact]
    public void EmptyFilenameFallsBackToApp()
        => Assert.Equal("Bansa-Throttle-app", DownloadThrottler.MakeRuleName("Bansa-Throttle-", @"C:\folder\"));

    [Fact]
    public void SameFilenameDifferentFoldersCollide()
    {
        // Documented trade-off: rule identity is by filename, so two different exes with the
        // same name collapse to one rule (consistent with QoS policy + settings-clear logic).
        var a = DownloadThrottler.MakeRuleName("Bansa-Throttle-", @"C:\a\node.exe");
        var b = DownloadThrottler.MakeRuleName("Bansa-Throttle-", @"D:\b\node.exe");
        Assert.Equal(a, b);
    }
}
