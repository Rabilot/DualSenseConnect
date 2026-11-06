using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using DualSenseConnect.Audio;
using DualSenseConnect.Core;
using DualSenseConnect.Gamepad;
using DualSenseConnect.Localization;
using DualSenseConnect.Microphone;

namespace DualSenseConnect.UI;

sealed class TrayApp : ApplicationContext
{
    const int LedRefreshMs = 30_000;
    const int MuteBuzzMs = 45;
    const int BufferCheckMs = 10_000;
    const int GapsToGrowBuffer = 3;      // per check window
    const int QuietWindowsToShrink = 6;  // one minute without gaps
    static readonly byte[] BufferLevels = [DualSense.DefaultBufferDepth, 0x50, 0x64, 0x78];

    readonly SynchronizationContext _ui;
    readonly NotifyIcon _tray;
    readonly ToolStripMenuItem _statusItem;
    readonly ToolStripMenuItem _autostartItem;
    readonly System.Windows.Forms.Timer _timer;
    readonly DualSense _pad = new();
    readonly MicWatcher _micWatcher = new();
    readonly HapticPulses _pulses = new();
    readonly GlobalHotkey _hotkey = new();
    readonly System.Windows.Forms.Timer _bufferTimer = new() { Interval = BufferCheckMs };
    int _bufferLevel;
    int _quietWindows;
    int _chargeState = -1;
    readonly Icon _iconOn = MakeIcon(Color.FromArgb(46, 160, 67), slash: false);
    readonly Icon _iconMuted = MakeIcon(Color.FromArgb(218, 54, 51), slash: true);
    readonly Icon _iconNoMic = MakeIcon(Color.Gray, slash: true);

    readonly Settings _settings = Settings.Load();

    bool? _muted;
    bool _connected;
    bool _headphones;
    byte? _batteryStatus;
    int _batteryWarnedAt = 100;
    AudioStreamer? _streamer;
    bool _streamingAudio;
    string? _streamerSource;
    SettingsForm? _settingsForm;
    QualityForm? _qualityForm;
    readonly ToolStripMenuItem _powerOffItem;
    readonly ToolStripMenuItem _toggleMicItem;
    readonly ToolStripMenuItem _settingsItem;
    readonly ToolStripMenuItem _qualityItem;
    readonly ToolStripMenuItem _openLogItem;
    readonly ToolStripMenuItem _exitItem;
    string? _language;

    public TrayApp()
    {
        _ui = SynchronizationContext.Current!;
        _language = _settings.Language;
        Strings.Use(_language);

        _statusItem = new ToolStripMenuItem { Enabled = false };
        Autostart.MigrateFromRunKey();
        _autostartItem = new ToolStripMenuItem("", null, (_, _) => ToggleAutostart()) { Checked = Autostart.IsEnabled() };
        _toggleMicItem = new ToolStripMenuItem("", null, (_, _) => ToggleMute());
        _powerOffItem = new ToolStripMenuItem("", null, (_, _) => _pad.PowerOff());
        _settingsItem = new ToolStripMenuItem("", null, (_, _) => ShowSettings());
        _qualityItem = new ToolStripMenuItem("", null, (_, _) => ShowQuality());
        _openLogItem = new ToolStripMenuItem("", null, (_, _) => OpenLog());
        _exitItem = new ToolStripMenuItem("", null, (_, _) => ExitThread());
        var menu = new ContextMenuStrip();
        menu.Items.AddRange([
            _statusItem, new ToolStripSeparator(),
            _toggleMicItem, _powerOffItem, new ToolStripSeparator(),
            _settingsItem, _qualityItem, _autostartItem, _openLogItem, new ToolStripSeparator(),
            _exitItem,
        ]);

        _tray = new NotifyIcon { ContextMenuStrip = menu, Visible = true };
        _tray.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                ToggleMute();
        };

        // Mute state comes from Windows notifications; the timer only re-sends the pad LED in case
        // another app (e.g. Steam) overwrote it.
        _micWatcher.Changed += () => _ui.Post(_ =>
        {
            _micWatcher.Attach();
            Refresh();
        }, null);
        _timer = new System.Windows.Forms.Timer { Interval = LedRefreshMs };
        _timer.Tick += (_, _) => _pad.SetMuteLed(_muted == true);

