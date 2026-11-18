using KikitanTranslator.Base.Outputs;
using KikitanTranslator.Base.Translators;
using KikitanTranslator.Recognizers;
using KikitanTranslator.Utility;
using Serilog;

namespace KikitanTranslator.Base;

public class Kikitan : IDisposable
{
    private IRecognizer _recognizer;
    private ITranslator _translator;
    private IErrorHandler _errorHandler;
    private List<IOutput> _outputs = [];

    private List<string[]> _queue = [];
    private readonly object _queueLock = new object();

    private bool _running;
    private bool _isLoopback;
    // FORK: optional gate checked at recognition time (not send time).
    // Used for mic-mute: closing the gate mid-utterance cuts the utterance
    // off at the last interim that passed the gate, instead of letting the
    // recognizer's later final (which may include speech after the mute)
    // through.
    private readonly Func<bool>? _shouldProcess;
    // FORK: true once an interim result passes the gate for the current
    // utterance. Last interim that passed the gate; _cutOff suppresses the
    // cut-off utterance's later final, which would otherwise duplicate the
    // cut-off send or include post-mute speech.
    private bool _utteranceStarted;
    private string _lastInterim = "";
    private bool _cutOff;
    // FORK: set by ManualTranslate. Typed text is an explicit user action,
    // not mic input, so it bypasses the mute gate and leaves mic utterance
    // state (including a pending cut-off suppression) untouched.
    private bool _manualBypass;
    // FORK: speech typing indicator callback (mic pipeline only, wired by
    // AppEngine to the Chatbox). True on mic interims, false on mic finals —
    // including translation-failed and empty finals — so it can never stick
    // on. Manual (typed) finals never touch it; MainForm owns typed typing.
    private readonly Action<bool>? _onTyping;
    
    public event OnRecognizerStatus? OnRecognizerStatusChanged;

    public Kikitan(IRecognizer recognizer, ITranslator translator, IErrorHandler errorHandler, bool loopback, Func<bool>? shouldProcess = null, Action<bool>? onTyping = null)
    {
        _recognizer = recognizer;
        _translator = translator;
        _errorHandler = errorHandler;
        _shouldProcess = shouldProcess;
        _onTyping = onTyping;

        recognizer.OnRecognitionReceived += OnRecognition;
        recognizer.OnRecognizerStatusChanged += OnRecognizerStatus;
        
        _isLoopback = loopback;
        
        Log.Information($"[KKTN] Kikitan is starting up: desktop={_isLoopback}, recognizer={_recognizer.GetType().Name}, translator={_translator.GetType().Name}");
    }

    public void AddOutput(IOutput output) => _outputs.Add(output);

    public void Start()
    {
        _recognizer.Start(_isLoopback ? AppConfig.ConfigObject.TargetLanguage : AppConfig.ConfigObject.SourceLanguage, _errorHandler);
        _running = true;
        Log.Information($"[KKTN] Recognizer start result: desktop={_isLoopback}, status={_recognizer.Status()}");
        
        Task.Run(QueueWorker);
    }

    public void Stop()
    {
        _recognizer.Stop();
        _running = false;
        
        Log.Information("[KKTN] Kikitan has stopped");
    }

    private void OnRecognition(string text, bool final)
    {
        if (final) Log.Debug($"[KKTN] Final recognition received: desktop={_isLoopback}, chars={text.Length}, running={_running}");
        // FORK: gate logic. Interim results passing the gate mark the utterance
        // as started and update the last good interim. The first interim after
        // the gate closes cuts the utterance off: the last good interim is
        // finalized immediately (mute = done talking) and the recognizer's
        // later final for that utterance is suppressed, so speech after the
        // mute never rides along in it. An interim after the gate reopens
        // starts a new utterance, even if the recognizer has not segmented
        // yet. Utterances that never passed the gate are still dropped.
        bool gateOpen = _shouldProcess == null || _shouldProcess();
        bool isManual = false;
        if (!final)
        {
            if (_cutOff)
            {
                if (!gateOpen)
                {
                    Log.Debug($"[KKTN] Recognition dropped by gate: desktop={_isLoopback}, final={final}");
                    return;
                }
                _cutOff = false;
                _utteranceStarted = true;
                if (text.Length > 0) _lastInterim = text;
            }
            else if (gateOpen)
            {
                _utteranceStarted = true;
                if (text.Length > 0) _lastInterim = text;
            }
            else if (_utteranceStarted)
            {
                _cutOff = true;
                _utteranceStarted = false;
                var cutoff = _lastInterim;
                _lastInterim = "";
                if (cutoff.Length > 0)
                {
                    Log.Debug($"[KKTN] Utterance cut off by gate: desktop={_isLoopback}, chars={cutoff.Length}");
                    ProcessFinal(cutoff);
                }
                else Log.Debug($"[KKTN] Utterance cut off by gate with no interim to finalize: desktop={_isLoopback}");
                return;
            }
            else
            {
                Log.Debug($"[KKTN] Recognition dropped by gate: desktop={_isLoopback}, final={final}");
                return;
            }
        }
        else
        {
            isManual = _manualBypass;
            _manualBypass = false;
            if (!isManual)
            {
                if (_cutOff)
                {
                    _cutOff = false;
                    Log.Debug($"[KKTN] Final suppressed after gate cut-off: desktop={_isLoopback}");
                    return;
                }
                if (!_utteranceStarted && !gateOpen)
                {
                    Log.Debug($"[KKTN] Recognition dropped by gate: desktop={_isLoopback}, final={final}");
                    return;
                }
                _utteranceStarted = false;
                _lastInterim = "";
            }
        }
        if (!final)
        {
            // FORK: mic interims mean the user is speaking — show the typing
            // indicator. Desktop (incoming) interims never drive it, and
            // gate-dropped interims return above without reaching here.
            if (!_isLoopback) _onTyping?.Invoke(true);
            if (AppConfig.ConfigObject.Recognizer == 2)
            {
                foreach (var output in _outputs)
                {
                    output.Send(text.Split("|")[0], text.Split("|")[1], false);
                }
            }
            else
            {
                foreach (var output in _outputs)
                {
                    output.Send(text, "", false);
                }
            }
            return;
        }

        ProcessFinal(text, isManual);
    }

