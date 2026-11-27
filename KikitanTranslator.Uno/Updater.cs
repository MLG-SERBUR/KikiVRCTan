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
    private const string ReleaseBase = "https://github.com/MLG-SERBUR/KikiVRCTan/releases/download/feed-overlay-latest";
    private const string ZipName = "KikiVRCTan-portable.zip";
    private const string AppExe = "KikiVRCTan.exe";

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
        DeleteStaleFiles(appDir, stageDir);

        var bat = Path.Combine(Path.GetTempPath(), "KikiVRCTan-apply-update.bat");
        File.WriteAllText(bat,
            "@echo off\r\n" +
            "timeout /t 3 /nobreak >nul\r\n" +
            $"robocopy \"{stageDir}\" \"{appDir}\" /E /XF config.json >nul\r\n" +
            // FORK: one-time rename cleanup — the exe used to be
            // KikitanTranslator.exe; delete the stale copy so it
            // can't be launched by mistake after the update.
            $"del \"{Path.Combine(appDir, "KikitanTranslator.exe")}\" >nul 2>&1\r\n" +
            $"start \"\" \"{Path.Combine(appDir, AppExe)}\"\r\n" +
            $"rmdir /s /q \"{stageDir}\"\r\n" +
            "del \"%~f0\"\r\n");

        Process.Start(new ProcessStartInfo(bat) { UseShellExecute = true, CreateNoWindow = true });
        Environment.Exit(0);
    }

    // FORK: manifest cleanup — delete files the previous release shipped
    // but this one doesn't (stale files otherwise linger forever, e.g. the
    // overlay exe at the root after it moved to overlay/). Only paths listed
    // in the old manifest are ever deleted, so user files in the folder are
    // never touched. Skipped when either manifest is missing (installs that
    // predate manifests get no cleanup — only the one-time item below).
    private static void DeleteStaleFiles(string appDir, string stageDir)
    {
        var oldManifest = Path.Combine(appDir, "manifest.txt");
        var newManifest = Path.Combine(stageDir, "manifest.txt");
        if (File.Exists(oldManifest) && File.Exists(newManifest))
        {
            var fresh = new HashSet<string>(
                File.ReadAllLines(newManifest), StringComparer.OrdinalIgnoreCase);
            foreach (var entry in File.ReadAllLines(oldManifest))
            {
                var rel = entry.Trim().Replace('\\', '/');
                if (rel.Length == 0 || rel == "manifest.txt" || rel == "config.json"
                    || fresh.Contains(rel))
                    continue;

                var full = Path.GetFullPath(Path.Combine(
                    appDir, rel.Replace('/', Path.DirectorySeparatorChar)));
                if (!full.StartsWith(appDir + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                    continue; // entry escapes the app folder; never delete it

                try
                {
                    if (File.Exists(full)) File.Delete(full);
                }
                catch (Exception e)
                {
                    Log.Debug(e, "[FORK] Could not delete stale shipped file {File}", rel);
                }
            }
        }

        // FORK: one-time cleanup for the pre-manifest layout — the overlay
        // used to ship beside the exe; installs updated before manifests
        // existed still carry the stale root copy.
        var staleRootOverlay = Path.Combine(appDir, "KikitanTranslator.Overlay.exe");
        var newOverlay = Path.Combine(stageDir, "overlay", "KikiVRCTan.Overlay.exe");
        if (File.Exists(newOverlay) && File.Exists(staleRootOverlay))
        {
            try { File.Delete(staleRootOverlay); }
            catch (Exception e)
            {
                Log.Debug(e, "[FORK] Could not delete stale root overlay exe");
            }
        }
    }

    // FORK: local version (full SHA from version.txt, or "unknown").
    // MainForm shows the short form in the title bar.
    internal static string LocalVersion()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "version.txt");
        return File.Exists(path) ? File.ReadAllText(path).Trim() : "unknown";
    }
}
