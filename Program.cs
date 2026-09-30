using GpuShot.UI;

namespace GpuShot;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--selftest")
            return SelfTest.Run(args.Skip(1).ToArray());

        using var mutex = new Mutex(true, @"Local\GpuShot.SingleInstance", out bool first);
        if (!first)
        {
            MessageBox.Show("GpuShot is already running (see the system tray).", "GpuShot", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
            MessageBox.Show(e.Exception.ToString(), "GpuShot error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        Application.Run(new TrayApp());
        return 0;
    }
}
