using KikitanTranslator.Base;
using KikitanTranslator.Utility;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinRT;

namespace KikitanTranslator.Uno;

internal class Program
{
    private static Mutex? _singleInstance;

    [STAThread]
    public static void Main(string[] args)
    {
        _singleInstance = new Mutex(true, "KikiVRCTan-single-instance", out bool createdNew);
        if (!createdNew) return;

        WinRT.ComWrappersSupport.InitializeComWrappers();
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

        using var engine = new AppEngine();
        engine.Initialize();
        App.Engine = engine;

        Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
    }
}
