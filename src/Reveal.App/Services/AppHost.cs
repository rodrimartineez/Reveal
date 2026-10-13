using System.Diagnostics;
using System.Windows;
using Reveal.App.Views;
using Reveal.Core.Lexicon;
using Reveal.Core.Lookup;
using Reveal.Core.Settings;
using Reveal.Core.Text;
using Reveal.Core.Translation;
using Reveal.Core.Vocabulary;
using Reveal.Platform.Capture;
using Reveal.Platform.Hotkeys;
using Reveal.Platform.Speech;
using Reveal.Platform.SystemIntegration;

namespace Reveal.App.Services;

/// <summary>
/// Composition root and application state: owns every service, wires capture -> overlay,
/// registers the global hotkeys and keeps the tray / windows in sync.
/// </summary>
public sealed class AppHost : IDisposable
{
    private readonly Application _app;
    private SelectionMonitor? _monitor;
    private GlobalHotkeys? _hotkeys;
    private int _startHotkeyId;
    private int _stopHotkeyId;
    private TrayIconService? _tray;
    private OverlayWindow? _overlay;
    private ToastWindow? _toast;
    private OverlayController? _controller;
    private MainWindow? _main;
    private SettingsWindow? _settingsWindow;
    private SqliteLexicon? _sqliteLexicon;
    private bool _disposed;

    public AppHost(Application app) => _app = app;

    public SettingsStore Settings { get; private set; } = null!;

    public ILexicon Lexicon { get; private set; } = new EmptyLexicon();

    public ModelStore Models { get; } = new();

    public MarianTranslator Marian { get; private set; } = null!;

    public LookupService Lookup { get; private set; } = null!;

    public VocabularyService Vocabulary { get; private set; } = null!;

    public SpeechPlayer Speech { get; } = new();

    public bool CaptureActive { get; private set; }

    public bool IsExiting { get; private set; }

    /// <summary>Last problem registering a hotkey (shown in the main window), null when all is fine.</summary>
    public string? HotkeyProblem { get; private set; }

    /// <summary>A hotkey works but with a caveat (e.g. another app also reserved it).</summary>
    public string? HotkeyNotice { get; private set; }

    public event Action? StateChanged;

    // ------------------------------------------------------------------ startup

    internal void Initialize(bool background)
    {
        Log.Info($"Reveal {typeof(AppHost).Assembly.GetName().Version} starting (background={background})");
        Settings = new SettingsStore();

        try
        {
            if (File.Exists(AppPaths.LexiconFile)) Lexicon = _sqliteLexicon = new SqliteLexicon(AppPaths.LexiconFile);
            else Log.Error("Lexicon file not found: " + AppPaths.LexiconFile);
        }
        catch (Exception ex)
        {
            Log.Error("Could not open the lexicon", ex);
        }

        Marian = new MarianTranslator();
        var translation = new TranslationService(Marian, new OnlineTranslator(() => Settings.Current.AllowOnlineFallback));
        var detector = new LanguageDetector(Lexicon, AppPaths.LangIdFile);
        Lookup = new LookupService(Lexicon, detector, translation);
        Vocabulary = new VocabularyService(() => Settings.Current.VocabularyPath);

        _overlay = new OverlayWindow();
        _overlay.EnsureHandle();
        _toast = new ToastWindow();
        _controller = new OverlayController(_app.Dispatcher, _overlay, _toast, Lookup, Vocabulary, Speech, Settings, () => ShowMain(pickDocument: true));

        _hotkeys = new GlobalHotkeys();
        RegisterHotkeys();

        _tray = new TrayIconService();
        _tray.ToggleRequested += ToggleCapture;
        _tray.OpenRequested += () => ShowMain();
        _tray.SettingsRequested += ShowSettings;
        _tray.ExitRequested += Exit;

        Settings.Changed += OnSettingsChanged;
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
        Models.InstallStateChanged += () => _app.Dispatcher.BeginInvoke(() => StateChanged?.Invoke());

        UpdateTray();

        if (Settings.Current.StartCaptureOnLaunch) StartCapture(silent: true);

        var firstRun = !Settings.Current.FirstRunCompleted;
        if (!background || firstRun)
        {
            ShowMain();
        }

        if (firstRun) Settings.Update(s => s.FirstRunCompleted = true);
    }

