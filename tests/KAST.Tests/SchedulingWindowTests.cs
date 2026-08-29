using KAST.UI.Services;

namespace KAST.Tests;

public class SchedulingWindowTests
{
    [Theory]
    // Simple forward window: (04:00, 04:01]
    [InlineData("04:00:30", "04:00:00", "04:01:00", true)]
    [InlineData("04:01:00", "04:00:00", "04:01:00", true)]  // boundary end inclusive
    [InlineData("04:00:00", "04:00:00", "04:01:00", false)] // boundary start exclusive
    [InlineData("04:02:00", "04:00:00", "04:01:00", false)]
    [InlineData("03:59:59", "04:00:00", "04:01:00", false)]
    // Drifted window: a slow tick covers several minutes and must not skip
    [InlineData("04:00:00", "03:58:00", "04:03:00", true)]
    // Midnight wrap: (23:59, 00:01]
    [InlineData("00:00:00", "23:59:00", "00:01:00", true)]
    [InlineData("23:59:30", "23:59:00", "00:01:00", true)]
    [InlineData("00:01:00", "23:59:00", "00:01:00", true)]
    [InlineData("12:00:00", "23:59:00", "00:01:00", false)]
    public void ShouldFire_EvaluatesWindowCorrectly(string scheduled, string windowStart, string windowEnd, bool expected)
    {
        var result = SchedulingBackgroundService.ShouldFire(
            TimeOnly.Parse(scheduled),
            TimeOnly.Parse(windowStart),
            TimeOnly.Parse(windowEnd));

        Assert.Equal(expected, result);
    }

    [Fact]
    public void ShouldFire_EmptyWindow_NeverFires()
    {
        var t = TimeOnly.Parse("04:00:00");
        Assert.False(SchedulingBackgroundService.ShouldFire(t, t, t));
    }
}
