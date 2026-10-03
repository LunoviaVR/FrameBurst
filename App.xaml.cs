using FrameBurst.UI;
using Microsoft.UI.Xaml;

namespace FrameBurst;

/// <summary>WinUI 3 application: owns the message loop; the tray, hotkeys and capture overlays run on it too.</summary>
public partial class App : Microsoft.UI.Xaml.Application
{
    private TrayApp? _tray;

    /// <summary>Set by `--selftest --settingsui`: render the WinUI windows to screenshots instead of starting the tray.</summary>
    internal static string? SettingsShotDir { get; set; }

    public App()
    {
        InitializeComponent();
        // A tray app has no main window: stay alive until Exit is chosen.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
        UnhandledException += (_, e) =>
        {
            Log.Write("unhandled: " + e.Exception);
            e.Handled = true;
            MessageBox.Show(e.Exception.ToString(), "FrameBurst error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (SettingsShotDir != null) SelfTest.SettingsShots(SettingsShotDir);
        else _tray = new TrayApp();
    }
}
