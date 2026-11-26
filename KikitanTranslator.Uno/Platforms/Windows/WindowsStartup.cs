using KikitanTranslator.Base;
using KikitanTranslator.Utility;

namespace KikitanTranslator.Uno;

internal static class WindowsStartup
{
    private static Mutex? _singleInstance;

    public static void Initialize()
    {
        _singleInstance = new Mutex(true, "KikiVRCTan-single-instance", out bool createdNew);
        if (!createdNew)
        {
            Environment.Exit(0);
            return;
        }

        Console.OutputEncoding = System.Text.Encoding.UTF8;
        AppConfig.UseExecutableDirectory = true;

        Logger.Initialize();
        var exeConfig = Path.Join(AppConfig.GetAppFolder(), "config.json");
        var legacyConfig = Path.Join(AppConfig.GetLegacyAppFolder(), "config.json");
        bool firstRun = !File.Exists(exeConfig) && !File.Exists(legacyConfig);
        AppConfig.Load();
        if (firstRun)
            AppConfig.ConfigObject.SourceLanguage = UiLanguage.MapToSource(UiLanguage.DetectSystem());

        App.InitializeLogging();

        var engine = new AppEngine();
        engine.Initialize();
        App.Engine = engine;
    }
}
