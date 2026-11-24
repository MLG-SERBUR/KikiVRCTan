// FORK (feed-overlay): bundled flat 4:3 flag images for the UI language
// button and menu. Windows has no flag glyphs (Segoe UI Emoji renders the
// flag emoji as the two-letter code), so the pictures ship as loose files
// under Assets/flags/ and load at runtime via ms-appx URIs — no download
// at runtime. Images are flagcdn.com renders of lipis/flag-icons (MIT, see
// Assets/flags/LICENSE.txt); the w80 files stay sharp at high UI scale.
// One cached ImageSource per language; the XAML Image control stretches
// Uniform, so the flag keeps its real aspect ratio (US is 80x42, the rest
// 80x53) with no manual scaling.

using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace KikitanTranslator.Uno;

public static class FlagImages
{
    // UI language code -> flag file (country code). Arabic uses the Saudi
    // flag as the conventional stand-in; no single flag exists for a
    // language.
    private static readonly Dictionary<string, string> FileFor = new()
    {
        ["en"] = "us",
        ["ko"] = "kr",
        ["ja"] = "jp",
        ["zh"] = "cn",
        ["ar"] = "sa",
        ["es"] = "es",
        ["ru"] = "ru",
    };

    private static readonly Dictionary<string, ImageSource> _cache = new();

    // Returns null when the language has no flag.
    public static ImageSource? Get(string code)
    {
        if (!FileFor.TryGetValue(code, out var file)) return null;
        if (_cache.TryGetValue(file, out var cached)) return cached;
        var source = new BitmapImage(new Uri($"ms-appx:///Assets/flags/{file}.png"));
        _cache[file] = source;
        return source;
    }
}
