using DualSenseConnect.Audio;
using DualSenseConnect.Core;
using DualSenseConnect.Localization;

namespace DualSenseConnect.UI;

/// <summary>
/// Small settings window; every change is applied and saved immediately. Texts are read once at
/// construction, so a language change re-creates the window.
/// </summary>
sealed class SettingsForm : Form
{
    static readonly Padding Indent = new(20, 3, 3, 3);

    readonly Settings _settings;
    readonly Action _changed;
    readonly Action<bool> _capturingHotkey;
    readonly Strings _t = Strings.Current;

    readonly List<(string? Code, string Name)> _languages = [];
    readonly ComboBox _language = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    readonly List<(string? Id, string Name)> _devices = [];
    readonly CheckBox _enabled = new() { AutoSize = true };
    readonly CheckBox _always = new() { AutoSize = true, Margin = Indent };
    readonly CheckBox _onHeadphones = new() { AutoSize = true, Margin = Indent };
    readonly ComboBox _source = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 360 };
    readonly TrackBar _volume = new()
    {
        Minimum = 0, Maximum = 100, TickFrequency = 10, SmallChange = 10, LargeChange = 10, Width = 360,
    };
    readonly (string Id, string Name)? _cable = OutputDevices.FindCable();
    readonly CheckBox _useCable = new() { AutoSize = true };
    readonly CheckBox _switchDefault = new() { AutoSize = true, Margin = Indent };
    readonly CheckBox _haptics = new() { AutoSize = true };
    readonly TrackBar _hapticsStrength = new()
    {
        Minimum = 10, Maximum = 100, TickFrequency = 10, SmallChange = 10, LargeChange = 10, Width = 360,
    };
    readonly CheckBox _feedback = new() { AutoSize = true };
    readonly CheckBox _hotkey = new() { AutoSize = true, Margin = new Padding(3, 5, 3, 3) };
    readonly TextBox _hotkeyBox = new() { ReadOnly = true, Width = 150, BackColor = SystemColors.Window, Cursor = Cursors.Hand };
    readonly CheckBox _powerOffOnLock = new() { AutoSize = true };
    readonly CheckBox _autoBuffer = new() { AutoSize = true };
    bool _loading;

    /// <param name="capturingHotkey">true while the shortcut field has focus: the live shortcut must be
    /// released then, or pressing it would toggle the mic instead of reaching the field.</param>
    public SettingsForm(Settings settings, Action changed, Action<bool> capturingHotkey)
    {
        _settings = settings;
        _changed = changed;
        _capturingHotkey = capturingHotkey;
        Text = _t.SettingsTitle;
        Icon = GamepadIcon.App;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        var layout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
        Controls.Add(layout);

        var languageRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 0, 0, 4), WrapContents = false };
        var languageLabel = Labels.Caption(_t.Language, top: 4);
        languageLabel.Margin = new Padding(0, 4, 6, 0);
        languageRow.Controls.Add(languageLabel);
        languageRow.Controls.Add(_language);
        layout.Controls.Add(languageRow);
        _languages.Add((null, _t.LanguageAuto));
        _languages.AddRange(Strings.Languages.Select(l => ((string?)l.Code, l.Name)));
        foreach (var language in _languages)
            _language.Items.Add(language.Name);

        layout.Controls.Add(Labels.Heading(_t.SectionPadAudio, Font, top: 10));
        _enabled.Text = _t.PadAudioEnabled;
        _always.Text = _t.StreamAlways;
        _onHeadphones.Text = _t.StreamOnHeadphones;
        layout.Controls.Add(_enabled);
        layout.Controls.Add(_always);
        layout.Controls.Add(_onHeadphones);

        layout.Controls.Add(Labels.Caption(_t.AudioSource));
        _devices.Add((null, _t.DefaultOutputDevice));
        _devices.AddRange(AudioStreamer.ListOutputDevices().Select(d => ((string?)d.Id, d.Name)));
        foreach (var device in _devices)
            _source.Items.Add(device.Name);
        layout.Controls.Add(_source);

        layout.Controls.Add(Labels.Caption(_t.PadVolume));
        layout.Controls.Add(_volume);
        layout.Controls.Add(Labels.Hint(_t.PadAudioHint));

        layout.Controls.Add(Labels.Heading(_t.SectionOutputDevice, Font));
        if (_cable is { } cable)
        {
            _useCable.Text = _t.UseCable;
            _switchDefault.Text = _t.SwitchDefaultOutput;
            layout.Controls.Add(_useCable);
            layout.Controls.Add(_switchDefault);
            layout.Controls.Add(Labels.Hint(string.Format(_t.CableInUse, cable.Name)));
        }
        else
        {
            layout.Controls.Add(Labels.Caption(_t.CableMissing, top: 0));
        }
        var link = new LinkLabel
        {
            Text = "VB-CABLE — donationware, www.vb-cable.com",
            AutoSize = true,
            Margin = new Padding(0, 4, 0, 0),
        };
        link.LinkClicked += (_, _) => System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo(OutputDevices.CableDownloadUrl) { UseShellExecute = true });
        layout.Controls.Add(link);

        layout.Controls.Add(Labels.Heading(_t.SectionHaptics, Font));
        _haptics.Text = _t.HapticsEnabled;
        layout.Controls.Add(_haptics);
        layout.Controls.Add(Labels.Caption(_t.HapticsStrength, top: 6));
        layout.Controls.Add(_hapticsStrength);
        layout.Controls.Add(Labels.Hint(_t.HapticsHint));

        layout.Controls.Add(Labels.Heading(_t.SectionOther, Font));
        _feedback.Text = _t.FeedbackPulses;
        _hotkey.Text = _t.MuteHotkey;
        _powerOffOnLock.Text = _t.PowerOffOnLock;
        _autoBuffer.Text = _t.AutoBuffer;
        layout.Controls.Add(_feedback);
        var hotkeyRow = new FlowLayoutPanel { AutoSize = true, Margin = Padding.Empty, WrapContents = false };
        hotkeyRow.Controls.Add(_hotkey);
        hotkeyRow.Controls.Add(_hotkeyBox);
        layout.Controls.Add(hotkeyRow);
        layout.Controls.Add(_powerOffOnLock);
        layout.Controls.Add(_autoBuffer);
        layout.Controls.Add(Labels.Hint(_t.OtherHint));

        Reload();
        _hotkeyBox.Enter += (_, _) =>
        {
            _capturingHotkey(true);
            _hotkeyBox.Text = _t.HotkeyPrompt;
        };
        _hotkeyBox.Leave += (_, _) =>
        {
            _hotkeyBox.Text = GlobalHotkey.Format(_settings.MuteHotkeyKeys);
            _capturingHotkey(false);
        };
        _hotkeyBox.KeyDown += (_, e) =>
        {
            e.SuppressKeyPress = true;
            if (!GlobalHotkey.IsValid(e.KeyData))
                return;   // still holding only modifiers, or a key without one
            _settings.MuteHotkeyKeys = e.KeyData;
            _hotkeyBox.Text = GlobalHotkey.Format(e.KeyData);
            Apply();
            ActiveControl = null;   // done: leaving the field re-registers the new shortcut
        };
        foreach (var box in new[] { _enabled, _always, _onHeadphones, _useCable, _switchDefault, _haptics, _feedback,
                     _hotkey, _powerOffOnLock, _autoBuffer })
            box.CheckedChanged += (_, _) => Apply();
        _language.SelectedIndexChanged += (_, _) => Apply();
        _source.SelectedIndexChanged += (_, _) => Apply();
        _volume.ValueChanged += (_, _) => Apply();
        _hapticsStrength.ValueChanged += (_, _) => Apply();
    }

    /// <summary>Shows settings changed from the gamepad while the window is open.</summary>
    public void Reload()
    {
        _loading = true;
        _language.SelectedIndex = Math.Max(0, _languages.FindIndex(l => l.Code == _settings.Language));
        _enabled.Checked = _settings.AudioEnabled;
        _always.Checked = _settings.StreamAudio;
        _onHeadphones.Checked = _settings.StreamOnHeadphones;
        _source.SelectedIndex = Math.Max(0, _devices.FindIndex(d => d.Id == _settings.SourceDeviceId));
        _volume.Value = _settings.VolumePercent;
        _useCable.Checked = _settings.UseVirtualDevice;
        _switchDefault.Checked = _settings.SwitchDefaultOutput;
        _haptics.Checked = _settings.HapticsEnabled;
        _hapticsStrength.Value = Math.Clamp(_settings.HapticsStrength, _hapticsStrength.Minimum, _hapticsStrength.Maximum);
        _feedback.Checked = _settings.FeedbackPulses;
        _hotkey.Checked = _settings.MuteHotkey;
        _hotkeyBox.Text = GlobalHotkey.Format(_settings.MuteHotkeyKeys);
        _powerOffOnLock.Checked = _settings.PowerOffOnLock;
        _autoBuffer.Checked = _settings.AutoBuffer;
        UpdateEnabled();
        _loading = false;
    }

    /// <summary>Dependent controls are greyed out while the option they belong to is off.</summary>
    void UpdateEnabled()
    {
        _always.Enabled = _onHeadphones.Enabled = _enabled.Checked;
        _source.Enabled = _cable == null || !_useCable.Checked;
        _switchDefault.Enabled = _useCable.Checked;
        _hapticsStrength.Enabled = _haptics.Checked;
        _hotkeyBox.Enabled = _hotkey.Checked;
    }

    void Apply()
    {
        if (_loading)
            return;
        _settings.Language = _languages[_language.SelectedIndex].Code;
        _settings.AudioEnabled = _enabled.Checked;
        _settings.StreamAudio = _always.Checked;
        _settings.StreamOnHeadphones = _onHeadphones.Checked;
        _settings.SourceDeviceId = _devices[_source.SelectedIndex].Id;
        _settings.VolumePercent = _volume.Value;
        _settings.UseVirtualDevice = _useCable.Checked;
        _settings.SwitchDefaultOutput = _switchDefault.Checked;
        _settings.HapticsEnabled = _haptics.Checked;
        _settings.HapticsStrength = _hapticsStrength.Value;
        _settings.FeedbackPulses = _feedback.Checked;
        _settings.MuteHotkey = _hotkey.Checked;
        _settings.PowerOffOnLock = _powerOffOnLock.Checked;
        _settings.AutoBuffer = _autoBuffer.Checked;
        _settings.Save();
        UpdateEnabled();
        _changed();
    }
}
