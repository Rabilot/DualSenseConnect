using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DualSenseConnect.Core;

/// <summary>System-wide keyboard shortcut via RegisterHotKey; no keyboard hook, no polling.</summary>
sealed class GlobalHotkey : NativeWindow, IDisposable
{
    const int WmHotkey = 0x0312;
    const uint ModAlt = 0x0001, ModControl = 0x0002, ModShift = 0x0004, ModNoRepeat = 0x4000;
    const int Id = 1;

    public const Keys Default = Keys.Control | Keys.Shift | Keys.M;

    Keys? _registered;

    public event Action? Pressed;

    public GlobalHotkey()
    {
        CreateHandle(new CreateParams { Parent = new IntPtr(-3) });   // HWND_MESSAGE: invisible, messages only
    }

    /// <summary>A shortcut needs Ctrl, Alt or Shift plus one ordinary key, so plain typing never triggers it.</summary>
    public static bool IsValid(Keys combo)
    {
        var key = combo & Keys.KeyCode;
        return (combo & (Keys.Control | Keys.Alt | Keys.Shift)) != 0
               && key is not (Keys.None or Keys.ControlKey or Keys.ShiftKey or Keys.Menu
                   or Keys.LControlKey or Keys.RControlKey or Keys.LShiftKey or Keys.RShiftKey
                   or Keys.LMenu or Keys.RMenu or Keys.LWin or Keys.RWin);
    }

    /// <summary>"Ctrl+Shift+M".</summary>
    public static string Format(Keys combo) => new KeysConverter().ConvertToInvariantString(combo) ?? combo.ToString();

    /// <summary>Registers <paramref name="combo"/> (null unregisters). Returns false when another app owns it.</summary>
    public bool Set(Keys? combo)
    {
        if (combo == _registered)
            return true;
        if (_registered != null)
        {
            UnregisterHotKey(Handle, Id);
            _registered = null;
        }
        if (combo is not { } keys)
            return true;

        uint modifiers = ModNoRepeat
                         | ((keys & Keys.Control) != 0 ? ModControl : 0)
                         | ((keys & Keys.Shift) != 0 ? ModShift : 0)
                         | ((keys & Keys.Alt) != 0 ? ModAlt : 0);
        if (!RegisterHotKey(Handle, Id, modifiers, (uint)(keys & Keys.KeyCode)))
            return false;
        _registered = keys;
        return true;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey && m.WParam == Id)
            Pressed?.Invoke();
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        Set(null);
        DestroyHandle();
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

    [DllImport("user32.dll")]
    static extern bool UnregisterHotKey(IntPtr window, int id);
}
