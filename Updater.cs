using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace FrameBurst;

public sealed record UpdateInfo(Version Version, string PageUrl, string? InstallerUrl, string? Sha256, string Notes);

/// <summary>
/// Checks the GitHub releases of LunoviaVR/FrameBurst for a newer version and installs it with the release's
/// Inno Setup installer (which closes FrameBurst, replaces the files and starts it again).
/// </summary>
internal static class Updater
{
    public const string Repo = "LunoviaVR/FrameBurst";
    public const string ReleasesPage = $"https://github.com/{Repo}/releases";

    public static Version Current { get; } = Normalize(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0));

    /// <summary>True when this copy was installed by the setup program (not a copy run from a build folder).</summary>
    public static bool IsInstalled => File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("FrameBurst", Current.ToString()));
        return http;
    }

    /// <summary>Returns the latest release if it is newer than this build, otherwise null.</summary>
    public static async Task<UpdateInfo?> CheckAsync()
    {
        using var http = CreateClient();
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var resp = await http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest");
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // no releases yet
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;
        version = Normalize(version);
        Log.Write($"update check: latest {version}, running {Current}");
        if (version <= Current) return null;

        string? url = null, sha = null;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            string name = a.GetProperty("name").GetString() ?? "";
            if (!name.StartsWith("FrameBurst-Setup", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            url = a.GetProperty("browser_download_url").GetString();
            if (a.TryGetProperty("digest", out var d) && d.GetString() is { } digest && digest.StartsWith("sha256:"))
                sha = digest["sha256:".Length..];
            break;
        }
        return new UpdateInfo(version, root.GetProperty("html_url").GetString() ?? ReleasesPage, url, sha,
            root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "");
    }

    /// <summary>Downloads the installer, checks its SHA-256 against GitHub's, and starts it. The caller should exit.</summary>
    public static async Task InstallAsync(UpdateInfo update)
    {
        if (update.InstallerUrl == null) throw new InvalidOperationException("This release has no installer attached.");
        var uri = new Uri(update.InstallerUrl);
        if (uri.Scheme != Uri.UriSchemeHttps || !(uri.Host == "github.com" || uri.Host.EndsWith(".githubusercontent.com")))
            throw new InvalidOperationException($"Refusing to download the installer from {uri.Host}.");

        string path = Path.Combine(Path.GetTempPath(), $"FrameBurst-Setup-{update.Version}.exe");
        using (var http = CreateClient())
        {
            http.Timeout = TimeSpan.FromMinutes(10);
            await using var src = await http.GetStreamAsync(uri);
            await using var dst = File.Create(path);
            await src.CopyToAsync(dst);
        }

        if (update.Sha256 != null)
        {
            await using var f = File.OpenRead(path);
            string actual = Convert.ToHexString(await SHA256.HashDataAsync(f));
            if (!actual.Equals(update.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(path);
                throw new InvalidOperationException("The downloaded installer is corrupt (checksum mismatch). Try again later.");
            }
        }

        Log.Write($"starting installer {path}");
        // The installer reuses the previous install folder and mode, closes FrameBurst and restarts it when done.
        Process.Start(new ProcessStartInfo(path, "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS") { UseShellExecute = true });
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));
}
