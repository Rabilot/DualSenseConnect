using System.Diagnostics;
using System.Runtime.InteropServices;
using DualSenseConnect.Audio;

namespace DualSenseConnect.Gamepad;

/// <summary>
/// Short confirmation buzzes felt in the hands (mute on/off, volume step). Queued here and either
/// mixed into the running haptics stream or, when nothing is streaming, sent on their own.
/// </summary>
sealed class HapticPulses
{
    const int SampleRate = 3000;   // the pad's haptics stream: 3 kHz, signed 8-bit, stereo
    const double PeriodSeconds = 32.0 / 3000.0;

    readonly object _lock = new();
    readonly Queue<sbyte> _samples = new();
    int _playing;

    public bool Pending
    {
        get
        {
            lock (_lock)
                return _samples.Count > 0;
        }
    }

    /// <summary>One buzz: the actuators feel 150–250 Hz best.</summary>
    public void Buzz(int milliseconds, int amplitude = 110, int hz = 180)
    {
        lock (_lock)
        {
            int count = milliseconds * SampleRate / 1000;
            for (int i = 0; i < count; i++)
                _samples.Enqueue((sbyte)(amplitude * Math.Sin(2 * Math.PI * hz * i / SampleRate)));
        }
    }

    public void Clear()
    {
        lock (_lock)
            _samples.Clear();
    }

    public void Pause(int milliseconds)
    {
        lock (_lock)
        {
            for (int i = milliseconds * SampleRate / 1000; i > 0; i--)
                _samples.Enqueue(0);
        }
    }

    /// <summary>Adds the next queued samples to both actuators of an interleaved stereo block.</summary>
    public void Mix(Span<byte> block)
    {
        lock (_lock)
        {
            for (int i = 0; i + 1 < block.Length && _samples.Count > 0; i += 2)
            {
                int pulse = _samples.Dequeue();
                block[i] = (byte)(sbyte)Math.Clamp((sbyte)block[i] + pulse, -127, 127);
                block[i + 1] = (byte)(sbyte)Math.Clamp((sbyte)block[i + 1] + pulse, -127, 127);
            }
        }
    }

    /// <summary>Sends the queue as haptics-only packets; for when no audio streamer is running.</summary>
    public void PlayAlone(DualSense pad)
    {
        if (Interlocked.Exchange(ref _playing, 1) == 1)
            return;   // already playing: new pulses join the queue being sent
        ThreadPool.QueueUserWorkItem(_ =>
        {
            var block = new byte[BassHaptics.BlockBytes];
            long period = (long)(PeriodSeconds * Stopwatch.Frequency);
            TimeBeginPeriod(1);
            try
            {
                long next = Stopwatch.GetTimestamp();
                while (Pending && pad.IsConnected)
                {
                    Array.Clear(block);
                    Mix(block);
                    pad.WriteHaptics(block);
                    next += period;
                    long wait = next - Stopwatch.GetTimestamp();
                    if (wait > 0)
                        Thread.Sleep(TimeSpan.FromTicks(wait * TimeSpan.TicksPerSecond / Stopwatch.Frequency));
                }
                Array.Clear(block);
                pad.WriteHaptics(block);   // settle the actuators
            }
            finally
            {
                TimeEndPeriod(1);
                Volatile.Write(ref _playing, 0);
            }
        });
    }

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    static extern uint TimeBeginPeriod(uint milliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    static extern uint TimeEndPeriod(uint milliseconds);
}
