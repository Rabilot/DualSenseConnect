namespace DualSenseConnect.UI;

/// <summary>Label styles shared by the windows. Text wraps by width, since translations differ in length.</summary>
static class Labels
{
    const int WrapWidth = 460;

    public static Label Heading(string text, Font baseFont, int top = 16) => new()
    {
        Text = text,
        Font = new Font(baseFont, FontStyle.Bold),
        AutoSize = true,
        MaximumSize = new Size(WrapWidth, 0),
        Margin = new Padding(0, top, 0, 6),
    };

    public static Label Caption(string text, int top = 10) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(WrapWidth, 0),
        Margin = new Padding(0, top, 0, 2),
    };

    public static Label Hint(string text) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(WrapWidth, 0),
        ForeColor = SystemColors.GrayText,
        Margin = new Padding(0, 6, 0, 0),
    };
}
