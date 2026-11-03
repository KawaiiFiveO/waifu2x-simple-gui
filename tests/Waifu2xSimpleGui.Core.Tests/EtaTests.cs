using Waifu2xSimpleGui.Core.Jobs;

namespace Waifu2xSimpleGui.Core.Tests;

public sealed class EtaTests
{
    private static TimeSpan S(double s) => TimeSpan.FromSeconds(s);

    [Fact]
    public void SteadyRateGivesExactEstimate()
    {
        var eta = new EtaEstimator();
        for (var t = 1; t <= 50; t++)
        {
            eta.Report(t / 100.0, S(t)); // 1 %/s
        }

        Assert.Equal(50, eta.Remaining(S(50))!.Value.TotalSeconds, 1);
    }

    [Fact]
    public void BurstyProgressStaysSmooth()
    {
        // 10 % lands every 10 s in one burst (a batch of images finishing together); the true rate is 1 %/s.
        var eta = new EtaEstimator();
        var estimates = new List<double>();
        for (var t = 0.1; t < 60; t += 0.1)
        {
            eta.Report(Math.Floor(t / 10) / 10, S(t));
            if (t > 15 && eta.Remaining(S(t)) is { } r)
            {
                estimates.Add(r.TotalSeconds + t); // predicted finish time; ideal = 100 s
            }
        }

        Assert.All(estimates, e => Assert.InRange(e, 80, 125));
    }

    [Fact]
    public void CountsDownBetweenReports()
    {
        var eta = new EtaEstimator();
        eta.Report(0.5, S(50));
        Assert.Equal(40, eta.Remaining(S(60))!.Value.TotalSeconds, 1);
        Assert.Equal(TimeSpan.Zero, eta.Remaining(S(200)));
    }

    [Fact]
    public void NoEstimateBeforeProgress()
    {
        var eta = new EtaEstimator();
        eta.Report(0, S(10));
        Assert.Null(eta.Remaining(S(10)));
        Assert.Equal("estimating…", EtaEstimator.Format(null));
    }

    [Theory]
    [InlineData(4, "a few seconds left")]
    [InlineData(41, "about 50 s left")]
    [InlineData(150, "about 3 min left")]
    [InlineData(3599, "about 1 h 00 min left")]
    [InlineData(7500, "about 2 h 05 min left")]
    public void FormatIsCoarse(double seconds, string expected) =>
        Assert.Equal(expected, EtaEstimator.Format(S(seconds)));
}
