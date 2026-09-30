namespace FrameBurst.UI;

/// <summary>Text box that records a key combination. Backspace/Delete clears it.</summary>
internal sealed class HotkeyBox : TextBox
{
    private Hotkey _value = new();

    public HotkeyBox()
    {
        ReadOnly = true;
        BackColor = SystemColors.Window;
        ShortcutsEnabled = false;
        Cursor = Cursors.Hand;
    }

    public Hotkey Value
    {
        get => _value;
        set { _value = value; Text = value.ToString(); }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // Capture everything, including Tab / Alt combos and PrintScreen.
        HandleKey(keyData);
        return true;
    }

    protected override bool IsInputKey(Keys keyData) => true;

    protected override void OnKeyUp(KeyEventArgs e)
    {
        // PrintScreen only arrives as KeyUp on many systems.
        if (e.KeyCode == Keys.Snapshot) HandleKey(e.KeyData);
        e.Handled = true;
    }

    private void HandleKey(Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        var mods = keyData & Keys.Modifiers;
        if (key is Keys.Back or Keys.Delete && mods == Keys.None) { Value = new Hotkey(); return; }
        if (key is Keys.ShiftKey or Keys.ControlKey or Keys.Menu or Keys.LWin or Keys.RWin or Keys.None)
        {
            var parts = new List<string>();
            if (mods.HasFlag(Keys.Control)) parts.Add("Ctrl");
            if (mods.HasFlag(Keys.Alt)) parts.Add("Alt");
            if (mods.HasFlag(Keys.Shift)) parts.Add("Shift");
            if (IsWinDown()) parts.Add("Win");
            if (parts.Count > 0) Text = string.Join(" + ", parts) + " + …";
            return;
        }
        if (IsWinDown()) mods |= Keys.LWin;
        Value = new Hotkey { Key = key, Modifiers = mods };
    }

    private static bool IsWinDown() => (GetKeyState(0x5B) & 0x8000) != 0 || (GetKeyState(0x5C) & 0x8000) != 0;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetKeyState(int vk);

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Text = _value.ToString();
    }
}
