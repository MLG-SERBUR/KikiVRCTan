// FORK (feed-overlay): native Uno entry point. No webview.

using KikitanTranslator.Base;
using KikitanTranslator.Utility;
using Uno.UI.Hosting;

namespace KikitanTranslator.Uno;

internal class Program
{
    // Held for the process lifetime: the single-instance mutex must outlive
    // Main's locals (host.Run blocks until the app exits, but a static
    // makes the lifetime explicit).
    private static Mutex? _singleInstance;

    [STAThread]
    public static void Main(string[] args)
    {
        // FORK: one instance only. Two hosts would both send OSC (double
        // chatbox messages, double typing indicator) and both drive the
        // feed/overlay.
        _singleInstance = new Mutex(true, "KikiVRCTan-single-instance", out bool createdNew);
        if (!createdNew) return;

        Console.OutputEncoding = System.Text.Encoding.UTF8;

        // FORK: keep config.json and logs next to the executable, not %APPDATA%.
        // Migrates an existing config from the legacy location on first run.
        AppConfig.UseExecutableDirectory = true;

        Logger.Initialize();
        // FORK: true first run — no config next to the exe and none to
        // migrate from the legacy location. Default the source language to
        // the user's Windows language (Spanish maps to es-ES; the Source
        // list has no plain "es"). Existing configs keep their language.
        var exeConfig = Path.Join(AppConfig.GetAppFolder(), "config.json");
        var legacyConfig = Path.Join(AppConfig.GetLegacyAppFolder(), "config.json");
        bool firstRun = !File.Exists(exeConfig) && !File.Exists(legacyConfig);
        AppConfig.Load();
        if (firstRun)
            AppConfig.ConfigObject.SourceLanguage = UiLanguage.MapToSource(UiLanguage.DetectSystem());

        App.InitializeLogging();

        // Engine lives for the app lifetime; disposed when the host exits.
        using var engine = new AppEngine();
        engine.Initialize();
        App.Engine = engine;

        var host = UnoPlatformHostBuilder.Create()
            .App(() => new App())
            .UseWin32()
            .Build();

        host.Run();
    }
}
