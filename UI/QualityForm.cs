using DualSenseConnect.Gamepad;
using DualSenseConnect.Localization;

namespace DualSenseConnect.UI;

/// <summary>Live Bluetooth link quality; statistics are only collected while this window is open.</summary>
sealed class QualityForm : Form
{
    readonly DualSense _pad;
    readonly LinkStats _stats = new();
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    readonly Label _rating = new() { AutoSize = true };
    readonly Label _details = new() { AutoSize = true };

    public QualityForm(DualSense pad)
    {
        _pad = pad;
        Text = Strings.Current.QualityTitle;
        Icon = GamepadIcon.App;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        var layout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
        Controls.Add(layout);
        _rating.Font = new Font(Font.FontFamily, 14, FontStyle.Bold);
        layout.Controls.Add(_rating);
        layout.Controls.Add(_details);
        var hint = Labels.Hint(string.Format(Strings.Current.QualityHint, LinkStats.DropoutMs));
        hint.Margin = new Padding(0, 8, 0, 8);
        layout.Controls.Add(hint);
        var reset = new Button { Text = Strings.Current.QualityReset, AutoSize = true };
        reset.Click += (_, _) =>
        {
            _stats.Reset();
            UpdateView();
        };
        layout.Controls.Add(reset);

        _timer.Tick += (_, _) => UpdateView();
        Shown += (_, _) =>
        {
            _pad.Stats = _stats;
            _timer.Start();
            UpdateView();
        };
        FormClosed += (_, _) =>
        {
            _timer.Stop();
            _timer.Dispose();
            _pad.Stats = null;
        };
    }

    void UpdateView()
    {
        var t = Strings.Current;
        var s = _stats.Read();
        var (rating, color) = s.Rating switch
        {
            LinkRating.Excellent => (t.RatingExcellent, Color.FromArgb(46, 160, 67)),
            LinkRating.Good => (t.RatingGood, Color.FromArgb(46, 120, 200)),
            LinkRating.Fair => (t.RatingFair, Color.FromArgb(200, 130, 0)),
            _ => (t.RatingPoor, Color.FromArgb(218, 54, 51)),
        };
        _rating.Text = _pad.IsConnected ? string.Format(t.QualityRating, rating) : t.PadNotConnected;
        _rating.ForeColor = _pad.IsConnected ? color : SystemColors.GrayText;
        _details.Text = string.Format(t.QualityDetails, s.Elapsed.ToString(@"mm\:ss"), s.ReportsPerSecond,
            s.Dropouts, s.DropoutsPerMinute.ToString("0.#"), s.LongestGapMs.ToString("0"), s.Disconnects);
    }
}