        _pad.MuteShortPress += () => _ui.Post(_ => ToggleMute(), null);
        _pad.MuteLongPress += () => _ui.Post(_ => ToggleAudioEnabled(), null);
        _pad.VolumeStep += step => _ui.Post(_ => ChangeVolume(step * 10), null);
        _pad.ConnectionChanged += connected => _ui.Post(_ =>
        {
            _connected = connected;
            if (connected)
            {
                _pad.SetMuteLed(MicMute.IsMuted() == true);
                _timer.Start();
            }
            else
            {
                _timer.Stop();
                _batteryStatus = null;
                _headphones = false;
            }
            UpdateAudio();
            UpdateTray();
        }, null);
        _pad.HeadphonesChanged += plugged => _ui.Post(_ =>
        {
            _headphones = plugged;
            Log.Write(plugged ? "Headphones plugged in" : "Headphones unplugged");
            UpdateAudio();
            UpdateTray();
        }, null);
        _pad.BatteryStatusChanged += status => _ui.Post(_ =>
        {
            var (percent, charging) = DualSense.DecodeBattery(status);
            _batteryStatus = status;
            Log.Write($"Battery status 0x{status:X2}: {percent?.ToString() ?? "error"}%{(charging ? ", charging" : "")}");
            WarnLowBattery(percent, charging);
            NotifyCharged(status >> 4);
            UpdateTray();
        }, null);

