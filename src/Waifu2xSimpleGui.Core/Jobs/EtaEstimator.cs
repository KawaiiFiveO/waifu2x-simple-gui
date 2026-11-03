using System.Globalization;

namespace Waifu2xSimpleGui.Core.Jobs;

/// <summary>
/// Smoothed time-remaining estimate. The progress rate is a time-weighted moving average over about
/// <see cref="Window"/> of active (unpaused) time, so bursts such as a batch of images finishing together
/// don't make the estimate jump. Between reports the estimate counts down instead of freezing.
/// </summary>
public sealed class EtaEstimator
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(30);

    private double _fraction;
    private double _time;
    private double _rate;

    /// <param name="fraction">Overall progress, 0–1.</param>
    /// <param name="active">Active time since the start (excluding pauses).</param>
    public void Report(double fraction, TimeSpan active)
    {
        var t = active.TotalSeconds;
        var dt = t - _time;
        if (fraction <= _fraction || dt <= 0)
        {
            return; // no progress yet: the gap counts as slow time in the next sample
        }

        var instant = (fraction - _fraction) / dt;
        // First sample seeds with the average since the start; later ones blend in by elapsed time.
        _rate = _rate <= 0 ? fraction / t : _rate + ((1 - Math.Exp(-dt / Window.TotalSeconds)) * (instant - _rate));
        _fraction = fraction;
        _time = t;
    }

    /// <summary><c>null</c> until there is enough progress to estimate.</summary>
    public TimeSpan? Remaining(TimeSpan active)
    {
        if (_rate <= 0 || _fraction < 0.01 || active.TotalSeconds < 3)
        {
            return null;
        }

        var left = ((1 - _fraction) / _rate) - (active.TotalSeconds - _time);
        return TimeSpan.FromSeconds(Math.Max(0, left));
    }

    /// <summary>"about 3 min left": coarse rounding keeps the text from flickering.</summary>
    public static string Format(TimeSpan? remaining)
    {
        if (remaining is not { } r)
        {
            return "estimating…";
        }

        var s = r.TotalSeconds;
        var min = (int)Math.Round(s / 60, MidpointRounding.AwayFromZero);
        return s switch
        {
            < 10 => "a few seconds left",
            < 60 => string.Create(CultureInfo.InvariantCulture, $"about {Math.Ceiling(s / 10) * 10:0} s left"),
            _ when min < 60 => string.Create(CultureInfo.InvariantCulture, $"about {min} min left"),
            _ => string.Create(CultureInfo.InvariantCulture, $"about {min / 60} h {min % 60:00} min left"),
        };
    }
}