    // ------------------------------------------------------------------ capture

    public void ToggleCapture()
    {
        if (CaptureActive) StopCapture();
        else StartCapture();
    }

    public void StartCapture(bool silent = false)
    {
        if (CaptureActive)
        {
            if (!silent) _toast?.ShowToast("Reveal ya está capturando", "Selecciona una palabra o frase.", null, 1500);
            return;
        }

        try
        {
            _monitor = new SelectionMonitor(new SelectionMonitorOptions
            {
                KeyboardSelection = Settings.Current.CaptureKeyboardSelection,
                ClipboardFallback = Settings.Current.UseClipboardFallback,
            })
            {
                IsPointInsideOverlay = (x, y) => _overlay?.ContainsScreenPoint(x, y) == true,
                IsOverlayVisible = () => _overlay?.IsShown == true,
            };
            _monitor.Trace = m => Log.Info(m);
            _monitor.SelectionCaptured += s => _controller?.OnSelectionCaptured(s);
            _monitor.CaptureFailed += f => _controller?.OnCaptureFailed(f);
            _monitor.PointerPressedOutside += () => _controller?.OnPointerPressedOutside();
            _monitor.EscapePressed += () => _controller?.OnEscape();
            _monitor.Start();
        }
        catch (Exception ex)
        {
            Log.Error("Could not start capture", ex);
            _monitor?.Dispose();
            _monitor = null;
            _toast?.ShowToast("No se pudo iniciar la captura", ex.Message, (System.Windows.Media.Brush)_app.FindResource("Danger"), 3500);
            return;
        }

        CaptureActive = true;
        _controller?.RefreshVoices();
        _ = Marian.WarmupAsync(Reveal.Core.Models.Lang.English, Reveal.Core.Models.Lang.Spanish);
        Log.Info("Capture started");
        if (!silent)
        {
            _toast?.ShowToast("Reveal activo", $"Selecciona una palabra o frase · {Settings.Current.StopHotkey} para parar", null, 2200);
        }

        UpdateTray();
        StateChanged?.Invoke();
    }

    public void StopCapture(bool silent = false)
    {
        if (!CaptureActive)
        {
            if (!silent) _toast?.ShowToast("Reveal está en pausa", null, (System.Windows.Media.Brush)_app.FindResource("Text3"), 1200);
            return;
        }

        CaptureActive = false;
        _monitor?.Dispose();
        _monitor = null;
        _controller?.Dismiss();
        Lookup.ClearCache();
        Marian.Unload();
        Log.Info("Capture stopped");
        if (!silent) _toast?.ShowToast("Reveal en pausa", $"{Settings.Current.StartHotkey} para volver a capturar", (System.Windows.Media.Brush)_app.FindResource("Text3"), 1800);

        UpdateTray();
        StateChanged?.Invoke();
    }

    // ------------------------------------------------------------------ hotkeys

    public void RegisterHotkeys()
    {
        if (_hotkeys is null) return;
        _hotkeys.Unregister(_startHotkeyId);
        _hotkeys.Unregister(_stopHotkeyId);
        _startHotkeyId = _stopHotkeyId = 0;

        var problems = new List<string>();
        var notices = new List<string>();
        if (HotkeyGesture.TryParse(Settings.Current.StartHotkey, out var start))
        {
            _startHotkeyId = _hotkeys.Register(start, () => StartCapture(), out var r);
            if (!r.Success && r.Error is not null) problems.Add(r.Error);
            if (r.Warning is not null) notices.Add(r.Warning);
        }
        else problems.Add("El atajo de inicio no es válido.");

        if (HotkeyGesture.TryParse(Settings.Current.StopHotkey, out var stop))
        {
            _stopHotkeyId = _hotkeys.Register(stop, () => StopCapture(), out var r);
            if (!r.Success && r.Error is not null) problems.Add(r.Error);
            if (r.Warning is not null) notices.Add(r.Warning);
        }
        else problems.Add("El atajo de parada no es válido.");

        HotkeyProblem = problems.Count == 0 ? null : string.Join(" ", problems);
        HotkeyNotice = notices.Count == 0 ? null : string.Join(" ", notices);
        if (HotkeyProblem is not null) Log.Error("Hotkeys: " + HotkeyProblem);
        if (HotkeyNotice is not null) Log.Info("Hotkeys: " + HotkeyNotice);
        StateChanged?.Invoke();
    }