    private void ProcessFinal(string text, bool manual = false)
    {
        // FORK: manual text is plain typed text, not a Gemini recognizer
        // "input|output" pair, so it always goes through the translator even
        // when the Gemini recognizer is selected (which otherwise forwards
        // its own already-translated output and would split-crash on "|").
        if (!manual && AppConfig.ConfigObject.Recognizer == 2)
        {
            foreach (var output in _outputs)
            {
                output.Send(text.Split("|")[0], text.Split("|")[1], true);
            }

            ClearSpeechTyping(manual);
            return;
        }

        foreach (var output in _outputs)
        {
            output.Send(text, "", false);
        }

        if (text.Length == 0) { ClearSpeechTyping(manual); return; }

        try
        {
            var translationTimer = System.Diagnostics.Stopwatch.StartNew();
            Log.Debug($"[KKTN] Translation started: desktop={_isLoopback}, translator={_translator.GetType().Name}, chars={text.Length}, transcriptionOnly={AppConfig.ConfigObject.SpeechToTextOnly}");
            var translated = AppConfig.ConfigObject.SpeechToTextOnly ? "" : _isLoopback ? _translator.Translate(text, AppConfig.ConfigObject.TargetLanguage, AppConfig.ConfigObject.SourceLanguage) : _translator.Translate(text, AppConfig.ConfigObject.SourceLanguage, AppConfig.ConfigObject.TargetLanguage);
        
            Log.Debug($"[KKTN] Translation completed: desktop={_isLoopback}, elapsedMs={translationTimer.ElapsedMilliseconds}, resultChars={translated?.Length}, hasResult={translated != null}");
            if (translated == null) Log.Warning($"[KKTN] No translation result; final output skipped: desktop={_isLoopback}");
            if (translated != null)
            {
                lock (_queueLock)
                {
                    _queue.Add([text, translated]);
                    Log.Debug($"[KKTN] Translation queued: desktop={_isLoopback}, depth={_queue.Count}");
                }
            
                foreach (var output in _outputs.Where(v => !v.IsDelayed())) output.Send(text, translated, true);
            }
        }
        catch (Exception e)
        {
            Log.Error(e, $"[KKTN] Translation/output processing failed: desktop={_isLoopback}");
            
            _errorHandler.OnError($"Error while translating: {e.Message}");
        }

        ClearSpeechTyping(manual);
    }

    // FORK: mic finals clear the speech typing indicator, including the
    // translation-failed and empty-text exits, so it can never stick on.
    // Manual (typed) finals never touch it — MainForm owns typed typing.
    private void ClearSpeechTyping(bool manual)
    {
        if (!manual && !_isLoopback) _onTyping?.Invoke(false);
    }

    private void OnRecognizerStatus(RecognizerStatus status) => OnRecognizerStatusChanged?.Invoke(status);

    private async void QueueWorker()
    {
        while (_running)
        {
            if (_queue.Count == 0)
            {
                await Task.Delay(50);
                
                continue;
            }

            lock (_queueLock)
            {
                var texts = _queue.First();
                _queue.RemoveAt(0);
            
                Log.Debug($"[KKTN] Processing delayed output: desktop={_isLoopback}, remaining={_queue.Count}, sourceChars={texts[0].Length}, translationChars={texts[1].Length}");
            
                foreach (var output in _outputs.Where(v => v.IsDelayed())) output.Send(texts[0], texts[1], true);
                // FORK: no artificial per-char delay; send ASAP. Typing is
                // driven by Kikitan (interim = true, mic final = false);
                // Chatbox.Send itself does not touch it.
            }
        }
    }

    public void ManualTranslate(string text)
    {
        _manualBypass = true;
        OnRecognition(text, true);
    }

    public void Dispose()
    {
        _running = false;
        _recognizer.Dispose();
        _translator.Dispose();
    }
}