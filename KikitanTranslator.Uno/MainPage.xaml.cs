// FORK (feed-overlay): native Uno UI. Replaces the WinForms host.
// All settings bind to the shared AppConfig (same config.json as upstream).
// Unlike WinForms, the UI scale and UI language apply live without a
// layout rebuild: the scale is a root ScaleTransform, the language is
// re-applied by ApplyUiLanguage.

using System.Collections.ObjectModel;
using KikitanTranslator.Utility;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace KikitanTranslator.Uno;

public sealed class LeftWrapPanel : Panel
{
    public static readonly DependencyProperty ItemSpacingProperty = DependencyProperty.Register(
        nameof(ItemSpacing), typeof(double), typeof(LeftWrapPanel), new PropertyMetadata(0d));
    public static readonly DependencyProperty LineSpacingProperty = DependencyProperty.Register(
        nameof(LineSpacing), typeof(double), typeof(LeftWrapPanel), new PropertyMetadata(0d));

    public double ItemSpacing
    {
        get => (double)GetValue(ItemSpacingProperty);
        set => SetValue(ItemSpacingProperty, value);
    }

    public double LineSpacing
    {
        get => (double)GetValue(LineSpacingProperty);
        set => SetValue(LineSpacingProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var lineWidth = 0d;
        var lineHeight = 0d;
        var desiredWidth = 0d;
        var desiredHeight = 0d;

        foreach (var child in Children)
        {
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var childSize = child.DesiredSize;
            var nextWidth = lineWidth == 0 ? childSize.Width : lineWidth + ItemSpacing + childSize.Width;
            if (lineWidth > 0 && nextWidth > availableSize.Width)
            {
                desiredWidth = Math.Max(desiredWidth, lineWidth);
                desiredHeight += lineHeight + LineSpacing;
                lineWidth = 0;
                lineHeight = 0;
                nextWidth = childSize.Width;
            }

            lineWidth = nextWidth;
            lineHeight = Math.Max(lineHeight, childSize.Height);
        }

        desiredWidth = Math.Max(desiredWidth, lineWidth);
        desiredHeight += lineHeight;
        return new Size(Math.Min(desiredWidth, availableSize.Width), desiredHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var x = 0d;
        var y = 0d;
        var lineHeight = 0d;

        foreach (var child in Children)
        {
            var childSize = child.DesiredSize;
            var nextX = x == 0 ? childSize.Width : x + ItemSpacing + childSize.Width;
            if (x > 0 && nextX > finalSize.Width)
            {
                y += lineHeight + LineSpacing;
                x = 0;
                lineHeight = 0;
                nextX = childSize.Width;
            }

            child.Arrange(new Rect(x, y, childSize.Width, childSize.Height));
            x = nextX;
            lineHeight = Math.Max(lineHeight, childSize.Height);
        }

        return finalSize;
    }
}

// FORK: one feed entry as the virtualized list sees it. Transcription and
// translation are separate lines (like VRCT); the header carries the
// timestamp + speaker (You / Incoming). TranscriptionVisibility is fixed
// at creation from the "Show transcription" setting; toggling it rebuilds
// the list (RepopulateFeed).
public sealed class FeedItemView
{
    public required string Header { get; init; }
    public required string Transcription { get; init; }
    public required string MainText { get; init; }
    public required Brush Accent { get; init; }
    public required Visibility TranscriptionVisibility { get; init; }
}

public sealed partial class MainPage : Page
{
    // FORK: feed accents — incoming (desktop) blue like the overlay,
    // own mic green.
    private static readonly SolidColorBrush IncomingAccent =
        new(Windows.UI.Color.FromArgb(0xFF, 0x4C, 0xC2, 0xFF));
    private static readonly SolidColorBrush OwnAccent =
        new(Windows.UI.Color.FromArgb(0xFF, 0x6F, 0xCF, 0x97));

    private readonly AppEngine _engine;
    private readonly ConfigObject _cfg;
    private readonly ObservableCollection<FeedItemView> _feedItems = new();
    private readonly DispatcherTimer _typingTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _sendClearTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Updater _updater = new();
    private readonly List<(Button Button, string Code)> _targetQuickButtons = [];
    private readonly List<ComboBox> _sourceCombos = [];
    private string _uiLang = "en";
    private string _lastStatus = "Stopped";
    private double _uiFactor = 1;
    private double _settingsWidth = 320;
    private double _lastExpandedSettingsWidth = 320;
    private bool _feedCollapsed = false;
    // FORK: the divider button is both a drag handle and a tap target.
    private bool _collapsePressed = false;
    private bool _collapseDragging = false;
    private bool _collapseStartedCollapsed = false;
    private double _collapseStartX;
    private double _collapseStartWidth;
    private bool _loading = true;
    private bool _stickToBottom = true;
    private MenuFlyout? _langMenu;

    private record LangOption(string Code, string Name);

    public MainPage()
    {
        _engine = App.Engine ?? throw new InvalidOperationException("AppEngine not initialized");
        _cfg = AppConfig.ConfigObject;
        this.InitializeComponent();
        UpdateSettingsContentWidth();

        _typingTimer.Tick += (_, _) => StopTypingIndicator();
        _sendClearTimer.Tick += (_, _) => ResendTypingFalse();
        FeedList.ItemsSource = _feedItems;
        FeedScroll.SizeChanged += (_, _) => UpdateFeedViewportWidth();
        FeedScroll.ViewChanged += (_, _) =>
            _stickToBottom = FeedScroll.VerticalOffset >= FeedScroll.ScrollableHeight - 4;
        SettingsPane.SizeChanged += (_, _) => UpdateSettingsContentWidth();
        ViewportGrid.SizeChanged += (_, _) =>
        {
            UpdateRootSize();
            UpdateFeedViewportWidth();
        };
        this.Unloaded += (_, _) =>
        {
            StopTypingIndicator();
            _typingTimer.Stop();
            _sendClearTimer.Stop();
        };

        _uiLang = UiLanguage.Resolve(_cfg.UiLanguage);
        _targetQuickButtons.Add((QuickKo, "ko"));
        _targetQuickButtons.Add((QuickJa, "ja"));
        _targetQuickButtons.Add((QuickZh, "zh"));
        _targetQuickButtons.Add((QuickAr, "ar"));
        _targetQuickButtons.Add((QuickEs, "es"));
        _sourceCombos.Add(FromComboMain);
        _sourceCombos.Add(FromComboSettings);

        ApplyUiScale();
        UpdateRootSize();
        BuildLangMenu();
        WireControls();
        ApplyUiLanguage();
        LoadFromConfig();
        ApplyLogFont();
        RepopulateFeed();
        WireEngine();
        _loading = false;
    }

    private string T(string key) => UiLanguage.T(_uiLang, key);

    // FORK: status lines from the engine/updater arrive in English; show
    // the translated form for the known ones, pass the rest through (error
    // text stays English — it is diagnostic, not UI).
    private string TranslateStatus(string msg) => msg switch
    {
        "Stopped" or "Running" or "Translator unavailable: check API key"
            or "Translation failed" or "Downloading update..." or "Update downloaded" => T(msg),
        _ => msg,
    };

    private void ApplyUiScale()
    {
        _uiFactor = Math.Clamp(_cfg.UiScale, 80, 250) / 100.0;
        AppScale.ScaleX = _uiFactor;
        AppScale.ScaleY = _uiFactor;
        UpdateRootSize();
    }

    // FORK: the scaled grid is sized to the window divided by the scale, so
    // the scaled content exactly fills the window.
    private void UpdateRootSize()
    {
        if (ViewportGrid.ActualWidth <= 0 || ViewportGrid.ActualHeight <= 0) return;
        RootGrid.Width = ViewportGrid.ActualWidth / _uiFactor;
        RootGrid.Height = ViewportGrid.ActualHeight / _uiFactor;
        if (!_feedCollapsed)
        {
            _settingsWidth = Math.Clamp(_settingsWidth, GetMinSettingsWidth(), GetMaxSettingsWidth());
            SettingsColumn.Width = new GridLength(_settingsWidth);
        }
        UpdateSplitterAlignment();
    }

    private void UpdateFeedViewportWidth()
    {
        if (FeedScroll.ViewportWidth > 0)
            FeedList.Width = FeedScroll.ViewportWidth;
    }

    private void UpdateSettingsContentWidth()
    {
        var paneWidth = SettingsPane.ActualWidth > 0 ? SettingsPane.ActualWidth : _settingsWidth;
        ViewSelector.MaxWidth = paneWidth;
        MainScroll.MaxWidth = paneWidth;
        SettingsScroll.MaxWidth = paneWidth;
        ManualBox.MaxWidth = Math.Max(0, paneWidth - 16 - SendButton.Width - 6);
    }

    private void SplitterThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        // Dragging the strip re-opens a hidden feed. Keep its pane width at
        // the last expanded size for this gesture so the scaled root does not
        // reflow while the pointer is captured.
        if (_feedCollapsed)
        {
            SetFeedCollapsed(false);
            return;
        }
        _settingsWidth = Math.Clamp(_settingsWidth + e.HorizontalChange, GetMinSettingsWidth(), GetMaxSettingsWidth());
        RememberExpandedWidth();
        SettingsColumn.Width = new GridLength(_settingsWidth);
        UpdateSettingsContentWidth();
        UpdateSplitterAlignment();
    }

    // FORK: the divider button hides the FEED (the log), not the settings —
    // tap it and the settings take the whole window, tap again and the log
    // comes back. It is also the drag handle: pressing and moving it
    // resizes the settings column exactly like the strip does, so the
    // button never blocks dragging. A tap is a press+release with almost
    // no movement; a drag re-opens a hidden feed first, then resizes.
    private void CollapseButton_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        ((UIElement)sender).CapturePointer(e.Pointer);
        _collapsePressed = true;
        _collapseDragging = false;
        _collapseStartedCollapsed = _feedCollapsed;
        _collapseStartX = e.GetCurrentPoint(RootGrid).Position.X;
        _collapseStartWidth = _settingsWidth;
    }

