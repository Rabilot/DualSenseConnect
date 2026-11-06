using System.Drawing.Drawing2D;

namespace DualSenseConnect.UI;

/// <summary>
/// The app's gamepad glyph. app.ico (the .exe icon) is generated from <see cref="Draw"/>;
/// the tray keeps its microphone icons for the mute state.
/// </summary>
static class GamepadIcon
{
    public static readonly Color Blue = Color.FromArgb(61, 139, 255);   // DualSense light-bar blue

    /// <summary>The .exe icon (app.ico), for window title bars.</summary>
    public static Icon App { get; } = Icon.ExtractAssociatedIcon(Environment.ProcessPath!)!;

    public static Bitmap Draw(int size, Color color)
    {
        var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = size;

        using (var body = new GraphicsPath { FillMode = FillMode.Winding })
        {
            body.AddEllipse(0.03f * s, 0.34f * s, 0.36f * s, 0.58f * s);   // left grip
            body.AddEllipse(0.61f * s, 0.34f * s, 0.36f * s, 0.58f * s);   // right grip
            AddRoundedRect(body, 0.06f * s, 0.20f * s, 0.88f * s, 0.42f * s, 0.18f * s);
            using var brush = new SolidBrush(color);
            g.FillPath(brush, body);
        }

        // Cut-outs show the background through, so the glyph reads on light and dark backgrounds.
        g.CompositingMode = CompositingMode.SourceCopy;
        using var hole = new SolidBrush(Color.Transparent);
        float stick = Math.Max(0.13f * s, 2.5f);
        g.FillEllipse(hole, 0.37f * s - stick / 2, 0.55f * s - stick / 2, stick, stick);
        g.FillEllipse(hole, 0.63f * s - stick / 2, 0.55f * s - stick / 2, stick, stick);
        if (size >= 32)
        {
            // D-pad and face buttons only where there are enough pixels for them.
            float arm = 0.05f * s, reach = 0.15f * s;
            g.FillRectangle(hole, 0.27f * s - reach / 2, 0.41f * s - arm / 2, reach, arm);
            g.FillRectangle(hole, 0.27f * s - arm / 2, 0.41f * s - reach / 2, arm, reach);
            float dot = 0.055f * s;
            foreach (var (dx, dy) in new[] { (0f, -1f), (0f, 1f), (-1f, 0f), (1f, 0f) })
                g.FillEllipse(hole, 0.73f * s + dx * 0.065f * s - dot / 2, 0.41f * s + dy * 0.065f * s - dot / 2, dot, dot);
        }
        return bitmap;
    }

    static void AddRoundedRect(GraphicsPath path, float x, float y, float w, float h, float r)
    {
        path.StartFigure();
        path.AddArc(x, y, 2 * r, 2 * r, 180, 90);
        path.AddArc(x + w - 2 * r, y, 2 * r, 2 * r, 270, 90);
        path.AddArc(x + w - 2 * r, y + h - 2 * r, 2 * r, 2 * r, 0, 90);
        path.AddArc(x, y + h - 2 * r, 2 * r, 2 * r, 90, 90);
        path.CloseFigure();
    }
}