        _hotkey.Pressed += ToggleMute;
        ApplyHotkey();
        _bufferTimer.Tick += (_, _) => TuneBuffer();
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        Log.Write("Started");
        ApplyTexts();
        Refresh();
        if (_settings.RestoreDeviceId != null)
            UpdateAudio();   // the previous run ended while Windows output was on the pad
        _pad.Start();
    }

    void ToggleMute()
    {
        bool? current = MicMute.IsMuted();
        if (current == null)
        {
            Log.Write("Mute pressed, but there is no default microphone");
            return;
        }
        MicMute.SetMuted(!current.Value);
        Log.Write(current.Value ? "Microphone unmuted" : "Microphone muted");
        Refresh();
        // Felt, not seen: one buzz = muted, two = back on.
        _pulses.Buzz(MuteBuzzMs);
        if (current.Value)
        {
            _pulses.Pause(70);
            _pulses.Buzz(MuteBuzzMs);
        }
        PlayPulses();
    }

    /// <summary>Sends queued buzzes through the running streamer, or on their own when there is none.</summary>
    void PlayPulses()
    {
        if (!_settings.FeedbackPulses || !_connected || !_pad.IsBluetooth)
        {
            _pulses.Clear();
            return;
        }
        if (_streamer != null)
            _streamer.Wake();
        else
            _pulses.PlayAlone(_pad);
    }

    void ApplyHotkey()
    {
        var combo = GlobalHotkey.IsValid(_settings.MuteHotkeyKeys) ? _settings.MuteHotkeyKeys : GlobalHotkey.Default;
        if (!_hotkey.Set(_settings.MuteHotkey ? combo : null))
        {
            string name = GlobalHotkey.Format(combo);
            Log.Write($"{name} is taken by another app");
            _tray.ShowBalloonTip(5000, "DualSenseConnect", string.Format(Strings.Current.HotkeyTaken, name),
                ToolTipIcon.Warning);
        }
    }

    /// <summary>One notification when charging completes (status nibble 1 = charging, 2 = full).</summary>
    void NotifyCharged(int chargeState)
    {
        if (_chargeState == 1 && chargeState == 2)
            _tray.ShowBalloonTip(10000, Strings.Current.ChargedTitle, Strings.Current.ChargedText, ToolTipIcon.Info);
        _chargeState = chargeState;
    }

    void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock && _settings.PowerOffOnLock && _connected)
        {
            Log.Write("PC locked: turning the pad off");
            _pad.PowerOff();
        }
    }

    void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend && _settings.PowerOffOnLock && _connected)
        {
            Log.Write("PC going to sleep: turning the pad off");
            _pad.PowerOff();
        }
    }

    /// <summary>
    /// Every 10 s while sound goes to the pad: several link gaps grow the pad's buffer one step,
    /// a quiet minute shrinks it back, so delay is only added while the link needs it.
    /// </summary>
    void TuneBuffer()
    {
        int gaps = _pad.TakeGapCount();
        int level = _bufferLevel;
        if (gaps >= GapsToGrowBuffer)
        {
            _quietWindows = 0;
            level = Math.Min(level + 1, BufferLevels.Length - 1);
        }
        else if (gaps == 0 && ++_quietWindows >= QuietWindowsToShrink)
        {
            _quietWindows = 0;
            level = Math.Max(level - 1, 0);
        }
        if (level != _bufferLevel)
            SetBufferLevel(level, $"{gaps} gaps in {BufferCheckMs / 1000} s");
    }

    void SetBufferLevel(int level, string reason)
    {
        _bufferLevel = level;
        _pad.SpeakerBufferDepth = BufferLevels[level];
        Log.Write($"Pad audio buffer level {level} (0x{BufferLevels[level]:X2}): {reason}");
    }

    void UpdateBufferTuning()
    {
        bool on = _settings.AutoBuffer && _streamingAudio;
        if (on && !_bufferTimer.Enabled)
        {
            _pad.TakeGapCount();   // start counting from now
            _quietWindows = 0;
            _bufferTimer.Start();
        }
        else if (!on && _bufferTimer.Enabled)
        {
            _bufferTimer.Stop();
        }
        if (!_settings.AutoBuffer && _bufferLevel != 0)
            SetBufferLevel(0, "auto tuning off");
    }

    /// <summary>One notification at 20% and one at 10%; re-armed once the pad charges.</summary>
    void WarnLowBattery(int? percent, bool charging)
    {
        if (percent == null)
            return;
        if (charging || percent > 20)
        {
            _batteryWarnedAt = 100;
            return;
        }
        int threshold = percent <= 10 ? 10 : 20;
        if (threshold >= _batteryWarnedAt)
            return;
        _batteryWarnedAt = threshold;
        _tray.ShowBalloonTip(10000, Strings.Current.LowBatteryTitle, string.Format(Strings.Current.LowBatteryText, percent),
            ToolTipIcon.Warning);
    }

    /// <summary>Long press of mute: turns audio to the pad on or off.</summary>
    void ToggleAudioEnabled()
    {
        _settings.AudioEnabled = !_settings.AudioEnabled;
        _settings.Save();
        _settingsForm?.Reload();
        Log.Write(_settings.AudioEnabled ? "Pad audio enabled" : "Pad audio disabled");
        UpdateAudio();
        UpdateTray();
        _tray.ShowBalloonTip(3000, "DualSenseConnect",
            _settings.AudioEnabled ? Strings.Current.PadAudioOn : Strings.Current.PadAudioOff, ToolTipIcon.None);
    }

    /// <summary>Mute + D-pad: pad-side volume in 10% steps.</summary>
    void ChangeVolume(int delta)
    {
        int volume = Math.Clamp(_settings.VolumePercent + delta, 0, 100);
        if (volume == _settings.VolumePercent)
            return;
        _settings.VolumePercent = volume;
        _settings.Save();
        _settingsForm?.Reload();
        Log.Write($"Pad volume {volume}%");
        UpdateAudio();
        _pulses.Buzz(25, amplitude: 80, hz: 250);
        PlayPulses();
    }

    void ShowQuality()
    {
        if (_qualityForm == null)
        {
            _qualityForm = new QualityForm(_pad);
            _qualityForm.FormClosed += (_, _) => _qualityForm = null;
            _qualityForm.Show();
        }
        _qualityForm.Activate();
    }

    /// <summary>Starts, reconfigures or stops audio streaming to match the settings and the pad state.</summary>
    void UpdateAudio()
    {
        bool link = _connected && _pad.IsBluetooth;
        bool audio = link && _settings.AudioEnabled
                     && (_settings.StreamAudio || _settings.StreamOnHeadphones && _headphones);
        int haptics = link && _settings.HapticsEnabled ? _settings.HapticsStrength : 0;
        bool wanted = audio || haptics > 0;

        var cable = _settings.UseVirtualDevice ? FindCable() : null;
        string? source = cable?.Id ?? _settings.SourceDeviceId;
        UpdateDefaultOutput(cable?.Id, audio && _settings.SwitchDefaultOutput);

        if (_streamer != null && (!wanted || _streamerSource != source))
        {
            _streamer.Dispose();
            _streamer = null;
            Log.Write("Audio streaming off");
        }

        if (wanted && _streamer == null)
        {
            try
            {
                _streamer = new AudioStreamer(_pad, _pulses, source, audio, _headphones,
                    _settings.VolumePercent, haptics);
                _streamer.Start();
                _streamerSource = source;
                Log.Write("Audio streaming on");
            }
            catch (Exception ex)
            {
                Log.Write($"Audio streaming failed to start: {ex.Message}");
                _streamer?.Dispose();
                _streamer = null;
            }
        }

        if (_streamer != null)
        {
            _streamer.Audio = audio;
            _streamer.Jack = _headphones;
            _streamer.VolumePercent = _settings.VolumePercent;
            _streamer.HapticsPercent = haptics;
        }
        _streamingAudio = _streamer != null && audio;
        UpdateBufferTuning();
    }

    static (string Id, string Name)? FindCable()
    {
        try
        {
            return OutputDevices.FindCable();
        }
        catch (COMException ex)
        {
            Log.Write($"Looking for VB-CABLE failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// While sound goes to the pad, the virtual cable is the Windows default output; afterwards the
    /// previous default comes back, unless the user picked another device in the meantime.
    /// </summary>
    void UpdateDefaultOutput(string? cableId, bool toPad)
    {
        try
        {
            if (toPad && cableId != null && _settings.RestoreDeviceId == null)
            {
                string? current = OutputDevices.GetDefault(Role.Multimedia);
                if (current == cableId)
                    return;   // already the default: nothing of the user's to restore later
                _settings.RestoreDeviceId = current;
                _settings.RestoreCommunicationsDeviceId = OutputDevices.GetDefault(Role.Communications);
                _settings.Save();
                OutputDevices.SetDefault(cableId, Role.Console, Role.Multimedia, Role.Communications);
                Log.Write("Windows output switched to the pad");
            }
            else if (!toPad && _settings.RestoreDeviceId is { } previous)
            {
                bool stillOnPad = cableId == null || OutputDevices.GetDefault(Role.Multimedia) == cableId;
                if (stillOnPad && OutputDevices.Exists(previous))
                {
                    OutputDevices.SetDefault(previous, Role.Console, Role.Multimedia);
                    string communications = _settings.RestoreCommunicationsDeviceId is { } c && OutputDevices.Exists(c)
                        ? c
                        : previous;
                    OutputDevices.SetDefault(communications, Role.Communications);
                    Log.Write("Windows output restored");
                }
                _settings.RestoreDeviceId = _settings.RestoreCommunicationsDeviceId = null;
                _settings.Save();
            }
        }
        catch (COMException ex)
        {
            Log.Write($"Switching Windows output failed: {ex.Message}");
        }
    }

    void ShowSettings(Point? location = null)
    {
        if (_settingsForm == null)
        {
            var form = new SettingsForm(_settings, () =>
            {
                UpdateAudio();
                UpdateTray();
                ApplyHotkey();
                // Deferred: the language change re-creates the window whose event is running now.
                if (_settings.Language != _language)
                    _ui.Post(_ => ChangeLanguage(), null);
            }, capturing =>
            {
                if (capturing)
                    _hotkey.Set(null);
                else
                    ApplyHotkey();
            });
            if (location is { } at)
            {
                form.StartPosition = FormStartPosition.Manual;
                form.Location = at;
            }
            form.FormClosed += (_, _) =>
            {
                if (_settingsForm == form)
                    _settingsForm = null;
            };
            _settingsForm = form;
            form.Show();
        }
        _settingsForm.Activate();
    }

    /// <summary>Picks up mute changes made elsewhere and keeps the pad LED in sync.</summary>
    void Refresh()
    {
        bool? muted;
        try
        {
            muted = MicMute.IsMuted();
        }
        catch (Exception ex)
        {
            Log.Write($"Reading mute state failed: {ex.Message}");
            muted = null;
        }

        if (muted == _muted)
            return;
        Log.Write($"Mic state: {(muted == null ? "no microphone" : muted.Value ? "muted" : "on")}");
        _muted = muted;
        _pad.SetMuteLed(muted == true);
        UpdateTray();
    }

    void UpdateTray()
    {
        var t = Strings.Current;
        string mic = _muted switch
        {
            true => t.MicMuted,
            false => t.MicOn,
            null => t.MicNotFound,
        };
        string pad = !_connected ? t.PadNotFound
            : _batteryStatus is { } status ? string.Format(t.PadStatus, FormatBattery(status))
            : t.PadConnected;
        string? audio = !_streamingAudio ? null
            : _headphones ? t.AudioToHeadphones
            : t.AudioToSpeaker;
        _powerOffItem.Enabled = _connected && _pad.IsBluetooth;
        _statusItem.Text = $"{mic} · {pad}";
        string tooltip = audio == null ? $"{mic}\n{pad}" : $"{mic}\n{pad}\n{audio}";
        _tray.Text = tooltip.Length <= 127 ? tooltip : tooltip[..127];   // Windows' tooltip limit
        _tray.Icon = _muted switch
        {
            true => _iconMuted,
            false => _iconOn,
            null => _iconNoMic,
        };
    }

    static string FormatBattery(byte status)
    {
        var (percent, charging) = DualSense.DecodeBattery(status);
        return percent == null ? Strings.Current.BatteryError
            : charging ? string.Format(Strings.Current.BatteryCharging, percent)
            : $"{percent}%";
    }

    /// <summary>Menu texts in the current language; called at start and after the language changes.</summary>
    void ApplyTexts()
    {
        var t = Strings.Current;
        _toggleMicItem.Text = t.MenuToggleMic;
        _powerOffItem.Text = t.MenuPowerOff;
        _settingsItem.Text = t.MenuSettings;
        _qualityItem.Text = t.MenuQuality;
        _autostartItem.Text = t.MenuAutostart;
        _openLogItem.Text = t.MenuOpenLog;
        _exitItem.Text = t.MenuExit;
        UpdateTray();
    }

    /// <summary>Switches the UI language; open windows are re-created since they read texts once.</summary>
    void ChangeLanguage()
    {
        _language = _settings.Language;
        Strings.Use(_language);
        Log.Write($"Language: {_language ?? "system"}");
        ApplyTexts();
        _qualityForm?.Close();
        if (_settingsForm is { } old)
        {
            var location = old.Location;
            old.Close();
            ShowSettings(location);
        }
    }

    void ToggleAutostart()
    {
        if (Autostart.IsEnabled())
            Autostart.Disable();
        else
            Autostart.Enable();
        _autostartItem.Checked = Autostart.IsEnabled();
    }

    static void OpenLog()
    {
        if (File.Exists(Log.FilePath))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Log.FilePath) { UseShellExecute = true });
    }

    protected override void ExitThreadCore()
    {
        _timer.Stop();
        _bufferTimer.Stop();
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _hotkey.Dispose();
        _micWatcher.Dispose();
        _streamer?.Dispose();
        if (_settings.RestoreDeviceId != null)
            UpdateDefaultOutput(FindCable()?.Id, toPad: false);
        _pad.SetMuteLed(false);
        _pad.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        Log.Write("Stopped");
        base.ExitThreadCore();
    }

    /// <summary>Draws a simple microphone glyph, crossed out when muted.</summary>
    static Icon MakeIcon(Color color, bool slash)
    {
        const int size = 32;
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(color);
            using var pen = new Pen(color, 3);
            g.FillRectangle(brush, 12, 3, 8, 13);
            g.FillEllipse(brush, 12, 0, 8, 7);
            g.FillEllipse(brush, 12, 12, 8, 8);
            g.DrawArc(pen, 7, 6, 18, 18, 0, 180);
            g.DrawLine(pen, 16, 24, 16, 29);
            g.DrawLine(pen, 10, 29, 22, 29);
            if (slash)
            {
                using var slashPen = new Pen(color, 4);
                g.DrawLine(slashPen, 4, 4, 28, 28);
            }
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }
}