    private void CollapseButton_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_collapsePressed) return;
        var dx = e.GetCurrentPoint(RootGrid).Position.X - _collapseStartX;
        if (!_collapseDragging && Math.Abs(dx) < 4) return;
        if (!_collapseDragging)
        {
            _collapseDragging = true;
            if (_collapseStartedCollapsed) SetFeedCollapsed(false);
        }
        // A drag from the collapsed edge is a restore gesture. Resizing in
        // the same gesture shifts the scaled content and can clip its left
        // edge, so leave the saved pane width alone.
        if (_collapseStartedCollapsed) return;
        _settingsWidth = Math.Clamp(_collapseStartWidth + dx, GetMinSettingsWidth(), GetMaxSettingsWidth());
        RememberExpandedWidth();
        SettingsColumn.Width = new GridLength(_settingsWidth);
        UpdateSettingsContentWidth();
        UpdateSplitterAlignment();
    }

    private void CollapseButton_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
        if (_collapsePressed && !_collapseDragging)
            SetFeedCollapsed(!_collapseStartedCollapsed);
        _collapsePressed = false;
        _collapseDragging = false;
    }

    private void CollapseButton_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
        _collapsePressed = false;
        _collapseDragging = false;
    }

    private void SetFeedCollapsed(bool collapsed)
    {
        if (collapsed)
            RememberExpandedWidth();
        else
            _settingsWidth = Math.Min(_lastExpandedSettingsWidth, GetMaxSettingsWidth());

        _feedCollapsed = collapsed;
        FeedScroll.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        FeedColumn.Width = collapsed ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        // Move the divider to the window edge when hiding the feed. The view
        // content keeps its splitter-sized maximum width so controls do not
        // stretch or clip across the expanded column.
        SettingsColumn.Width = collapsed
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(_settingsWidth);
        CollapseGlyph.Text = collapsed ? "‹" : "›";
        UpdateSplitterAlignment();
    }

    private double GetMaxSettingsWidth() => Math.Max(0, RootGrid.Width - SplitterColumn.Width.Value);

    private double GetMinSettingsWidth() => Math.Min(200, GetMaxSettingsWidth());

    private void RememberExpandedWidth()
    {
        if (_settingsWidth < GetMaxSettingsWidth() - 1)
            _lastExpandedSettingsWidth = _settingsWidth;
    }

    private void UpdateSplitterAlignment()
    {
        var atRightEdge = !_feedCollapsed && _settingsWidth >= GetMaxSettingsWidth() - 1;
        var alignment = _feedCollapsed || atRightEdge ? HorizontalAlignment.Right : HorizontalAlignment.Center;
        CollapseButton.HorizontalAlignment = alignment;
        SplitterLine.HorizontalAlignment = alignment;
    }

    // FORK: Main/Settings view switch in the same window — two ToggleButtons
    // above the left column flip which ScrollViewer is visible. The Main
    // view holds the working controls (Control, Send text, Languages);
    // the Settings view holds Voice, VRChat, Overlay, API keys, Display.
    private void MainViewButton_Click(object sender, RoutedEventArgs e) =>
        ShowSettingsView(false);

    private void SettingsViewButton_Click(object sender, RoutedEventArgs e) =>
        ShowSettingsView(true);

    private void ShowSettingsView(bool settings)
    {
        MainScroll.Visibility = settings ? Visibility.Collapsed : Visibility.Visible;
        SettingsScroll.Visibility = settings ? Visibility.Visible : Visibility.Collapsed;
        MainViewButton.IsChecked = !settings;
        SettingsViewButton.IsChecked = settings;
    }

    // FORK: UI-language switcher — a button showing the current language
    // (bundled flag image + native name) with a menu of all seven.
    private void BuildLangMenu()
    {
        _langMenu = new MenuFlyout();
        foreach (var (code, name) in UiLanguage.Supported)
        {
            var item = new MenuFlyoutItem { Text = name };
            var flag = FlagImages.Get(code);
            if (flag != null) item.Icon = new ImageIcon { Source = flag };
            item.Click += (_, _) =>
            {
                if (_cfg.UiLanguage == code) return;
                _cfg.UiLanguage = code;
                _uiLang = code;
                ApplyUiLanguage();
                RepopulateFeed();
            };
            _langMenu.Items.Add(item);
        }
    }

    private void LangButton_Click(object sender, RoutedEventArgs e) => _langMenu?.ShowAt(LangButton);

    private void ApplyUiLanguage()
    {
        MainViewButton.Content = T("Main");
        SettingsViewButton.Content = T("Settings");
        DisplayGroupHeader.Text = T("Display");
        VoiceGroupHeader.Text = T("Voice");
        VrChatGroupHeader.Text = T("VRChat");
        ApiKeysGroupHeader.Text = T("API keys");
        OverlayGroupHeader.Text = T("Overlay");
        AdvancedGroupHeader.Text = T("Advanced");

        StartStopButton.Content = _engine.IsRunning ? T("Stop") : T("Start");
        ClearFeedButton.Content = T("Clear feed");
        SendButton.Content = T("Send");
        UpdateButton.Content = T("Restart to update");
        MicRefreshButton.Content = T("Refresh");
        StatusText.Text = TranslateStatus(_lastStatus);
        MutedText.Text = _engine.IsMuted ? T("MUTED") : "";
        LangName.Text = UiLanguage.NativeName(_uiLang);
        LangFlag.Source = FlagImages.Get(_uiLang);

        FromMainLabel.Text = T("From:");
        FromVoiceLabel.Text = T("From:");
        ToLabel.Text = T("To:");
        RecognizerLabel.Text = T("Recognizer:");
        TranslatorLabel.Text = T("Translator:");
        MicLabel.Text = T("Mic:");
        LogSizeLabel.Text = T("Log size:");
        UiSizeLabel.Text = T("UI size %:");
        GroqLabel.Text = T("Groq:");
        GeminiLabel.Text = T("Gemini:");
        AnchorLabel.Text = T("Anchor:");
        MaxEntriesLabel.Text = T("Max entries:");
        OverlayFontSizeLabel.Text = T("Font size:");
        WidthCmLabel.Text = T("Width (cm):");
        OpacityLabel.Text = T("Opacity %:");
        DisplaySecLabel.Text = T("Display sec (0=always):");
        FadeoutSecLabel.Text = T("Fadeout sec:");
        PosXLabel.Text = T("Pos X (m):");
        PosYLabel.Text = T("Pos Y (m):");
        PosZLabel.Text = T("Pos Z (m):");

        SendToChatboxBox.Content = T("Send to chatbox");
        ShowTranscriptionBox.Header = T("Show transcription");
        TranslateMySpeechBox.Header = T("Translate my speech");
        TranslateIncomingBox.Header = T("Translate incoming speech");
        TranscriptionOnlyBox.Header = T("Transcription only (no translation)");
        TranslationOnlyBox.Content = T("Translation only (no original)");
        SendWithoutWaitingBox.Content = T("Send without waiting for finish");
        DisableWhenMutedBox.Content = T("Disable when muted");
        SendUserDataBox.Content = T("Send user data (OSC)");
        DesktopOverlayBox.Header = T("Desktop overlay");
        ShowOriginalBox.Header = T("Show original text");
        ShowOwnMicBox.Header = T("Show own mic in feed");

        // Anchor options carry translated strings, so rebuild them here;
        // the selection is restored (LoadFromConfig on startup, here on
        // language change).
        AnchorCombo.ItemsSource = new List<LangOption>
        {
            new("0", "HMD"),
            new("1", T("Left hand")),
            new("2", T("Right hand")),
        };
        AnchorCombo.SelectedIndex = Math.Clamp(_cfg.OverlayAnchor, 0, 2);
    }

    private void WireControls()
    {
        var sources = Languages.Source.Select(l => new LangOption(l.Code, l.Name)).ToList();
        var targets = Languages.Target.Select(l => new LangOption(l.Code, l.Name)).ToList();
        FromComboMain.ItemsSource = sources;
        FromComboSettings.ItemsSource = sources;
        TargetCombo.ItemsSource = targets;
        RecognizerCombo.ItemsSource = new List<LangOption>
            { new("0", "Bing (default)"), new("1", "Groq"), new("2", "Gemini") };
        TranslatorCombo.ItemsSource = new List<LangOption>
            { new("0", "Google"), new("1", "Groq"), new("2", "Gemini") };

        foreach (var cb in _sourceCombos)
        {
            cb.SelectionChanged += (_, _) =>
            {
                if (!_loading && cb.SelectedItem is LangOption o)
                {
                    _cfg.SourceLanguage = o.Code;
                    // FORK: the From dropdown exists twice (Main + Settings);
                    // keep the other one on the same language.
                    foreach (var other in _sourceCombos)
                        if (other != cb) SelectByCode(other, o.Code);
                }
            };
        }
        TargetCombo.SelectionChanged += (_, _) =>
        {
            if (!_loading && TargetCombo.SelectedItem is LangOption o) _cfg.TargetLanguage = o.Code;
            RefreshTargetQuickButtons();
        };
        RecognizerCombo.SelectionChanged += (_, _) =>
        {
            if (!_loading && RecognizerCombo.SelectedItem is LangOption o) _cfg.Recognizer = int.Parse(o.Code);
        };
        TranslatorCombo.SelectionChanged += (_, _) =>
        {
            if (!_loading && TranslatorCombo.SelectedItem is LangOption o) _cfg.Translator = int.Parse(o.Code);
        };
        AnchorCombo.SelectionChanged += (_, _) =>
        {
            if (!_loading && AnchorCombo.SelectedItem is LangOption o) _cfg.OverlayAnchor = int.Parse(o.Code);
        };
        MicCombo.SelectionChanged += (_, _) =>
        {
            if (!_loading && MicCombo.SelectedItem is string name) _cfg.Microphone = name;
        };

        WireCheck(SendToChatboxBox, v => _cfg.SendToChatbox = v);
        WireCheck(TranslateMySpeechBox, v => _cfg.MicTranslation = v);
        WireCheck(TranslateIncomingBox, v => _cfg.DesktopTranslation = v);
        WireCheck(TranscriptionOnlyBox, v => _cfg.SpeechToTextOnly = v);
        WireCheck(TranslationOnlyBox, v => _cfg.TranslationOnly = v);
        WireCheck(SendWithoutWaitingBox, v => _cfg.SendWithoutWaitingForFinish = v);
        WireCheck(DisableWhenMutedBox, v => _cfg.DisableWhenMuted = v);
        WireCheck(SendUserDataBox, v => _cfg.SendUserData = v);
        WireCheck(DesktopOverlayBox, v => _cfg.DesktopOverlayEnabled = v);
        WireCheck(ShowOriginalBox, v => _cfg.OverlayShowOriginal = v);
        WireCheck(ShowOwnMicBox, v => _cfg.FeedShowOwn = v);
        // FORK: toggling transcription re-renders the feed, so existing
        // entries gain/lose their transcription line too.
        ShowTranscriptionBox.Toggled += (_, _) =>
        {
            if (_loading) return;
            _cfg.FeedShowTranscription = ShowTranscriptionBox.IsOn;
            RepopulateFeed();
        };

        WireNum(LogSizeBox, v => { _cfg.LogFontSize = v; ApplyLogFont(); });
        WireNum(UiScaleBox, v =>
        {
            if (_cfg.UiScale == v) return;
            _cfg.UiScale = v;
            ApplyUiScale();
        });
        WireNum(MaxEntriesBox, v => _cfg.OverlayMaxEntries = v);
        WireNum(OverlayFontSizeBox, v => _cfg.OverlayFontSize = v);
        WireNum(WidthCmBox, v => _cfg.OverlayWidthCm = v);
        WireNum(DisplaySecBox, v => _cfg.OverlayDisplayDurationSec = v);
        WireNum(FadeoutSecBox, v => _cfg.OverlayFadeoutDurationSec = v);
        WireDbl(PosXBox, v => _cfg.OverlayPosX = v);
        WireDbl(PosYBox, v => _cfg.OverlayPosY = v);
        WireDbl(PosZBox, v => _cfg.OverlayPosZ = v);

        WireKey(GroqKeyBox, v => _cfg.GroqApiKey = v);
        WireKey(GeminiKeyBox, v => _cfg.GeminiApiKey = v);

        OpacitySlider.ValueChanged += (_, args) =>
        {
            if (_loading) return;
            _cfg.OverlayOpacity = (int)Math.Round(args.NewValue);
            OpacityValueLabel.Text = ((int)Math.Round(args.NewValue)) + "%";
        };
    }

    private void WireCheck(CheckBox box, Action<bool> set)
    {
        box.Checked += (_, _) => { if (!_loading) set(true); };
        box.Unchecked += (_, _) => { if (!_loading) set(false); };
    }

    // FORK: ToggleSwitch version, for the Advanced toggles in Main.
    private void WireCheck(ToggleSwitch sw, Action<bool> set)
    {
        sw.Toggled += (_, _) => { if (!_loading) set(sw.IsOn); };
    }

    private void WireNum(NumberBox box, Action<int> set)
    {
        box.ValueChanged += (_, args) => { if (!_loading) set((int)Math.Round(args.NewValue)); };
    }

    private void WireDbl(NumberBox box, Action<double> set)
    {
        box.ValueChanged += (_, args) => { if (!_loading) set(args.NewValue); };
    }

    private void WireKey(PasswordBox box, Action<string> set)
    {
        box.PasswordChanged += (_, _) => { if (!_loading) set(box.Password); };
    }

    private void LoadFromConfig()
    {
        _loading = true;
        try
        {
            SelectByCode(FromComboMain, _cfg.SourceLanguage);
            SelectByCode(FromComboSettings, _cfg.SourceLanguage);
            SelectByCode(TargetCombo, _cfg.TargetLanguage);
            RecognizerCombo.SelectedIndex = Math.Clamp(_cfg.Recognizer, 0, 2);
            TranslatorCombo.SelectedIndex = Math.Clamp(_cfg.Translator, 0, 2);
            AnchorCombo.SelectedIndex = Math.Clamp(_cfg.OverlayAnchor, 0, 2);

            SendToChatboxBox.IsChecked = _cfg.SendToChatbox;
            ShowTranscriptionBox.IsOn = _cfg.FeedShowTranscription;
            TranslateMySpeechBox.IsOn = _cfg.MicTranslation;
            TranslateIncomingBox.IsOn = _cfg.DesktopTranslation;
            TranscriptionOnlyBox.IsOn = _cfg.SpeechToTextOnly;
            TranslationOnlyBox.IsChecked = _cfg.TranslationOnly;
            SendWithoutWaitingBox.IsChecked = _cfg.SendWithoutWaitingForFinish;
            DisableWhenMutedBox.IsChecked = _cfg.DisableWhenMuted;
            SendUserDataBox.IsChecked = _cfg.SendUserData;
            DesktopOverlayBox.IsOn = _cfg.DesktopOverlayEnabled;
            ShowOriginalBox.IsOn = _cfg.OverlayShowOriginal;
            ShowOwnMicBox.IsOn = _cfg.FeedShowOwn;

            LogSizeBox.Value = Math.Clamp(_cfg.LogFontSize, 8, 24);
            UiScaleBox.Value = Math.Clamp(_cfg.UiScale, 80, 250);
            MaxEntriesBox.Value = Math.Clamp(_cfg.OverlayMaxEntries, 1, 12);
            OverlayFontSizeBox.Value = Math.Clamp(_cfg.OverlayFontSize, 24, 160);
            WidthCmBox.Value = Math.Clamp(_cfg.OverlayWidthCm, 40, 200);
            DisplaySecBox.Value = Math.Clamp(_cfg.OverlayDisplayDurationSec, 0, 3600);
            FadeoutSecBox.Value = Math.Clamp(_cfg.OverlayFadeoutDurationSec, 0, 30);
            PosXBox.Value = Math.Clamp(_cfg.OverlayPosX, -2, 2);
            PosYBox.Value = Math.Clamp(_cfg.OverlayPosY, -2, 2);
            PosZBox.Value = Math.Clamp(_cfg.OverlayPosZ, -2, 2);
            GroqKeyBox.Password = _cfg.GroqApiKey;
            GeminiKeyBox.Password = _cfg.GeminiApiKey;
            OpacitySlider.Value = Math.Clamp(_cfg.OverlayOpacity, 10, 100);
            OpacityValueLabel.Text = ((int)OpacitySlider.Value) + "%";

            RefreshMics();
            RefreshTargetQuickButtons();
        }
        finally { _loading = false; }
    }

    private static void SelectByCode(ComboBox cb, string code)
    {
        for (var i = 0; i < cb.Items.Count; i++)
            if (cb.Items[i] is LangOption o && o.Code == code) { cb.SelectedIndex = i; return; }
        cb.SelectedIndex = 0;
    }

    // FORK: bold the quick button matching the current target language.
    private void RefreshTargetQuickButtons()
    {
        foreach (var (button, code) in _targetQuickButtons)
            button.FontWeight = code == _cfg.TargetLanguage ? FontWeights.Bold : FontWeights.Normal;
    }

    private void RefreshMics()
    {
        _loading = true;
        try
        {
            var mics = _engine.GetMicrophones();
            MicCombo.ItemsSource = mics.Select(m => m.Name).ToList();
            var idx = MicCombo.Items.IndexOf(_cfg.Microphone);
            MicCombo.SelectedIndex = idx >= 0 ? idx : -1;
            if (MicCombo.SelectedIndex < 0 && MicCombo.Items.Count > 0)
            {
                var def = mics.FirstOrDefault(d => d.IsDefault);
                MicCombo.SelectedIndex = def.Name != null ? MicCombo.Items.IndexOf(def.Name) : 0;
            }
        }
        finally { _loading = false; }
    }

    private void ApplyLogFont()
    {
        // FORK: the feed font size lives on the ScrollViewer (ItemsRepeater
        // has no FontSize); template TextBlocks inherit it, except the
        // timestamp header, which keeps its own smaller size.
        FeedScroll.FontSize = Math.Clamp(_cfg.LogFontSize, 8, 24);
    }

    private void RepopulateFeed()
    {
        _feedItems.Clear();
        foreach (var e in _engine.Feed.GetSnapshot().TakeLast(240))
            _feedItems.Add(ToView(e));
        ScrollFeedToBottom();
    }

    private FeedItemView ToView(FeedEntry e)
    {
        var hasTranslation = e.Translation.Length > 0;
        return new FeedItemView
        {
            Header = $"[{e.Timestamp:HH:mm:ss}] {(e.FromDesktop ? T("FeedIncoming") : T("FeedYou"))}",
            Transcription = FeedEntry.Bidi(e.Original),
            MainText = FeedEntry.Bidi(hasTranslation ? e.Translation : e.Original),
            Accent = e.FromDesktop ? IncomingAccent : OwnAccent,
            TranscriptionVisibility = _cfg.FeedShowTranscription && hasTranslation && e.Original.Length > 0
                ? Visibility.Visible : Visibility.Collapsed,
        };
    }

    private void AddFeedEntry(FeedEntry entry)
    {
        _feedItems.Add(ToView(entry));
        if (_feedItems.Count > 240)
        {
            // Drop the oldest 40; keep the view pinned to the end.
            for (var i = 0; i < 40; i++) _feedItems.RemoveAt(0);
        }
        if (_stickToBottom) ScrollFeedToBottom();
    }

    private void ScrollFeedToBottom()
    {
        DispatcherQueue.TryEnqueue(() => FeedScroll.ChangeView(null, FeedScroll.ScrollableHeight, null));
    }

    private void WireEngine()
    {
        _engine.StatusChanged += msg => DispatcherQueue.TryEnqueue(() =>
        {
            _lastStatus = msg;
            StatusText.Text = TranslateStatus(msg);
        });
        // FORK: show a mute state pulled at startup even if it arrived
        // before this page wired up (the gate in AppEngine already has it).
        MutedText.Text = _engine.IsMuted ? T("MUTED") : "";
        _engine.MuteChanged += muted => DispatcherQueue.TryEnqueue(() =>
            MutedText.Text = muted ? T("MUTED") : "");
        _engine.Feed.EntryAdded += entry => DispatcherQueue.TryEnqueue(() => AddFeedEntry(entry));

        _updater.StatusChanged += msg => DispatcherQueue.TryEnqueue(() =>
        {
            _lastStatus = msg;
            StatusText.Text = TranslateStatus(msg);
        });
        _updater.UpdateReady += () => DispatcherQueue.TryEnqueue(() =>
            UpdateButton.Visibility = Visibility.Visible);
        _ = Task.Run(() => _updater.CheckAsync());
    }

    private void StartStopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_engine.IsRunning) _engine.Stop(); else _engine.Start();
        StartStopButton.Content = _engine.IsRunning ? T("Stop") : T("Start");
    }

    private void ClearFeedButton_Click(object sender, RoutedEventArgs e)
    {
        _engine.Feed.Clear();
        _feedItems.Clear();
    }

    private void UpdateButton_Click(object sender, RoutedEventArgs e) => _updater.ApplyAndRestart();

    private void SendButton_Click(object sender, RoutedEventArgs e) => SendManualText();

    private void MicRefreshButton_Click(object sender, RoutedEventArgs e) => RefreshMics();

    private void QuickButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string code)
        {
            SelectByCode(TargetCombo, code);
            RefreshTargetQuickButtons();
        }
    }

    // FORK: scrolling the settings pane must never change a dropdown the
    // cursor happens to pass over — the wheel only counts while the list
    // is open. (Touch scrolling was flipping languages/recognizers.)
    private void ComboBox_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (sender is ComboBox cb && !cb.IsDropDownOpen) e.Handled = true;
    }

    private void ManualBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            SendManualText();
            return;
        }
        // FORK: typing indicator fires on character keystrokes only.
        if (IsTypingKey(e)) OnManualBoxTyped();
    }

    // FORK: TextChanged only clears when the box becomes empty
    // (backspace / cut / Clear). Backspace, Delete, arrows, Enter
    // and other non-character keys never send the indicator.
    private void ManualBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (ManualBox.Text.Length == 0) StopTypingIndicator();
    }

    private void SendManualText()
    {
        var text = ManualBox.Text.Trim();
        if (text.Length == 0) return;
        // FORK: no Start needed anymore — the engine translates standalone
        // when the mic pipeline isn't running.
        StopTypingIndicator();
        _engine.ManualTranslate(text);
        ManualBox.Text = "";
        ManualBox.Focus(FocusState.Programmatic);
        // FORK: re-clear 1s later, once the message has landed, in case
        // VRChat re-showed the indicator on message arrival.
        _sendClearTimer.Stop();
        _sendClearTimer.Start();
    }

    // FORK: typing indicator while composing in the Send box. True on
    // character keystrokes only (see IsTypingKey); the 5s idle timer clears
    // it when typing stops, so VRChat never shows a stuck indicator. Works
    // without Start (plain OSC).
    private void OnManualBoxTyped()
    {
        _engine.SetTyping(true);
        _typingTimer.Stop();
        _typingTimer.Start();
    }

    private static bool IsTypingKey(KeyRoutedEventArgs e)
    {
        // FORK: only keys that insert a character count as typing. Backspace,
        // Delete, arrows, Enter, Tab, function keys and the like never send
        // the indicator. Ctrl+V pastes text in, so it counts.
        if (IsKeyDown(VirtualKey.Menu)) return false;
        if (IsKeyDown(VirtualKey.Control)) return e.Key == VirtualKey.V;
        var k = e.Key;
        if (k is >= VirtualKey.A and <= VirtualKey.Z) return true;
        if (k is >= VirtualKey.Number0 and <= VirtualKey.Number9) return true;
        if (k is >= VirtualKey.NumberPad0 and <= VirtualKey.NumberPad9) return true;
        return k is VirtualKey.Space or VirtualKey.Multiply or VirtualKey.Add
            or VirtualKey.Subtract or VirtualKey.Decimal or VirtualKey.Divide
            || IsOemKey(k);
    }

    // FORK: Uno's VirtualKey enum has no OEM (punctuation) members, so
    // match them by Win32 VK code — the same values as WinForms Keys.Oem1
    // (0xBA), Oem2 (0xBF), Oem3 (0xC0), Oem4 (0xDB), Oem5 (0xDC),
    // Oem6 (0xDD), Oem7 (0xDE), Oem8 (0xDF), Oem102 (0xE2).
    private static bool IsOemKey(VirtualKey k)
    {
        var v = (int)k;
        return v is 0xBA or 0xBF or 0xC0 or 0xDB or 0xDC or 0xDD or 0xDE or 0xDF or 0xE2;
    }

    // FORK: modifier state from the current thread; false on any failure
    // (the indicator just doesn't fire — never crash the keystroke).
    private static bool IsKeyDown(VirtualKey key)
    {
        try
        {
            var state = InputKeyboardSource.GetKeyStateForCurrentThread(key);
            return (state & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
        }
        catch { return false; }
    }

    // FORK: belt and braces — clear typing again 1s after a send, once the
    // message has landed. Skipped if the user already started typing the
    // next message.
    private void ResendTypingFalse()
    {
        _sendClearTimer.Stop();
        if (ManualBox.Text.Length == 0) _engine.SetTyping(false);
    }

    private void StopTypingIndicator()
    {
        _typingTimer.Stop();
        _engine.SetTyping(false);
    }
}
