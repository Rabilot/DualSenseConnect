using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using HidSharp;
using DualSenseConnect.Audio;
using DualSenseConnect.Core;

namespace DualSenseConnect.Gamepad;

/// <summary>
/// Watches for a DualSense over USB or Bluetooth, reports mute button presses
/// and drives the orange LED under the button. Reading is shared with other apps (Steam etc.).
/// </summary>
sealed class DualSense : IDisposable
{
    const int SonyVid = 0x054C;
    static readonly int[] Pids = { 0x0CE6, 0x0DF2 }; // DualSense, DualSense Edge

    const byte MuteButtonBit = 0x04;
    const int DpadDelta = -2;           // low nibble of buttons[0]: D-pad hat, relative to the buttons byte
    static readonly TimeSpan LongPress = TimeSpan.FromMilliseconds(800);
    const int UsbButtonsOffset = 10;    // report 0x01
    const int BtButtonsOffset = 11;     // report 0x31
    const int StatusDelta = 43;         // battery/charging byte, relative to the buttons byte
    const int HeadphoneDelta = 44;      // bit 0: headphones plugged into the jack
    const int UsbOutputLength = 48;     // report 0x02
    const int BtOutputLength = 78;      // report 0x31
    const int BtAudioLength = 547;      // report 0x39: two Opus frames plus two haptics blocks
    const int BtHapticsLength = 142;    // report 0x32: one haptics block, no audio
    const int AudioOpusOffset = 12;

    public const int OpusFrameBytes = 200;

    readonly CancellationTokenSource _cts = new();
    readonly AutoResetEvent _devicesChanged = new(false);
    readonly Thread _thread;
    readonly object _writeLock = new();
    HidStream? _stream;
    bool _bluetooth;
    int _maxOutputLength;
    int _featureLength;
    byte _btSequence;
    byte[] _audioReport = [];
    byte _audioSequence;
    byte _audioCounter;

    /// <summary>Mute pressed and released quickly without the D-pad.</summary>
    public event Action? MuteShortPress;

    /// <summary>Mute held for <see cref="LongPress"/> without the D-pad.</summary>
    public event Action? MuteLongPress;

    /// <summary>D-pad up (+1) or down (-1) pressed while mute is held.</summary>
    public event Action<int>? VolumeStep;

    public event Action<bool>? ConnectionChanged;

    /// <summary>Raised only when the raw battery status byte changes, which happens rarely.</summary>
    public event Action<byte>? BatteryStatusChanged;

    public event Action<bool>? HeadphonesChanged;

    /// <summary>
    /// How much audio the pad buffers before playing (stream-config byte of report 0x39). Larger
    /// rides out link gaps at the cost of delay.
    /// </summary>
    public byte SpeakerBufferDepth { get; set; } = DefaultBufferDepth;

    public const byte DefaultBufferDepth = 0x3C;

    /// <summary>Gaps longer than <see cref="LinkStats.DropoutMs"/> between input reports since the last call.</summary>
    public int TakeGapCount() => Interlocked.Exchange(ref _gaps, 0);
    int _gaps;

    /// <summary>Set while the connection-quality window is open; null costs nothing.</summary>
    public LinkStats? Stats { get; set; }

    public bool IsConnected => _stream != null;

    /// <summary>Audio has to be streamed only over Bluetooth; over USB Windows sees the pad as a sound card.</summary>
    public bool IsBluetooth => _bluetooth;

