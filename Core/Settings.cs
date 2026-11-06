using System.Text.Json;

namespace DualSenseConnect.Core;

sealed class Settings
{
    static readonly string FilePath = Path.Combine(Path.GetDirectoryName(Log.FilePath)!, "settings.json");

    /// <summary>UI language code ("en", "ru", …); null follows the Windows display language.</summary>
    public string? Language { get; set; }

    /// <summary>Master switch for audio to the pad (long press of mute toggles it).</summary>
    public bool AudioEnabled { get; set; } = true;

    /// <summary>Stream audio whenever the pad is connected over Bluetooth.</summary>
    public bool StreamAudio { get; set; }

    /// <summary>Stream audio while headphones are plugged into the pad.</summary>
    public bool StreamOnHeadphones { get; set; } = true;

    /// <summary>Output device to capture; null means the current default device.</summary>
    public string? SourceDeviceId { get; set; }

    public int VolumePercent { get; set; } = 100;

    /// <summary>Take sound from the VB-CABLE device ("DualSense" output) instead of <see cref="SourceDeviceId"/>.</summary>
    public bool UseVirtualDevice { get; set; } = true;

    /// <summary>Make the virtual device the Windows default output while sound goes to the pad.</summary>
    public bool SwitchDefaultOutput { get; set; } = true;

    /// <summary>Outputs to restore once streaming stops; kept on disk so a crash doesn't leave sound in the cable.</summary>
    public string? RestoreDeviceId { get; set; }

    public string? RestoreCommunicationsDeviceId { get; set; }

    /// <summary>Vibration driven by the bass of the played sound.</summary>
    public bool HapticsEnabled { get; set; }

    public int HapticsStrength { get; set; } = 50;

    /// <summary>Short buzz in the hands when the mic is muted/unmuted or the volume changes.</summary>
    public bool FeedbackPulses { get; set; } = true;

    /// <summary>A keyboard shortcut toggles the microphone.</summary>
    public bool MuteHotkey { get; set; } = true;

    /// <summary>The shortcut, as WinForms <see cref="Keys"/> (modifiers | key).</summary>
    public Keys MuteHotkeyKeys { get; set; } = GlobalHotkey.Default;

    /// <summary>Turn the pad off when the PC is locked or goes to sleep.</summary>
    public bool PowerOffOnLock { get; set; }

    /// <summary>Grow the pad's audio buffer when the link drops packets (more delay, fewer gaps).</summary>
    public bool AutoBuffer { get; set; }

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Log.Write($"Settings not loaded: {ex.Message}");
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch (IOException ex)
        {
            Log.Write($"Settings not saved: {ex.Message}");
        }
    }
}
