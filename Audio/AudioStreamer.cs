using System.Diagnostics;
using System.Runtime.InteropServices;
using Concentus;
using Concentus.Enums;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using DualSenseConnect.Core;
using DualSenseConnect.Gamepad;

namespace DualSenseConnect.Audio;

/// <summary>
/// Captures what Windows plays on an output device (WASAPI loopback) and streams it to the
/// DualSense over Bluetooth: as Opus audio, as bass-driven vibration, or both.
/// Goes idle while the device is silent.
/// </summary>
sealed class AudioStreamer : IDisposable
{
    // The pad consumes one 480-sample Opus frame per report slot of 32/3000 s (10.667 ms),
    // so 512 source samples at 48 kHz are squeezed into each frame.
    const double ReportPeriodSeconds = 32.0 / 3000.0;
    const int FrameSamples = 480;
    const int Channels = 2;
    const double IdleAfterSeconds = 2.0;
    const double MaxBufferedSeconds = 0.12;
    const double TargetBufferedSeconds = 0.04;

    readonly DualSense _pad;
    readonly WasapiRecorder _capture;
    readonly IOpusEncoder _encoder;
    readonly Thread _thread;
    readonly CancellationTokenSource _cts = new();
    readonly AutoResetEvent _dataArrived = new(false);
    readonly object _bufferLock = new();

    readonly int _sourceRate;
    readonly int _sourceChannels;
    readonly double _sourceStep;      // source frames advanced per output sample
    readonly float[] _ring;           // interleaved stereo at the source rate
    int _ringStart;
    int _ringCount;                   // in stereo frames
    double _phase;                    // fractional read position within the ring
    long _lastSoundTicks;

    readonly BassHaptics _haptics;
    readonly HapticPulses _pulses;

    volatile bool _audio;
    volatile bool _jack;
    volatile int _volume;

    public AudioStreamer(DualSense pad, HapticPulses pulses, string? deviceId, bool audio, bool jack,
        int volumePercent, int hapticsPercent)
    {
        _pad = pad;
        _pulses = pulses;
        _audio = audio;
        _jack = jack;
        VolumePercent = volumePercent;

        using var enumerator = new MMDeviceEnumerator();
        var device = deviceId != null && TryGetDevice(enumerator, deviceId) is { } chosen
            ? chosen
            : enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        _capture = new WasapiRecorderBuilder().WithDevice(device).WithLoopbackCapture().Build();

        var format = _capture.WaveFormat;
        if (format.Encoding != WaveFormatEncoding.IeeeFloat && format.Encoding != WaveFormatEncoding.Extensible
            || format.BitsPerSample != 32)
            throw new NotSupportedException($"Unsupported capture format: {format}");
        _sourceRate = format.SampleRate;
        _sourceChannels = format.Channels;
        _sourceStep = _sourceRate * ReportPeriodSeconds / FrameSamples;
        _ring = new float[(int)(_sourceRate * MaxBufferedSeconds * 2) * Channels];
        Log.Write($"Audio source: {device.FriendlyName}, {format.SampleRate} Hz, {format.Channels} ch");

        _encoder = OpusCodecFactory.CreateEncoder(48000, Channels, OpusApplication.OPUS_APPLICATION_RESTRICTED_LOWDELAY);
        _encoder.Bitrate = DualSense.OpusFrameBytes * 8 * 100;
        _encoder.UseVBR = false;
        _encoder.Complexity = 0;
        _haptics = new BassHaptics(_sourceRate, hapticsPercent);

        _capture.DataAvailable += OnData;
        _thread = new Thread(Run) { IsBackground = true, Name = "AudioStreamer", Priority = ThreadPriority.AboveNormal };
    }

    /// <summary>Sound to the pad; when off, only vibration is sent (if enabled).</summary>
    public bool Audio
    {
        set => _audio = value;
    }

    /// <summary>Vibration strength, 0..100; 0 turns it off.</summary>
    public int HapticsPercent
    {
        set => _haptics.StrengthPercent = value;
    }

