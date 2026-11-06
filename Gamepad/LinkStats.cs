using System.Diagnostics;

namespace DualSenseConnect.Gamepad;

enum LinkRating { Excellent, Good, Fair, Poor }

/// <summary>
/// Bluetooth link quality measured from the timing of input reports the pad sends anyway.
/// Only collected while the quality window is open.
/// </summary>
sealed class LinkStats
{
    /// <summary>A gap this long between input reports is audible in streamed sound.</summary>
    public const double DropoutMs = 20;

    readonly object _lock = new();
    long _started = Stopwatch.GetTimestamp();
    long _last;
    int _dropouts;
    int _disconnects;
    double _longestGapMs;
    long _secondStarted = Stopwatch.GetTimestamp();
    int _secondReports;
    int _lastSecondReports;

    public readonly record struct Snapshot(
        TimeSpan Elapsed, int ReportsPerSecond, int Dropouts, double LongestGapMs, int Disconnects)
    {
        public LinkRating Rating =>
            Disconnects > 0 || LongestGapMs >= 200 ? LinkRating.Poor
            : LongestGapMs >= 80 || DropoutsPerMinute >= 10 ? LinkRating.Fair
            : LongestGapMs >= DropoutMs * 2 || DropoutsPerMinute >= 1 ? LinkRating.Good
            : LinkRating.Excellent;

        public double DropoutsPerMinute => Elapsed.TotalMinutes > 0 ? Dropouts / Elapsed.TotalMinutes : 0;
    }

    public void Record(long timestamp)
    {
        lock (_lock)
        {
            if (_last != 0)
            {
                double gapMs = (timestamp - _last) * 1000.0 / Stopwatch.Frequency;
                if (gapMs > DropoutMs)
                    _dropouts++;
                if (gapMs > _longestGapMs)
                    _longestGapMs = gapMs;
            }
            _last = timestamp;
            _secondReports++;
            if (timestamp - _secondStarted >= Stopwatch.Frequency)
            {
                _lastSecondReports = _secondReports;
                _secondReports = 0;
                _secondStarted = timestamp;
            }
        }
    }

    public void Disconnected()
    {
        lock (_lock)
        {
            _disconnects++;
            _last = 0;   // the time without a connection is not a dropout
        }
    }

    public Snapshot Read()
    {
        lock (_lock)
        {
            // A stalled link stops producing reports, so count the current silence as well.
            double currentGapMs = _last == 0 ? 0 : (Stopwatch.GetTimestamp() - _last) * 1000.0 / Stopwatch.Frequency;
            return new Snapshot(Stopwatch.GetElapsedTime(_started),
                currentGapMs > 1000 ? 0 : _lastSecondReports,
                _dropouts,
                Math.Max(_longestGapMs, currentGapMs > 1000 ? 0 : currentGapMs),
                _disconnects);
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _started = _secondStarted = Stopwatch.GetTimestamp();
            _last = 0;
            _dropouts = _disconnects = _secondReports = _lastSecondReports = 0;
            _longestGapMs = 0;
        }
    }
}
