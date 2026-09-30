using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace FrameBurst.UI;

/// <summary>Read-only text box that records a key combination. Backspace/Delete clears it.</summary>
public sealed partial class HotkeyBox : Microsoft.UI.Xaml.Controls.TextBox
{
    private Hotkey _value = new();

    public HotkeyBox()
    {
        IsReadOnly = true;
        PlaceholderText = "Press a key…";
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewKeyUp += OnPreviewKeyUp;
        LostFocus += (_, _) => Text = _value.ToString();
    }

    public Hotkey Value
    {
        get => _value;
        set { _value = value; Text = value.ToString(); }
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Capture everything, including Tab, Alt combos and PrintScreen.
        e.Handled = true;
        HandleKey(e.Key);
    }

    private void OnPreviewKeyUp(object sender, KeyRoutedEventArgs e)
    {
        // PrintScreen only arrives as KeyUp on many systems.
        e.Handled = true;
        if (e.Key == VirtualKey.Snapshot) HandleKey(e.Key);
    }

    private void HandleKey(VirtualKey vk)
    {
        var key = (Keys)(int)vk;
        var mods = CurrentModifiers();
        if (key is Keys.Back or Keys.Delete && mods == Keys.None) { Value = new Hotkey(); return; }
        if (key is Keys.ShiftKey or Keys.ControlKey or Keys.Menu or Keys.LWin or Keys.RWin or Keys.LShiftKey or Keys.RShiftKey
            or Keys.LControlKey or Keys.RControlKey or Keys.LMenu or Keys.RMenu or Keys.None)
        {
            var parts = new List<string>();
            if (mods.HasFlag(Keys.Control)) parts.Add("Ctrl");
            if (mods.HasFlag(Keys.Alt)) parts.Add("Alt");
            if (mods.HasFlag(Keys.Shift)) parts.Add("Shift");
            if (mods.HasFlag(Keys.LWin)) parts.Add("Win");
            if (parts.Count > 0) Text = string.Join(" + ", parts) + " + …";
            return;
        }
        Value = new Hotkey { Key = key, Modifiers = mods };
    }

    private static Keys CurrentModifiers()
    {
        static bool Down(int vk) => (GetKeyState(vk) & 0x8000) != 0;
        var m = Keys.None;
        if (Down(0x11)) m |= Keys.Control;
        if (Down(0x12)) m |= Keys.Alt;
        if (Down(0x10)) m |= Keys.Shift;
        if (Down(0x5B) || Down(0x5C)) m |= Keys.LWin;
        return m;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetKeyState(int vk);
}