    /// <summary>Switches between the headphone jack and the pad speaker on the fly.</summary>
    public bool Jack
    {
        get => _jack;
        set => _jack = value;
    }

    /// <summary>Pad-side volume, 0..100; applied with the next route setup.</summary>
    public int VolumePercent
    {
        set => _volume = Math.Clamp(value, 0, 100) * 0x7F / 100;
    }

    public void Start()
    {
        _capture.StartRecording();
        _thread.Start();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _dataArrived.Set();
        _thread.Join(2000);
        _capture.DataAvailable -= OnData;
        _capture.StopRecording();
        _capture.Dispose();
    }

    public static IReadOnlyList<(string Id, string Name)> ListOutputDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Select(d => (d.ID, d.FriendlyName))
            .ToList();
    }

    static MMDevice? TryGetDevice(MMDeviceEnumerator enumerator, string id)
    {
        try
        {
            var device = enumerator.GetDevice(id);
            return device.State == DeviceState.Active ? device : null;
        }
        catch (COMException)
        {
            return null;
        }
    }

    /// <summary>Capture thread: keeps the first two channels, drops the oldest audio when the buffer overflows.</summary>
    void OnData(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        if ((flags & AudioClientBufferFlags.Silent) != 0)
            return;
        var samples = MemoryMarshal.Cast<byte, float>(buffer);
        int frames = samples.Length / _sourceChannels;
        if (frames == 0)
            return;

        bool sound = false;
        int capacity = _ring.Length / Channels;
        lock (_bufferLock)
        {
            for (int i = 0; i < frames; i++)
            {
                float left = samples[i * _sourceChannels];
                float right = _sourceChannels > 1 ? samples[i * _sourceChannels + 1] : left;
                sound |= left != 0 || right != 0;

                if (_ringCount == capacity)
                {
                    _ringStart = (_ringStart + 1) % capacity;
                    _ringCount--;
                }
                int slot = (_ringStart + _ringCount) % capacity * Channels;
                _ring[slot] = left;
                _ring[slot + 1] = right;
                _ringCount++;
            }
        }

        if (sound)
        {
            Volatile.Write(ref _lastSoundTicks, Stopwatch.GetTimestamp());
            _dataArrived.Set();
        }
    }

    void Run()
    {
        TimeBeginPeriod(1);
        try
        {
            var pcm = new float[FrameSamples * Channels];
            var first = new byte[DualSense.OpusFrameBytes];
            var second = new byte[DualSense.OpusFrameBytes];
            var firstHaptics = new byte[BassHaptics.BlockBytes];
            var secondHaptics = new byte[BassHaptics.BlockBytes];
            (bool Jack, int Volume)? sentSetup = null;

            while (!_cts.IsCancellationRequested)
            {
                // Idle: no reports and no encoding until something is actually played (or a pulse is queued).
                if (!Busy())
                {
                    _dataArrived.WaitOne();
                    sentSetup = null;
                    continue;
                }

                bool audio = _audio;
                bool jack = _jack;
                int volume = _volume;
                if (audio && sentSetup != (jack, volume))
                {
                    _pad.SendAudioSetup(jack, (byte)volume);
                    if (sentSetup?.Jack != jack)
                        PrimeBuffer();
                    sentSetup = (jack, volume);
                }
                if (!audio)
                {
                    sentSetup = null;
                    if (!_haptics.Enabled && !_pulses.Pending)
                    {
                        _dataArrived.WaitOne(1000);   // nothing to send until settings change
                        continue;
                    }
                }

                // Audio: two 10 ms frames per packet; vibration only: one block per packet.
                double periodTicks = (audio ? 2 : 1) * ReportPeriodSeconds * Stopwatch.Frequency;
                long next = Stopwatch.GetTimestamp();
                bool hapticsMoving = true;
                while (!_cts.IsCancellationRequested && _audio == audio && _jack == jack && _volume == volume
                       && (audio || _haptics.Enabled || _pulses.Pending) && Busy())
                {
                    if (audio)
                    {
                        EncodeNext(pcm, first, firstHaptics);
                        EncodeNext(pcm, second, secondHaptics);
                        _pad.WriteAudioFrames(first, second, firstHaptics, secondHaptics, jack);
                    }
                    else
                    {
                        // Only vibration goes out: a block without bass is not worth a radio packet
                        // (saves the pad's battery), except one to settle the actuators after a burst.
                        Resample(null, firstHaptics);
                        bool still = !firstHaptics.AsSpan().ContainsAnyExcept((byte)0);
                        if (!still || hapticsMoving)
                            _pad.WriteHaptics(firstHaptics);
                        hapticsMoving = !still;
                    }

                    next += (long)periodTicks;
                    long wait = next - Stopwatch.GetTimestamp();
                    if (wait > 0)
                        Thread.Sleep(TimeSpan.FromTicks(wait * TimeSpan.TicksPerSecond / Stopwatch.Frequency));
                    else if (-wait > periodTicks * 5)
                        next = Stopwatch.GetTimestamp();   // fell far behind (e.g. system stall): don't burst
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Audio streaming stopped: {ex}");
        }
        finally
        {
            TimeEndPeriod(1);
        }
    }

    bool Busy() => _pulses.Pending
                   || Stopwatch.GetElapsedTime(Volatile.Read(ref _lastSoundTicks)).TotalSeconds <= IdleAfterSeconds;

    /// <summary>Wakes the idle streamer so queued pulses go out.</summary>
    public void Wake() => _dataArrived.Set();

    void EncodeNext(float[] pcm, byte[] opus, byte[] haptics)
    {
        Resample(pcm, haptics);
        int length = _encoder.Encode(pcm, FrameSamples, opus, opus.Length);
        if (length < opus.Length)
            Array.Clear(opus, length, opus.Length - length);
    }

    /// <summary>Drops stale audio so playback starts with a small, fixed latency.</summary>
    void PrimeBuffer()
    {
        lock (_bufferLock)
        {
            int keep = (int)(_sourceRate * TargetBufferedSeconds);
            if (_ringCount > keep)
            {
                int capacity = _ring.Length / Channels;
                _ringStart = (_ringStart + _ringCount - keep) % capacity;
                _ringCount = keep;
            }
            _phase = 0;
        }
    }

    /// <summary>
    /// Consumes one report slot of source audio: linear-interpolates it into <paramref name="output"/>
    /// (skipped when null) and derives the vibration block. Missing input becomes silence, excess is trimmed.
    /// </summary>
    void Resample(float[]? output, byte[] haptics)
    {
        lock (_bufferLock)
        {
            int capacity = _ring.Length / Channels;
            int maxKeep = (int)(_sourceRate * TargetBufferedSeconds * 2);
            if (_ringCount > maxKeep)
            {
                // Capture clock runs faster than the pad: skip ahead instead of drifting into latency.
                int drop = _ringCount - (int)(_sourceRate * TargetBufferedSeconds);
                _ringStart = (_ringStart + drop) % capacity;
                _ringCount -= drop;
            }

            if (output == null)
            {
                _phase += _sourceStep * FrameSamples;
            }
            else
            {
                for (int i = 0; i < FrameSamples; i++)
                {
                    int index = (int)_phase;
                    float frac = (float)(_phase - index);
                    for (int ch = 0; ch < Channels; ch++)
                    {
                        float a = index < _ringCount ? _ring[(_ringStart + index) % capacity * Channels + ch] : 0f;
                        float b = index + 1 < _ringCount ? _ring[(_ringStart + index + 1) % capacity * Channels + ch] : a;
                        output[i * Channels + ch] = a + (b - a) * frac;
                    }
                    _phase += _sourceStep;
                }
            }

            int consumed = Math.Min((int)_phase, _ringCount);
            _haptics.Process(_ring, _ringStart, consumed, capacity, haptics);
            _pulses.Mix(haptics);
            _ringStart = (_ringStart + consumed) % capacity;
            _ringCount -= consumed;
            _phase -= (int)_phase;
        }
    }

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    static extern uint TimeBeginPeriod(uint milliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    static extern uint TimeEndPeriod(uint milliseconds);
}
