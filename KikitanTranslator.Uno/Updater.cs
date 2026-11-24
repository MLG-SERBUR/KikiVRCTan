// FORK (feed-overlay): simple self-updater for the portable distribution.
// Compares a version.txt on the feed-overlay-latest release against the local
// one; on mismatch downloads KikiVRCTan-portable.zip, then applies it with a
// batch script that copies the new files over after this process exits
// (config.json is preserved). The user clicks "Restart to update" when ready.

using System.Diagnostics;
using System.IO.Compression;
using Serilog;

namespace KikitanTranslator.Uno;

public sealed class Updater
{
    private const string ReleaseBase = "https://github.com/MLG-SERBUR/kikitan-translator/releases/download/feed-overlay-latest";
    private const string ZipName = "KikiVRCTan-portable.zip";
    private const string AppExe = "KikitanTranslator.exe";

    public event Action<string>? StatusChanged;
    public event Action? UpdateReady;

    private string? _downloadedZip;

    public async Task CheckAsync()
    {
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("KikiVRCTan-updater");

            var local = LocalVersion();
            var remote = (await http.GetStringAsync($"{ReleaseBase}/version.txt")).Trim();
            if (string.IsNullOrEmpty(remote) || remote == local)
                return; // up to date (or no version info); stay silent

            StatusChanged?.Invoke("Downloading update...");
            var zipPath = Path.Combine(Path.GetTempPath(), ZipName);
            using (var resp = await http.GetAsync($"{ReleaseBase}/{ZipName}", HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                await using var fs = File.Create(zipPath);
                await resp.Content.CopyToAsync(fs);
            }

            _downloadedZip = zipPath;
            UpdateReady?.Invoke();
            StatusChanged?.Invoke("Update downloaded");
        }
        catch (Exception e)
        {
            Log.Debug(e, "[FORK] Update check failed");
        }
    }

    public void ApplyAndRestart()
    {
        if (_downloadedZip == null || !File.Exists(_downloadedZip)) return;

        var appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var stageDir = Path.Combine(Path.GetTempPath(), "KikiVRCTan-update");
        if (Directory.Exists(stageDir)) Directory.Delete(stageDir, true);
        ZipFile.ExtractToDirectory(_downloadedZip, stageDir);

        var bat = Path.Combine(Path.GetTempPath(), "KikiVRCTan-apply-update.bat");
        File.WriteAllText(bat,
            "@echo off\r\n" +
            "timeout /t 3 /nobreak >nul\r\n" +
            $"robocopy \"{stageDir}\" \"{appDir}\" /E /XF config.json >nul\r\n" +
            // FORK: one-time rename cleanup — the exe used to be
            // KikitanTranslator.WinForms.exe; delete the stale copy so it
            // can't be launched by mistake after the update.
            $"del \"{Path.Combine(appDir, "KikitanTranslator.WinForms.exe")}\" >nul 2>&1\r\n" +
            $"start \"\" \"{Path.Combine(appDir, AppExe)}\"\r\n" +
            $"rmdir /s /q \"{stageDir}\"\r\n" +
            "del \"%~f0\"\r\n");

        Process.Start(new ProcessStartInfo(bat) { UseShellExecute = true, CreateNoWindow = true });
        Environment.Exit(0);
    }

    // FORK: local version (full SHA from version.txt, or "unknown").
    // MainForm shows the short form in the title bar.
    internal static string LocalVersion()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "version.txt");
        return File.Exists(path) ? File.ReadAllText(path).Trim() : "unknown";
    }
}