    /// <summary>Tries to apply a new pair of hotkeys; on failure the previous ones are restored.</summary>
    public string? TrySetHotkeys(string start, string stop)
    {
        var previous = (Settings.Current.StartHotkey, Settings.Current.StopHotkey);
        Settings.Update(s =>
        {
            s.StartHotkey = start;
            s.StopHotkey = stop;
        });
        RegisterHotkeys();
        if (HotkeyProblem is null) return null;

        var problem = HotkeyProblem;
        Settings.Update(s =>
        {
            s.StartHotkey = previous.StartHotkey;
            s.StopHotkey = previous.StopHotkey;
        });
        RegisterHotkeys();
        return problem;
    }

    // ------------------------------------------------------------------ windows

    public void ShowMain(bool pickDocument = false)
    {
        if (_main is null)
        {
            _main = new MainWindow(this);
            _main.Closed += (_, _) => _main = null;
        }

        if (!_main.IsVisible) _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
        _main.Topmost = true; // bring to front even when another app has focus
        _main.Topmost = false;
        _main.Focus();
        if (pickDocument) _main.PromptForDocument();
    }

    public void ShowSettings()
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(this);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }

        _settingsWindow.Show();
        if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
        _settingsWindow.Activate();
    }

    public void NotifyMinimizedToTray() =>
        _tray?.ShowBalloon("Reveal sigue en segundo plano", $"{Settings.Current.StartHotkey} inicia la captura · {Settings.Current.StopHotkey} la detiene.");

    public void Exit()
    {
        IsExiting = true;
        _app.Dispatcher.BeginInvoke(() =>
        {
            Dispose();
            _app.Shutdown();
        });
    }

    // ------------------------------------------------------------------ misc

    private void OnSettingsChanged(AppSettings settings)
    {
        _app.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                var exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
                if (exe is not null && StartupRegistration.IsEnabled() != settings.StartWithWindows)
                {
                    StartupRegistration.SetEnabled(settings.StartWithWindows, exe);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Could not update the startup registration", ex);
            }

            _controller?.RefreshVoices();
            UpdateTray();
            StateChanged?.Invoke();
        });
    }

    private void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode == Microsoft.Win32.PowerModes.Resume) _app.Dispatcher.BeginInvoke(() => _monitor?.RestartHooks());
    }

    private void OnSessionSwitch(object? sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        if (e.Reason is Microsoft.Win32.SessionSwitchReason.SessionUnlock or Microsoft.Win32.SessionSwitchReason.ConsoleConnect
            or Microsoft.Win32.SessionSwitchReason.RemoteConnect)
        {
            _app.Dispatcher.BeginInvoke(() => _monitor?.RestartHooks());
        }
    }

    private void UpdateTray() => _tray?.SetState(CaptureActive, Settings.Current.StartHotkey, Settings.Current.StopHotkey);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        IsExiting = true;

        try
        {
            Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
            _monitor?.Dispose();
            _hotkeys?.Dispose();
            _tray?.Dispose();
            Speech.Dispose();
            Marian?.Dispose();
            _sqliteLexicon?.Dispose();
            _overlay?.Close();
            _toast?.Close();
            _main?.Close();
            _settingsWindow?.Close();
        }
        catch (Exception ex)
        {
            Log.Error("Error while shutting down", ex);
        }
    }
}
