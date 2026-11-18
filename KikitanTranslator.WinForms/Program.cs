// FORK (feed-overlay): native WinForms entry point. No webview.

using KikitanTranslator.Base;
using KikitanTranslator.Utility;

namespace KikitanTranslator.WinForms;

static class Program
{
    [STAThread]
    static void Main()
    {
        // FORK: one instance only. Two hosts would both send OSC (double
        // chatbox messages, double typing indicator) and both drive the
        // feed/overlay.
        using var singleInstance = new Mutex(true, "KikiVRCTan-single-instance", out bool createdNew);
        if (!createdNew) return;

        Console.OutputEncoding = System.Text.Encoding.UTF8;

        // FORK: keep config.json and logs next to the executable, not %APPDATA%.
        // Migrates an existing config from the legacy location on first run.
        AppConfig.UseExecutableDirectory = true;

        Logger.Initialize();
        AppConfig.Load();

        ApplicationConfiguration.Initialize();
        using var engine = new AppEngine();
        engine.Initialize();
        Application.Run(new MainForm(engine));
    }
}
