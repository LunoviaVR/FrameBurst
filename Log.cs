namespace GpuShot;

/// <summary>Append-only diagnostic log at %AppData%\GpuShot\gpushot.log (trimmed when it grows past 1 MB).</summary>
internal static class Log
{
    private static readonly object Gate = new();
    public static string PathName => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GpuShot", "gpushot.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathName)!);
                var fi = new FileInfo(PathName);
                if (fi.Exists && fi.Length > 1_000_000) fi.Delete();
                File.AppendAllText(PathName, $"{DateTime.Now:HH:mm:ss.fff} [{Environment.CurrentManagedThreadId}] {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never break a capture */ }
    }
}
