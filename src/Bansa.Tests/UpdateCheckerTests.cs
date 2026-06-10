using System;
using Bansa.Services;
using Xunit;

namespace Bansa.Tests;

public class UpdateCheckerTests
{
    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("V1.2", "1.2")]
    [InlineData("1.2.3+abc123", "1.2.3")]
    [InlineData("v2", "2.0")]
    [InlineData("  v1.1.1  ", "1.1.1")]
    public void ParsesTags(string raw, string expected)
        => Assert.Equal(Version.Parse(expected), UpdateChecker.ParseVersion(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]
    [InlineData("v1.2.beta")]
    public void RejectsUnparseable(string? raw)
        => Assert.Null(UpdateChecker.ParseVersion(raw));

    [Fact]
    public void ComparesNewerCorrectly()
        => Assert.True(UpdateChecker.ParseVersion("v1.2.0") > UpdateChecker.ParseVersion("v1.1.9"));
}
