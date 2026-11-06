using System.Globalization;

namespace DualSenseConnect.Localization;

/// <summary>
/// All user-facing text. Each language lives in its own partial file in this folder; every property is
/// <c>required</c>, so a language that misses a string fails the build instead of showing a blank label.
/// Log messages stay in English: they are for diagnostics.
/// </summary>
sealed partial class Strings
{
    /// <summary>Language codes with their names as speakers of that language write them.</summary>
    public static readonly IReadOnlyList<(string Code, string Name)> Languages =
    [
        ("en", "English"), ("es", "Español"), ("fr", "Français"), ("de", "Deutsch"),
        ("ru", "Русский"), ("pt", "Português"), ("pl", "Polski"), ("uk", "Українська"),
        ("be", "Беларуская"), ("ja", "日本語"), ("zh", "中文（简体）"), ("ko", "한국어"),
    ];

    // Resolved on first use: the language tables are static fields spread over partial files,
    // and their initialisation order relative to a field initializer here is not defined.
    static Strings? _current;

    public static Strings Current => _current ??= For(null);

    /// <summary>null follows the Windows display language; unknown languages fall back to English.</summary>
    public static void Use(string? code) => _current = For(code);

    static Strings For(string? code) => (code ?? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName) switch
    {
        "es" => Es, "fr" => Fr, "de" => De, "ru" => Ru, "pt" => Pt, "pl" => Pl,
        "uk" => Uk, "be" => Be, "ja" => Ja, "zh" => Zh, "ko" => Ko,
        _ => En,
    };

    // Tray menu and status
    public required string MenuToggleMic { get; init; }
    public required string MenuPowerOff { get; init; }
    public required string MenuSettings { get; init; }
    public required string MenuQuality { get; init; }
    public required string MenuAutostart { get; init; }
    public required string MenuOpenLog { get; init; }
    public required string MenuExit { get; init; }
    public required string MicOn { get; init; }
    public required string MicMuted { get; init; }
    public required string MicNotFound { get; init; }
    public required string PadNotFound { get; init; }
    public required string PadConnected { get; init; }
    /// <summary>{0}: battery text.</summary>
    public required string PadStatus { get; init; }
    public required string BatteryError { get; init; }
    /// <summary>{0}: percent.</summary>
    public required string BatteryCharging { get; init; }
    public required string AudioToHeadphones { get; init; }
    public required string AudioToSpeaker { get; init; }

    // Notifications
    /// <summary>{0}: shortcut, e.g. Ctrl+Shift+M.</summary>
    public required string HotkeyTaken { get; init; }
    public required string ChargedTitle { get; init; }
    public required string ChargedText { get; init; }
    public required string LowBatteryTitle { get; init; }
    /// <summary>{0}: percent.</summary>
    public required string LowBatteryText { get; init; }
    public required string PadAudioOn { get; init; }
    public required string PadAudioOff { get; init; }

    // Settings window
    public required string SettingsTitle { get; init; }
    public required string Language { get; init; }
    public required string LanguageAuto { get; init; }
    public required string SectionPadAudio { get; init; }
    public required string PadAudioEnabled { get; init; }
    public required string StreamAlways { get; init; }
    public required string StreamOnHeadphones { get; init; }
    public required string AudioSource { get; init; }
    public required string DefaultOutputDevice { get; init; }
    public required string PadVolume { get; init; }
    public required string PadAudioHint { get; init; }
    public required string SectionOutputDevice { get; init; }
    public required string UseCable { get; init; }
    public required string SwitchDefaultOutput { get; init; }
    /// <summary>{0}: the cable's device name.</summary>
    public required string CableInUse { get; init; }
    public required string CableMissing { get; init; }
    public required string SectionHaptics { get; init; }
    public required string HapticsEnabled { get; init; }
    public required string HapticsStrength { get; init; }
    public required string HapticsHint { get; init; }
    public required string SectionOther { get; init; }
    public required string FeedbackPulses { get; init; }
    public required string MuteHotkey { get; init; }
    public required string HotkeyPrompt { get; init; }
    public required string PowerOffOnLock { get; init; }
    public required string AutoBuffer { get; init; }
    public required string OtherHint { get; init; }

    // Connection quality window
    public required string QualityTitle { get; init; }
    /// <summary>{0}: dropout threshold in ms.</summary>
    public required string QualityHint { get; init; }
    public required string QualityReset { get; init; }
    /// <summary>{0}: rating.</summary>
    public required string QualityRating { get; init; }
    public required string PadNotConnected { get; init; }
    public required string RatingExcellent { get; init; }
    public required string RatingGood { get; init; }
    public required string RatingFair { get; init; }
    public required string RatingPoor { get; init; }
    /// <summary>{0} elapsed mm:ss, {1} reports/s, {2} dropouts, {3} dropouts/min, {4} longest gap ms, {5} disconnects.</summary>
    public required string QualityDetails { get; init; }
}
