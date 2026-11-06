namespace DualSenseConnect.Audio;

/// <summary>
/// Turns the low end of the played sound into vibration for the pad's two haptic actuators:
/// a low-pass filter per channel, then decimation to the pad's 3 kHz signed 8-bit stereo stream.
/// A few multiply-adds per captured sample, no allocations.
/// </summary>
sealed class BassHaptics
{
    public const int FramesPerBlock = 32;               // 32 frames at 3 kHz = one 10.667 ms report slot
    public const int BlockBytes = FramesPerBlock * 2;   // interleaved left/right actuator
    const double CutoffHz = 150;
    const int NoiseGate = 2;                            // keeps the actuators silent on near-silence

    readonly float _b0, _b1, _b2, _a1, _a2;
    readonly float[] _state = new float[8];             // x1, x2, y1, y2 per channel
    volatile float _gain;

    public BassHaptics(int sampleRate, int strengthPercent)
    {
        // RBJ biquad low-pass, Q = 1/sqrt(2).
        double w0 = 2 * Math.PI * CutoffHz / sampleRate;
        double alpha = Math.Sin(w0) / (2 * Math.Sqrt(0.5));
        double cos = Math.Cos(w0);
        double a0 = 1 + alpha;
        _b0 = (float)((1 - cos) / 2 / a0);
        _b1 = (float)((1 - cos) / a0);
        _b2 = _b0;
        _a1 = (float)(-2 * cos / a0);
        _a2 = (float)((1 - alpha) / a0);
        StrengthPercent = strengthPercent;
    }

    /// <summary>0 turns vibration off; 100 is four times the plain bass level.</summary>
    public int StrengthPercent
    {
        set => _gain = Math.Clamp(value, 0, 100) / 100f * 4f * 127f;
    }

    public bool Enabled => _gain > 0;

    /// <summary>
    /// Filters <paramref name="count"/> stereo frames of the ring buffer starting at <paramref name="start"/>
    /// and writes one block of <see cref="FramesPerBlock"/> actuator frames.
    /// </summary>
    public void Process(float[] ring, int start, int count, int capacity, Span<byte> block)
    {
        float gain = _gain;
        if (gain <= 0 || count == 0)
        {
            block.Clear();
            return;
        }

        int nextPick = 0;
        for (int i = 0; i < count; i++)
        {
            int slot = (start + i) % capacity * 2;
            float left = Filter(ring[slot], 0);
            float right = Filter(ring[slot + 1], 4);

            // Pick evenly spaced samples; the low-pass has already removed what would alias.
            while (nextPick < FramesPerBlock && nextPick * count / FramesPerBlock == i)
            {
                block[nextPick * 2] = ToActuator(left * gain);
                block[nextPick * 2 + 1] = ToActuator(right * gain);
                nextPick++;
            }
        }
    }

    float Filter(float x, int s)
    {
        float y = _b0 * x + _b1 * _state[s] + _b2 * _state[s + 1] - _a1 * _state[s + 2] - _a2 * _state[s + 3];
        _state[s + 1] = _state[s];
        _state[s] = x;
        _state[s + 3] = _state[s + 2];
        _state[s + 2] = y;
        return y;
    }

    static byte ToActuator(float value)
    {
        int v = (int)Math.Clamp(MathF.Round(value), -127, 127);
        return (byte)(sbyte)(Math.Abs(v) < NoiseGate ? 0 : v);
    }
}
