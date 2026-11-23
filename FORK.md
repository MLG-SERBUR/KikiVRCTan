# Fork notes — `feed-overlay` branch

Fork of [YusufOzmen01/kikitan-translator](https://github.com/YusufOzmen01/kikitan-translator)
(`upstream` remote). This branch adds a VRCT-style in-memory translation feed
and a native Windows UI, without touching the speech/translation backend.

## What changed

**New: `KikitanTranslator.WinForms`** — native WinForms host, no webview/WebView2
(upstream's `KikitanTranslator.Photino` project is untouched and still builds).

- `Program.cs` — entry point: Serilog `Logger.Initialize()`, `AppConfig.Load()`,
  runs `MainForm`. Single-instance guard (named mutex): a second copy
  refuses to start instead of double-sending OSC.
- `AppEngine.cs` — pipeline orchestration, mirrors upstream `Photino/Manager`
  (same recognizers/translators/capture, chatbox + OSC outputs, mic fallback
  watcher, overlay process launch). Adds the translation feed as a plain
  `IOutput` (`Custom`) on both mic and desktop pipelines, and pushes feed
  frames to the overlay through the `kikitan-feed` pipe.
- `TranslationFeed.cs` — thread-safe in-memory ring buffer (200 entries,
  like VRCT: no persistence). Incoming (desktop) speech only by default;
  `feed_show_own` opts the user's own mic transcription back in.
- `FeedPipeWriter.cs` — JSON over a named MMF (`kikitan-pipe` is upstream's
  legacy single-message pipe; `kikitan-feed` is new and carries entries +
  overlay config).
- `MainForm.cs` — settings UI (languages, recognizer/translator, mic,
  VRChat/chatbox options, API keys, overlay settings) bound to the shared
  `AppConfig` (`%APPDATA%/Kikitan Translator/config.json`, same file as
  upstream), plus a live feed list. Two tabs: Main (Start/Stop, Send text
  + the Send to chatbox toggle, target-language quick buttons) and Settings
  (everything else). The quick
  buttons switch the target language in one tap — Korean, Japanese,
  Chinese, Arabic, Spanish, in that order — through the same path as the
  To dropdown, with the active one bolded. A dropdown never changes on
  mouse-wheel scroll unless its list is open, so scrolling the pane can't
  flip a setting the cursor passes over. UI language: `UiLanguage.cs`
  translates the whole UI into English, Korean, Japanese, Chinese, Arabic,
  Spanish and Russian (one table, no .resx); it defaults to the Windows
  display language and is switched from a button on Main showing the current
  language's flag + native name, with a menu listing all seven the same
  way. The flags are real pictures, not emoji — Windows has no flag glyphs
  (Segoe UI Emoji renders them as the two-letter code) — bundled as
  embedded PNGs in `Resources/flags/` (`FlagImages.cs` scales them per UI
  size), flat 4:3 renders of lipis/flag-icons via flagcdn.com, MIT (license
  in `Resources/flags/LICENSE.txt`); Arabic uses the Saudi flag as the
  conventional stand-in. Saved as `ui_language`, empty means follow Windows. The From dropdown is on Main too, synced with
  the one in Settings, and on a true first run it defaults to the UI
  language (Spanish maps to es-ES, the Source list has no plain "es").
  Layout stays left-to-right in all languages; changing the UI language
  restarts the engine like any other setting change. The Send text box translates typed text
  directly: no Start needed (standalone translation in `AppEngine` when the
  mic pipeline isn't running), and typing in it shows the VRChat typing
  indicator, cleared 5s after the last keystroke or on send. The indicator
  is keystrokes-only — speech output never sends it — and only character
  keys trigger it (Backspace, Delete, arrows, Enter never do); a second
  clear goes out 1s after each send. Speech typing is driven separately by
  the mic pipeline (true on interim, false on mic final including
  translation failure, so it can't stick on); incoming speech and typed
  sends never drive it. The title bar shows the short build
  SHA from `version.txt`, so a stale build is visible at a glance.
- `Languages.cs` — generated from upstream
  `KikitanTranslator.Photino/UserInterface/src/util/constants.ts`.
- `OscWatcher.cs` — copied from upstream Photino (mute detection), namespace
  adjusted.

**`KikitanTranslator.Base`** — one additive edit, marked `// FORK`:

- `Utility/AppConfig.cs`: overlay/feed settings
  (`overlay_max_entries`, `overlay_font_size`, `overlay_opacity`,
  `overlay_anchor`, `overlay_width_cm`,
  `overlay_show_original`, `overlay_display_duration_sec`,
  `overlay_fadeout_duration_sec`, `overlay_pos_x/y/z`,
  `desktop_overlay_enabled`) plus WinForms-only UI settings
  (`ui_scale`: window/control scale percent, applied live with no restart;
  `log_font_size`). Unknown to upstream builds, which ignore extra
  JSON members.

**`KikitanTranslator.Overlay`** — additive + small marked edits:

- `FeedPipe.cs` (new) — JSON DTOs + tolerant `FeedPipeReader`
  (returns null when the pipe is absent).
- `FeedRenderer.cs` (new) — SkiaSharp multi-line feed renderer, extracted
  from `OverlayServer` so it can be tested in isolation (`tests/FeedTests`).
  Font size is fixed from config; **the box grows with content** (up to a cap,
  oldest lines trimmed) instead of shrinking text. Desktop entries render
  light-blue, mic entries white; overlay messages carry no timestamp
  (the log feed still shows them). An empty feed renders fully transparent —
  no "Waiting for speech…" placeholder.
- `OverlayServer.cs` — `StartFeedReader` poll loop; `SendFeed` routes rendered
  feed frames to the desktop form / VR overlay.
- `OpenVROverlay.cs` — anchor presets (HMD / left hand / right hand, with HMD
  fallback when a controller isn't tracked), VRCT-like position offsets,
  display duration + fadeout, persistent feed display, per-tick transform
  updates for hand tracking, width/opacity from config. Legacy timed
  single-message path preserved.
- `Form1.cs` — `SetFeedImage` (persistent, optional display duration) /
  `ClearImage`; feed drawn larger than legacy messages.
- `Program.cs` — watchdog also accepts the WinForms host process; VR overlay
  loop moved to a background thread so the desktop form runs alongside it.

`sln` — WinForms project added. The WinForms build also builds + stages
`KikitanTranslator.Overlay.exe` next to its own exe.

## Upstream sync strategy

- Every fork change is marked `// FORK (feed-overlay):` — grep for it.
- Rule: **additive only** in shared projects (`Base`, `Overlay`). New files or
  new blocks; no signature changes, no restructures. The only modified shared
  file with logic is `OverlayServer.cs`/`OpenVROverlay.cs`/`Form1.cs`
  (overlay), each change fenced by `// FORK` comments.
- To pull upstream: `git fetch upstream && git merge upstream/main`
  (resolve; the `// FORK` blocks are small and localized). The Photino web UI
  is untouched, so its churn never conflicts with the WinForms host.

## Distribution

No installer — KikiVRCTan is portable. The workflow publishes a
self-contained, single-file win-x64 build (`PublishSingleFile`; the zip holds
just the two exes, `silero_vad.onnx`, and `version.txt`) as
`KikiVRCTan-portable.zip`, attached to the `feed-overlay-latest` prerelease
(and as a workflow artifact).

Download, extract anywhere, run `KikitanTranslator.exe`. Everything
(app, `config.json`, `logs/`) stays in that one folder: no %APPDATA%, no
registry, no install step, no admin rights. To update, extract the new zip
over the same folder — the zip contains no `config.json`, so your settings
survive.

A simple self-updater (`Updater.cs`) checks `version.txt` on the release at
startup; on mismatch it downloads the zip in the background and shows a
"Restart to update" button, which applies the update via a batch script
(preserving `config.json`) and restarts the app.

## Tests

`tests/FeedTests` — Linux-runnable: feed pipe round-trip (skipped on Linux,
named MMFs are Windows-only) and `FeedRenderer` scenarios saved as PNGs for
visual inspection. The WinForms UI and OpenVR overlay need real Windows.