    public DualSense()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "DualSense" };
    }

    public void Start() => _thread.Start();

    public void Dispose()
    {
        _cts.Cancel();
        _thread.Join(3000);
    }

    void Run()
    {
        // While no pad is connected the thread sleeps until Windows reports a device change.
        DeviceList.Local.Changed += (_, _) => _devicesChanged.Set();
        var waitHandles = new[] { _cts.Token.WaitHandle, _devicesChanged };

        int retries = 0;
        while (!_cts.IsCancellationRequested)
        {
            _devicesChanged.Reset();
            var device = DeviceList.Local.GetHidDevices(SonyVid).FirstOrDefault(d => Pids.Contains(d.ProductID));
            if (device == null || !device.TryOpen(out HidStream stream))
            {
                // The pad's HID interface can show up a moment after the change event,
                // so a few quick retries follow each event before sleeping again.
                if (retries > 0)
                {
                    retries--;
                    _cts.Token.WaitHandle.WaitOne(1000);
                }
                else if (WaitHandle.WaitAny(waitHandles) == 1)
                {
                    retries = 5;
                }
                continue;
            }
            retries = 0;

            using (stream)
            {
                try
                {
                    Open(device, stream);
                    ConnectionChanged?.Invoke(true);
                    ReadLoop(stream);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or UnauthorizedAccessException)
                {
                    Log.Write($"Connection lost: {ex.Message}");
                }
                finally
                {
                    Stats?.Disconnected();
                    lock (_writeLock)
                        _stream = null;
                    ConnectionChanged?.Invoke(false);
                }
            }
            _cts.Token.WaitHandle.WaitOne(1000);
        }
    }

    void Open(HidDevice device, HidStream stream)
    {
        _bluetooth = device.GetMaxInputReportLength() != 64;
        _maxOutputLength = device.GetMaxOutputReportLength();
        Log.Write($"Connected {device.ProductID:X4} via {(_bluetooth ? "Bluetooth" : "USB")}, " +
                  $"in={device.GetMaxInputReportLength()} out={_maxOutputLength} feature={device.GetMaxFeatureReportLength()}");

        _featureLength = device.GetMaxFeatureReportLength();
        if (_bluetooth)
            RequestFullReports(stream);

        stream.ReadTimeout = 1000;
        stream.WriteTimeout = 200;
        lock (_writeLock)
        {
            _stream = stream;
            _audioReport = new byte[Math.Max(_maxOutputLength, BtAudioLength)];
        }
    }

    /// <summary>
    /// Over Bluetooth the pad sends a reduced report without the mute bit until
    /// the calibration feature report is read; reading it switches to full reports.
    /// </summary>
    bool RequestFullReports(HidStream stream)
    {
        try
        {
            var feature = new byte[_featureLength];
            feature[0] = 0x05;
            stream.GetFeature(feature);
            return true;
        }
        catch (IOException ex)
        {
            Log.Write($"Could not switch to full reports: {ex.Message}");
            return false;
        }
    }

    void ReadLoop(HidStream stream)
    {
        long lastFullReportRequest = Environment.TickCount64;
        var buffer = new byte[4096];
        bool muteHeld = false, muteCombo = false, muteLongFired = false;
        long muteDownAt = 0;
        long lastReportAt = 0;
        long gapTicks = (long)(LinkStats.DropoutMs / 1000 * Stopwatch.Frequency);
        int lastDpad = 0;
        bool loggedReduced = false;
        int lastStatus = -1;
        int lastHeadphones = -1;

        while (!_cts.IsCancellationRequested)
        {
            int length;
            try
            {
                length = stream.Read(buffer);
            }
            catch (TimeoutException)
            {
                continue;
            }

            // Mic audio arrives under the same report id as gamepad input. We never request it (Steam reads
            // it as stick input), but if another app does, skip it rather than read it as buttons.
            if (_bluetooth && buffer[0] == 0x31 && (buffer[1] & 0x0F) == 2)
                continue;

            int offset = (buffer[0], _bluetooth) switch
            {
                (0x01, false) => UsbButtonsOffset,
                (0x31, true) => BtButtonsOffset,
                _ => -1,
            };
            if (offset < 0 || length <= offset)
            {
                if (!loggedReduced)
                    Log.Write($"Ignoring report 0x{buffer[0]:X2} ({length} bytes)");
                loggedReduced = true;
                // Still in reduced mode: retry the switch, at most once a second.
                if (_bluetooth && buffer[0] == 0x01 && Environment.TickCount64 - lastFullReportRequest > 1000)
                {
                    lastFullReportRequest = Environment.TickCount64;
                    if (RequestFullReports(stream))
                        Log.Write("Switched to full reports");
                }
                continue;
            }

            long now = Stopwatch.GetTimestamp();
            Stats?.Record(now);
            if (lastReportAt != 0 && now - lastReportAt > gapTicks)
                Interlocked.Increment(ref _gaps);
            lastReportAt = now;

            bool down = (buffer[offset] & MuteButtonBit) != 0;
            int dpad = DpadDirection(buffer[offset + DpadDelta] & 0x0F);
            if (down && !muteHeld)
            {
                muteHeld = true;
                muteDownAt = Stopwatch.GetTimestamp();
                muteCombo = muteLongFired = false;
            }
            else if (down)
            {
                // Mute + D-pad up/down: one volume step per press.
                if (dpad != 0 && dpad != lastDpad)
                {
                    muteCombo = true;
                    VolumeStep?.Invoke(dpad);
                }
                if (!muteCombo && !muteLongFired && Stopwatch.GetElapsedTime(muteDownAt) >= LongPress)
                {
                    muteLongFired = true;   // fires while still held, so the action is felt at once
                    MuteLongPress?.Invoke();
                }
            }
            else if (muteHeld)
            {
                muteHeld = false;
                if (!muteCombo && !muteLongFired)
                    MuteShortPress?.Invoke();
            }
            lastDpad = dpad;

            // The battery level comes in the same report, so no extra requests are needed.
            int statusOffset = offset + StatusDelta;
            if (length > statusOffset && buffer[statusOffset] != lastStatus)
            {
                lastStatus = buffer[statusOffset];
                BatteryStatusChanged?.Invoke((byte)lastStatus);
            }

            int headphoneOffset = offset + HeadphoneDelta;
            if (length > headphoneOffset && (buffer[headphoneOffset] & 0x01) != lastHeadphones)
            {
                lastHeadphones = buffer[headphoneOffset] & 0x01;
                HeadphonesChanged?.Invoke(lastHeadphones == 1);
            }
        }
    }

    /// <summary>D-pad hat value to +1 (up, incl. diagonals), -1 (down) or 0.</summary>
    static int DpadDirection(int hat) => hat switch
    {
        0 or 1 or 7 => 1,
        3 or 4 or 5 => -1,
        _ => 0,
    };

    /// <summary>Decodes the status byte into a percentage and charging state; null percent means an error.</summary>
    public static (int? Percent, bool Charging) DecodeBattery(byte status)
    {
        int level = status & 0x0F;
        return (status >> 4) switch
        {
            0x0 => (Math.Min(level * 10 + 5, 100), false),
            0x1 => (Math.Min(level * 10 + 5, 100), true),
            0x2 => (100, false),
            _ => (null, false),
        };
    }

    /// <summary>Turns the LED under the mute button on or off without touching lightbar or rumble.</summary>
    public void SetMuteLed(bool on)
    {
        lock (_writeLock)
        {
            if (_stream == null)
                return;

            byte[] report;
            if (_bluetooth)
            {
                report = new byte[BtOutputLength];
                report[0] = 0x31;
                report[1] = (byte)(_btSequence << 4);
                report[2] = 0x10;
                _btSequence = (byte)((_btSequence + 1) & 0x0F);
                FillCommon(report.AsSpan(3), on);
                BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(BtOutputLength - 4),
                    Crc32(0xA2, report.AsSpan(0, BtOutputLength - 4)));
            }
            else
            {
                report = new byte[UsbOutputLength];
                report[0] = 0x02;
                FillCommon(report.AsSpan(1), on);
            }

            if (_maxOutputLength > report.Length)
                Array.Resize(ref report, _maxOutputLength);

            try
            {
                _stream.Write(report);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException)
            {
                Log.Write($"LED write failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Turns the pad off by dropping its Bluetooth link, like holding PS for 10 s.
    /// The MAC comes from the pairing-info feature report 0x09.
    /// </summary>
    public bool PowerOff()
    {
        ulong address = 0;
        lock (_writeLock)
        {
            if (_stream == null || !_bluetooth)
                return false;
            try
            {
                var feature = new byte[_featureLength];
                feature[0] = 0x09;
                _stream.GetFeature(feature);
                for (int i = 0; i < 6; i++)
                    address |= (ulong)feature[1 + i] << (8 * i);
            }
            catch (IOException ex)
            {
                Log.Write($"Reading pad address failed: {ex.Message}");
                return false;
            }
        }

        var findParams = new BluetoothFindRadioParams { Size = (uint)Marshal.SizeOf<BluetoothFindRadioParams>() };
        IntPtr find = BluetoothFindFirstRadio(ref findParams, out IntPtr radio);
        if (find == IntPtr.Zero)
        {
            Log.Write($"No Bluetooth radio found (error {Marshal.GetLastWin32Error()})");
            return false;
        }
        try
        {
            bool ok = DeviceIoControl(radio, IoctlBthDisconnectDevice, ref address, sizeof(ulong),
                IntPtr.Zero, 0, out _, IntPtr.Zero);
            Log.Write(ok ? $"Pad {address:X12} turned off" : $"Turning pad off failed (error {Marshal.GetLastWin32Error()})");
            return ok;
        }
        finally
        {
            CloseHandle(radio);
            BluetoothFindRadioClose(find);
        }
    }

    const uint IoctlBthDisconnectDevice = 0x41000C;

    [StructLayout(LayoutKind.Sequential)]
    struct BluetoothFindRadioParams
    {
        public uint Size;
    }

    [DllImport("BluetoothApis.dll", SetLastError = true)]
    static extern IntPtr BluetoothFindFirstRadio(ref BluetoothFindRadioParams findParams, out IntPtr radio);

    [DllImport("BluetoothApis.dll")]
    static extern bool BluetoothFindRadioClose(IntPtr find);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(IntPtr device, uint code, ref ulong input, int inputSize,
        IntPtr output, int outputSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    /// <summary>
    /// Routes pad audio to the headphone jack or the built-in speaker and sets its volume (0..127).
    /// Bluetooth only; must be sent before streaming and whenever the route changes.
    /// </summary>
    public void SendAudioSetup(bool jack, byte volume)
    {
        lock (_writeLock)
        {
            if (_stream == null || !_bluetooth)
                return;

            var report = new byte[Math.Max(_maxOutputLength, BtOutputLength)];
            report[0] = 0x31;
            report[1] = (byte)(_btSequence << 4);
            report[2] = 0x10;
            _btSequence = (byte)((_btSequence + 1) & 0x0F);
            var common = report.AsSpan(3);
            common[0] = 0xB0;                          // valid_flag0: audio control, speaker and headphone volume
            common[1] = 0x80;                          // valid_flag1: audio control 2
            common[4] = jack ? volume : (byte)0;       // headphone_volume
            common[5] = jack ? (byte)0 : volume;       // speaker_volume
            common[7] = jack ? (byte)0x00 : (byte)0x30; // audio_control: output path
            common[37] = 0x02;                         // audio_control2: speaker preamp gain
            BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(BtOutputLength - 4),
                Crc32(0xA2, report.AsSpan(0, BtOutputLength - 4)));
            TryWrite(report, "Audio setup");
        }
    }

    /// <summary>Sends one haptics block without audio (report 0x32). Reuses a buffer, no allocations.</summary>
    public void WriteHaptics(ReadOnlySpan<byte> block)
    {
        lock (_writeLock)
        {
            if (_stream == null || !_bluetooth)
                return;

            var report = _audioReport;
            Array.Clear(report);
            report[0] = 0x32;
            report[1] = (byte)(_audioSequence << 4);
            _audioSequence = (byte)((_audioSequence + 1) & 0x0F);
            report[2] = 0x91;                 // stream config sub-packet
            report[3] = 7;
            report[4] = 0xFE;                 // bit 0 clear: no mic capture
            report[9] = 0x20;                 // buffer depth
            report[10] = _audioCounter++;
            report[11] = 0x92;                // haptics sub-packet
            report[12] = BassHaptics.BlockBytes;
            block.CopyTo(report.AsSpan(13));
            BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(BtHapticsLength - 4),
                Crc32(0xA2, report.AsSpan(0, BtHapticsLength - 4)));
            TryWrite(report, "Haptics");
        }
    }

    /// <summary>
    /// Sends two consecutive Opus frames (exactly <see cref="OpusFrameBytes"/> bytes each) in one packet.
    /// Half as many radio packets as one frame per report, which survives distance much better.
    /// Reuses a buffer, no allocations.
    /// </summary>
    public void WriteAudioFrames(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second,
        ReadOnlySpan<byte> firstHaptics, ReadOnlySpan<byte> secondHaptics, bool jack)
    {
        lock (_writeLock)
        {
            if (_stream == null || !_bluetooth)
                return;

            var report = _audioReport;
            Array.Clear(report);
            report[0] = 0x39;
            report[1] = (byte)(_audioSequence << 4);
            _audioSequence = (byte)((_audioSequence + 1) & 0x0F);
            report[2] = 0x91;                 // stream config sub-packet
            report[3] = 6;
            report[4] = 0x7E;                 // bit 0 clear: no mic capture
            report[5] = 0x51;
            report[6] = 0x1F;
            report[7] = 0x62;
            report[8] = SpeakerBufferDepth;
            report[9] = _audioCounter;
            _audioCounter += 2;
            // Sub-packet ids with bit 6 set carry two payloads of the given length.
            report[AudioOpusOffset - 2] = jack ? (byte)0xD6 : (byte)0xD5;
            report[AudioOpusOffset - 1] = OpusFrameBytes;
            first.CopyTo(report.AsSpan(AudioOpusOffset, OpusFrameBytes));
            second.CopyTo(report.AsSpan(AudioOpusOffset + OpusFrameBytes, OpusFrameBytes));
            int haptics = AudioOpusOffset + 2 * OpusFrameBytes;
            report[haptics] = 0xD2;           // two haptics blocks
            report[haptics + 1] = BassHaptics.BlockBytes;
            firstHaptics.CopyTo(report.AsSpan(haptics + 2));
            secondHaptics.CopyTo(report.AsSpan(haptics + 2 + BassHaptics.BlockBytes));
            BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(BtAudioLength - 4),
                Crc32(0xA2, report.AsSpan(0, BtAudioLength - 4)));
            TryWrite(report, "Audio frame");
        }
    }

    void TryWrite(byte[] report, string what)
    {
        try
        {
            _stream!.Write(report);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException)
        {
            Log.Write($"{what} write failed: {ex.Message}");
        }
    }

    static void FillCommon(Span<byte> common, bool micLedOn)
    {
        common[1] = 0x01;                     // valid_flag1: mic mute LED control
        common[8] = (byte)(micLedOn ? 1 : 0); // mute_button_led
    }

    static uint Crc32(byte seed, ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        crc = Step(crc, seed);
        foreach (byte b in data)
            crc = Step(crc, b);
        return ~crc;

        static uint Step(uint crc, byte b)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            return crc;
        }
    }
}
