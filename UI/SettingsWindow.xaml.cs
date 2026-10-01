using System.Diagnostics;
using FrameBurst.Win32;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace FrameBurst.UI;

public sealed partial class SettingsWindow : Microsoft.UI.Xaml.Window
{
    private readonly Settings _s;
    private readonly Action _exitForUpdate;
    private UpdateInfo? _update;
    private bool _updateBusy;

    private const string TipUrl = "https://cash.app/$LunoviaVR";

    /// <summary>Raised with the edited copy when the user presses Save and it was written to disk.</summary>
    public event Action<Settings>? Saved;

    /// <param name="exitForUpdate">Called once the update installer has started; it should exit FrameBurst.</param>
    public SettingsWindow(Settings settings, Action exitForUpdate)
    {
        _s = settings.Clone();
        _exitForUpdate = exitForUpdate;
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "FrameBurst.ico");
        AppWindow.SetIcon(iconPath);
        var icon = new BitmapImage(new Uri(iconPath));
        TitleIcon.Source = icon;
        AboutIcon.Source = icon;

        if (AppWindow.Presenter is OverlappedPresenter p) { p.IsMinimizable = false; p.IsMaximizable = false; }
        double scale = Native.GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        var size = new Windows.Graphics.SizeInt32((int)(900 * scale), (int)(680 * scale));
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
            area.X + (area.Width - size.Width) / 2, area.Y + (area.Height - size.Height) / 2, size.Width, size.Height));

        VersionText.Text = $"Version {Updater.Current}" + (Updater.IsInstalled ? "" : " (portable build)");
        ReleaseNotes.NavigateUri = new Uri(Updater.ReleasesPage);
        SourceLink.NavigateUri = new Uri($"https://github.com/{Updater.Repo}");
        LoadValues();
    }

    private void LoadValues()
    {
        HkRegion.Value = _s.RegionHotkey; HkFull.Value = _s.FullscreenHotkey; HkMonitor.Value = _s.MonitorHotkey; HkWindow.Value = _s.WindowHotkey;
        Delay.Value = Math.Clamp(_s.CaptureDelayMs, 0, 10000);
        CursorToggle.IsOn = _s.CaptureCursor;
        Folder.Text = _s.OutputFolder; Pattern.Text = _s.FileNamePattern;
        DateFolderToggle.IsOn = _s.UseDateSubfolder; DateFolderPattern.Text = _s.DateSubfolderPattern;
        SaveToggle.IsOn = _s.SaveToFile; ClipToggle.IsOn = _s.CopyToClipboard; NotifyToggle.IsOn = _s.ShowNotification;
        Png.SelectedIndex = (int)_s.PngCompression;
        HdrToggle.IsOn = _s.SaveHdr;
        AutoUpdate.IsOn = _s.CheckForUpdates;
    }

    private void Commit()
    {
        _s.RegionHotkey = HkRegion.Value; _s.FullscreenHotkey = HkFull.Value; _s.MonitorHotkey = HkMonitor.Value; _s.WindowHotkey = HkWindow.Value;
        _s.CaptureDelayMs = double.IsNaN(Delay.Value) ? 0 : (int)Math.Clamp(Delay.Value, 0, 10000);
        _s.CaptureCursor = CursorToggle.IsOn;
        _s.OutputFolder = Folder.Text.Trim(); _s.FileNamePattern = Pattern.Text.Trim();
        _s.UseDateSubfolder = DateFolderToggle.IsOn; _s.DateSubfolderPattern = DateFolderPattern.Text.Trim();
        _s.SaveToFile = SaveToggle.IsOn; _s.CopyToClipboard = ClipToggle.IsOn; _s.ShowNotification = NotifyToggle.IsOn;
        _s.PngCompression = (PngCompression)Math.Max(0, Png.SelectedIndex);
        _s.SaveHdr = HdrToggle.IsOn;
        _s.CheckForUpdates = AutoUpdate.IsOn;
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        string tag = (args.SelectedItem as NavigationViewItem)?.Tag as string ?? "Capture";
        CapturePage.Visibility = tag == "Capture" ? Visibility.Visible : Visibility.Collapsed;
        OutputPage.Visibility = tag == "Output" ? Visibility.Visible : Visibility.Collapsed;
        AboutPage.Visibility = tag == "About" ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker { SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        try
        {
            var folder = await picker.PickSingleFolderAsync();
            if (folder != null) Folder.Text = folder.Path;
        }
        catch (Exception ex) { Error.Text = ex.Message; }
    }

    /// <summary>Opens the About page (used by the tray's "Check for updates" item) and optionally starts a check.</summary>
    public void ShowAbout(bool checkNow)
    {
        Nav.SelectedItem = Nav.MenuItems.OfType<NavigationViewItem>().First(i => (string)i.Tag == "About");
        if (checkNow) CheckNow_Click(this, new RoutedEventArgs());
    }

    private void SetUpdateState(string title, string status, int glyph, bool busy)
    {
        UpdateTitle.Text = title;
        UpdateStatus.Text = status;
        UpdateGlyph.Glyph = char.ConvertFromUtf32(glyph);
        UpdateRing.IsActive = busy;
        UpdateRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void CheckNow_Click(object sender, RoutedEventArgs e)
    {
        if (_updateBusy) return;
        _updateBusy = true;
        CheckButton.IsEnabled = false;
        InstallButton.Visibility = Visibility.Collapsed;
        SetUpdateState("Checking for updates…", "Contacting GitHub", 0xE895, busy: true);
        try
        {
            _update = await Updater.CheckAsync();
            if (_update == null)
            {
                SetUpdateState("You're up to date", $"FrameBurst {Updater.Current} is the latest version", 0xE930, busy: false);
            }
            else
            {
                bool canInstall = Updater.IsInstalled && _update.InstallerUrl != null;
                SetUpdateState($"FrameBurst {_update.Version} is available",
                    canInstall ? "FrameBurst will restart to finish installing" : "This copy wasn't installed with setup, so download it from GitHub",
                    0xE896, busy: false);
                InstallButton.Content = canInstall ? "Download and install" : "Open download page";
                InstallButton.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            Log.Write("update check failed: " + ex.Message);
            SetUpdateState("Couldn't check for updates", ex.Message, 0xE7BA, busy: false);
        }
        finally
        {
            _updateBusy = false;
            CheckButton.IsEnabled = true;
        }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_update is not { } update || _updateBusy) return;
        if (!Updater.IsInstalled || update.InstallerUrl == null)
        {
            Process.Start(new ProcessStartInfo(update.PageUrl) { UseShellExecute = true });
            return;
        }

        _updateBusy = true;
        CheckButton.IsEnabled = InstallButton.IsEnabled = false;
        DownloadProgress.Visibility = Visibility.Visible;
        DownloadProgress.IsIndeterminate = true;
        SetUpdateState($"Downloading FrameBurst {update.Version}…", "FrameBurst will restart when it's done", 0xE896, busy: false);
        var progress = new Progress<double>(f =>
        {
            DownloadProgress.IsIndeterminate = false;
            DownloadProgress.Value = f * 100;
            UpdateStatus.Text = $"{f:P0} downloaded. FrameBurst will restart when it's done";
        });
        try
        {
            await Updater.InstallAsync(update, progress);
            SetUpdateState("Installing…", "FrameBurst is restarting", 0xE896, busy: true);
            _exitForUpdate(); // the installer replaces the files and restarts FrameBurst
        }
        catch (Exception ex)
        {
            Log.Write("update failed: " + ex);
            SetUpdateState("The update failed", ex.Message, 0xE7BA, busy: false);
            DownloadProgress.Visibility = Visibility.Collapsed;
            _updateBusy = false;
            CheckButton.IsEnabled = InstallButton.IsEnabled = true;
        }
    }

    private void Tip_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(TipUrl) { UseShellExecute = true });

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Commit();
        try { _s.Save(); }
        catch (Exception ex) { Error.Text = "Could not save settings: " + ex.Message; return; }
        Saved?.Invoke(_s);
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Brings the window to the front (it may be behind other apps when reopened from the tray).</summary>
    public void BringToFront()
    {
        Activate();
        Native.SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }
}
