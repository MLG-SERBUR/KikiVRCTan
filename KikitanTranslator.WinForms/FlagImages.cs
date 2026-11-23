// FORK (feed-overlay): bundled flat 4:3 flag images for the UI language
// button and menu. Windows has no flag glyphs (Segoe UI Emoji renders the
// flag emoji as the two-letter code), so the pictures ship in the exe as
// embedded resources — no extra files in the portable zip, no download at
// runtime. Images are flagcdn.com renders of lipis/flag-icons (MIT, see
// Resources/flags/LICENSE.txt); the w80 files stay sharp at high UI scale.
// One cached bitmap per (language, size); scaled copies preserve the
// flag's real aspect ratio (US is 80x42, the rest 80x53).

using System.Drawing;

namespace KikitanTranslator.WinForms;

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

    private static readonly Dictionary<string, Bitmap> _source = new();
    private static readonly Dictionary<(string, int, int), Bitmap> _scaled = new();

    // Scaled to exactly maxWidth x maxHeight: the flag keeps its real
    // aspect ratio, centered on a transparent pad, so buttons and menus
    // draw it as-is instead of stretching it.
    // Returns null when the language has no flag or the resource is missing.
    public static Bitmap? Get(string code, int maxWidth, int maxHeight)
    {
        if (!FileFor.TryGetValue(code, out var file)) return null;
        if (!_source.TryGetValue(file, out var src))
        {
            src = Load(file);
            if (src == null) return null;
            _source[file] = src;
        }
        var key = (code, maxWidth, maxHeight);
        if (_scaled.TryGetValue(key, out var scaled)) return scaled;
        var scale = Math.Min((double)maxWidth / src.Width, (double)maxHeight / src.Height);
        var w = Math.Max(1, (int)(src.Width * scale));
        var h = Math.Max(1, (int)(src.Height * scale));
        scaled = new Bitmap(maxWidth, maxHeight);
        using (var g = Graphics.FromImage(scaled))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(src, (maxWidth - w) / 2, (maxHeight - h) / 2, w, h);
        }
        _scaled[key] = scaled;
        return scaled;
    }

    private static Bitmap? Load(string file)
    {
        var asm = typeof(FlagImages).Assembly;
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith($".flags.{file}.png", StringComparison.OrdinalIgnoreCase));
        if (name == null) return null;
        using var stream = asm.GetManifestResourceStream(name);
        return stream == null ? null : new Bitmap(stream);
    }
}
