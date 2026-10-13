using System.Diagnostics;
using System.Threading.Channels;
using System.Windows;
using Reveal.Platform.Native;

namespace Reveal.Platform.Capture;

public enum GestureKind
{
    Drag,
    DoubleClick,
    TripleClick,
    ShiftClick,
    Keyboard,
}

public sealed record CapturedSelection(string Text, Rect? BoundsPx, int CursorX, int CursorY, string Source, string ProcessName);

public enum CaptureFailureReason
{
    /// <summary>The app doesn't expose the selection and the clipboard fallback is off or not allowed here.</summary>
    Unsupported,

    /// <summary>Tried to copy the selection but nothing arrived.</summary>
    NothingCopied,

    /// <summary>Selection is longer than what Reveal handles.</summary>
    TooLong,
}

public sealed record CaptureFailure(CaptureFailureReason Reason, int CursorX, int CursorY, string ProcessName);

public sealed class SelectionMonitorOptions
{
    public bool KeyboardSelection { get; set; } = true;

    public bool ClipboardFallback { get; set; } = true;

    public int MaxSelectionChars { get; set; } = 400;
}

/// <summary>
/// Watches global input while capture is active and turns "the user just selected something" gestures
/// (drag, double/triple click, shift+click, shift+arrows) into the selected text.
/// Raises events from a worker thread.
/// </summary>
public sealed class SelectionMonitor : IDisposable
{
    private static readonly UIntPtr Marker = new(0x5245564C); // "REVL": tags the keystrokes we inject ourselves

    private readonly SelectionMonitorOptions _options;
    private readonly Channel<Gesture> _channel = Channel.CreateUnbounded<Gesture>(new UnboundedChannelOptions { SingleReader = true });
    private CancellationTokenSource _cts = new();
    private Timer? _watchdog;
    private long _stateSinceTicks = Stopwatch.GetTimestamp();
    private InputHooks? _hooks;
    private Task? _worker;
    private volatile bool _running;
    private string _workerStateValue = "idle";

    private string _workerState
    {
        get => Volatile.Read(ref _workerStateValue);
        set
        {
            Interlocked.Exchange(ref _stateSinceTicks, Stopwatch.GetTimestamp());
            Volatile.Write(ref _workerStateValue, value);
        }
    }

    // mouse state (touched only from the hook thread)
    private int _downX, _downY, _clickCount;
    private long _lastDownTicks;
    private bool _downWasIBeam;
    private int _lastDownScreenX, _lastDownScreenY;
    private long _kbPendingTicks;
    private volatile bool _kbSelecting;
    private Timer? _kbTimer;

    private readonly record struct Gesture(GestureKind Kind, int X, int Y, bool IBeam, DateTime At, int DownX = 0, int DownY = 0);

    public SelectionMonitor(SelectionMonitorOptions? options = null) => _options = options ?? new SelectionMonitorOptions();

    /// <summary>Screen-pixel hit test provided by the UI: true when the point is inside Reveal's own overlay.</summary>
    public Func<int, int, bool>? IsPointInsideOverlay { get; set; }

    /// <summary>True while the overlay is showing (lets Esc dismiss it).</summary>
    public Func<bool>? IsOverlayVisible { get; set; }

    /// <summary>Diagnostics (never contains the selected text).</summary>
    public Action<string>? Trace { get; set; }

    public event Action<CapturedSelection>? SelectionCaptured;

    public event Action<CaptureFailure>? CaptureFailed;

    /// <summary>The user pressed a mouse button somewhere outside the overlay.</summary>
    public event Action? PointerPressedOutside;

    public event Action? EscapePressed;

    public bool IsRunning => _running;

    public void Start()
    {
        if (_running) return;
        _hooks = new InputHooks(_options.KeyboardSelection);
        _hooks.Mouse += OnMouse;
        _hooks.Key += OnKey;
        _hooks.Start();
        _clickCount = 0;
        _running = true;
        _worker ??= Task.Run(() => WorkerAsync(_cts.Token));
        _watchdog ??= new Timer(_ => CheckWorker(), null, 3000, 3000);
    }

    /// <summary>
    /// Re-installs the low-level hooks. Windows silently drops a hook that ever stalls and some events
    /// (sleep/resume, desktop switches) can leave it dead, so the app calls this after them.
    /// </summary>
    public void RestartHooks()
    {
        if (!_running) return;
        try
        {
            _hooks?.Dispose();
            _hooks = new InputHooks(_options.KeyboardSelection);
            _hooks.Mouse += OnMouse;
            _hooks.Key += OnKey;
            _hooks.Start();
            _clickCount = 0;
            Trace?.Invoke("hooks re-installed");
        }
        catch (Exception ex)
        {
            Trace?.Invoke("could not re-install hooks: " + ex.Message);
        }
    }

    /// <summary>If the worker has been inside one gesture for far too long, abandon it and start a fresh one.</summary>
    private void CheckWorker()
    {
        if (!_running || _workerState == "idle") return;
        var busyFor = Stopwatch.GetElapsedTime(Interlocked.Read(ref _stateSinceTicks));
        if (busyFor < TimeSpan.FromSeconds(10)) return;

        Trace?.Invoke($"worker stuck in '{_workerState}' for {busyFor.TotalSeconds:0}s: restarting it");
        var old = _cts;
        _cts = new CancellationTokenSource();
        _worker = Task.Run(() => WorkerAsync(_cts.Token));
        _workerState = "idle";
        old.Cancel();
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _hooks?.Dispose();
        _hooks = null;
        _kbTimer?.Dispose();
        _kbTimer = null;
        _watchdog?.Dispose();
        _watchdog = null;
        // drain pending gestures
        while (_channel.Reader.TryRead(out _)) { }
    }

    // ------------------------------------------------------------------ hook thread

    private void OnMouse(MouseHookEvent e)
    {
        if (!_running) return;
        if (e.Down)
        {
            if (IsPointInsideOverlay?.Invoke(e.X, e.Y) != true) PointerPressedOutside?.Invoke();
            if (!e.Left) return;

            var now = Stopwatch.GetTimestamp();
            var dblTime = TimeSpan.FromMilliseconds(NativeMethods.GetDoubleClickTime());
            var dx = Math.Max(4, NativeMethods.GetSystemMetrics(NativeMethods.SM_CXDOUBLECLK) / 2);
            var dy = Math.Max(4, NativeMethods.GetSystemMetrics(NativeMethods.SM_CYDOUBLECLK) / 2);
            var elapsed = Stopwatch.GetElapsedTime(_lastDownTicks, now);
            if (_clickCount > 0 && elapsed <= dblTime && Math.Abs(e.X - _lastDownScreenX) <= dx && Math.Abs(e.Y - _lastDownScreenY) <= dy)
            {
                _clickCount++;
            }
            else
            {
                _clickCount = 1;
            }

            _lastDownTicks = now;
            _lastDownScreenX = _downX = e.X;
            _lastDownScreenY = _downY = e.Y;
            _downWasIBeam = e.CursorIsIBeam;
            return;
        }

        if (!e.Left) return;
        if (IsPointInsideOverlay?.Invoke(e.X, e.Y) == true) return;

        var distance = Math.Max(Math.Abs(e.X - _downX), Math.Abs(e.Y - _downY));
        GestureKind? kind = null;
        if (_clickCount >= 3) kind = GestureKind.TripleClick;
        else if (_clickCount == 2) kind = GestureKind.DoubleClick;
        else if (distance >= 6) kind = GestureKind.Drag;
        else if (e.Shift) kind = GestureKind.ShiftClick;

        if (kind is null) return;

        // Whether the pointer was a text I-beam is only a hint: some apps (Word...) use their own cursors. The worker
        // validates the gesture against the selection's on-screen bounds, so window drags, scrollbars, file drags... are ignored.
        var ibeam = e.CursorIsIBeam || _downWasIBeam;
        if (_workerState != "idle") Trace?.Invoke($"gesture queued while worker is '{_workerState}'");
        _channel.Writer.TryWrite(new Gesture(kind.Value, e.X, e.Y, ibeam, DateTime.UtcNow, _downX, _downY));
    }

    private void OnKey(KeyHookEvent e)
    {
        if (!_running || e.ExtraInfo == Marker) return;

        if (e.Down && e.VirtualKey == NativeMethods.VK_ESCAPE)
        {
            if (IsOverlayVisible?.Invoke() == true) EscapePressed?.Invoke();
            return;
        }

        if (!_options.KeyboardSelection) return;

        // Shift + navigation key = extending a text selection with the keyboard.
        var isNav = e.VirtualKey is 0x25 or 0x26 or 0x27 or 0x28 or 0x24 or 0x23 or 0x21 or 0x22; // arrows, home, end, pgup, pgdn
        if (e.Down && e.Shift && isNav && !e.Alt)
        {
            _kbSelecting = true;
            Interlocked.Exchange(ref _kbPendingTicks, Stopwatch.GetTimestamp());
            // debounce: fire when keys have been idle
            _kbTimer ??= new Timer(_ => KeyboardIdle(), null, Timeout.Infinite, Timeout.Infinite);
            _kbTimer.Change(280, Timeout.Infinite);
        }
    }

    private void KeyboardIdle()
    {
        if (!_running || !_kbSelecting) return;
        _kbSelecting = false;
        NativeMethods.GetCursorPos(out var p);
        _channel.Writer.TryWrite(new Gesture(GestureKind.Keyboard, p.X, p.Y, false, DateTime.UtcNow));
    }

    // ------------------------------------------------------------------ worker

    private async Task WorkerAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                _workerState = "idle";
                if (!await _channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false)) break;
                _workerState = "reading";
                Gesture gesture = default;
                var have = false;
                // Only the most recent gesture matters (triple click supersedes double click, etc.).
                while (_channel.Reader.TryRead(out var g))
                {
                    gesture = g;
                    have = true;
                }

                if (!have) continue;
                Trace?.Invoke($"worker: got {gesture.Kind}");

                // Give the application a moment to finish updating its selection.
                var delay = gesture.Kind switch
                {
                    GestureKind.Drag => 70,
                    GestureKind.Keyboard => 0,
                    _ => 90, // multi-click: wait to see whether a third click follows
                };
                if (delay > 0) await Task.Delay(delay, ct).ConfigureAwait(false);
                if (_channel.Reader.TryPeek(out _))
                {
                    Trace?.Invoke("worker: superseded by a newer gesture");
                    continue; // a newer gesture arrived: handle that one instead
                }

                try
                {
                    _workerState = "processing";
                    ProcessGesture(gesture);
                }
                catch (Exception ex)
                {
                    Trace?.Invoke("capture error: " + ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private void ProcessGesture(Gesture g)
    {
        _workerState = "foreground";
        var app = ForegroundApp.Current();
        Trace?.Invoke($"processing {g.Kind}; foreground='{app.ProcessName}' own={app.IsOwn}");
        if (app.IsOwn || app.Window == IntPtr.Zero) return;

        _workerState = "uia";
        var uia = UiaSelectionReader.Read(g.X, g.Y, TimeSpan.FromMilliseconds(450));
        _workerState = "after-uia";
        Trace?.Invoke($"gesture {g.Kind} in '{app.ProcessName}': uia={uia.Status} len={uia.Text.Length}");
        if (uia.Status == UiaStatus.Password) return;

        if (uia.Status == UiaStatus.Text)
        {
            if (!PointerTouchedSelection(g, uia.Bounds))
            {
                Trace?.Invoke("ignored: the pointer was not on the selected text");
                return;
            }

            Deliver(uia.Text, uia.Bounds, g, "uia", app);
            return;
        }

        if (uia.Status == UiaStatus.NoSelection)
        {
            // The control may still be applying the selection (Word, browsers...): look again a couple of times.
            if (g.Kind == GestureKind.Keyboard) return;
            foreach (var wait in new[] { 90, 220 })
            {
                Thread.Sleep(wait);
                var retry = UiaSelectionReader.Read(g.X, g.Y, TimeSpan.FromMilliseconds(450));
                Trace?.Invoke($"retry after {wait} ms: uia={retry.Status} len={retry.Text.Length}");
                if (retry.Status != UiaStatus.Text) continue;
                if (PointerTouchedSelection(g, retry.Bounds)) Deliver(retry.Text, retry.Bounds, g, "uia", app);
                return;
            }

            return;
        }

        // From here on the app doesn't tell us anything: only try the clipboard when the pointer really looked like a text caret.
        if (g.Kind != GestureKind.Keyboard && !g.IBeam) return;

        // UIA couldn't tell (no text pattern / error): fall back to copying the selection.
        if (!_options.ClipboardFallback || app.IsTerminal)
        {
            RaiseFailure(CaptureFailureReason.Unsupported, g, app);
            return;
        }

        _workerState = "clipboard";
        var text = CopySelectionViaClipboard();
        Trace?.Invoke($"clipboard fallback in '{app.ProcessName}': len={text?.Length ?? 0}");
        if (string.IsNullOrWhiteSpace(text))
        {
            // A double click on empty space or a failed copy: only worth a hint for explicit word selection.
            if (g.Kind is GestureKind.DoubleClick or GestureKind.Drag) RaiseFailure(CaptureFailureReason.NothingCopied, g, app);
            return;
        }

        Deliver(text, null, g, "clipboard", app);
    }

    /// <summary>
    /// A mouse gesture counts only if the pointer was an I-beam, or the pointer went down/up on the selection itself
    /// (keyboard selections always count). Filters out window moves, scrollbar drags and the like.
    /// </summary>
    private static bool PointerTouchedSelection(Gesture g, Rect? bounds)
    {
        if (g.Kind == GestureKind.Keyboard || g.IBeam) return true;
        if (bounds is not { } b) return false;
        b.Inflate(16, 16);
        return b.Contains(new Point(g.X, g.Y)) || b.Contains(new Point(g.DownX, g.DownY));
    }

    private void Deliver(string text, Rect? bounds, Gesture g, string source, ForegroundApp app)
    {
        if (text.Length > _options.MaxSelectionChars * 3)
        {
            RaiseFailure(CaptureFailureReason.TooLong, g, app);
            return;
        }

        SelectionCaptured?.Invoke(new CapturedSelection(text, bounds, g.X, g.Y, source, app.ProcessName));
    }

    private void RaiseFailure(CaptureFailureReason reason, Gesture g, ForegroundApp app) =>
        CaptureFailed?.Invoke(new CaptureFailure(reason, g.X, g.Y, app.ProcessName));

    private string? CopySelectionViaClipboard()
    {
        // The user may still be holding Shift/Alt/Win (shift+click selection): wait until they let go so the
        // application doesn't see Ctrl+Shift+C etc.
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 2;
        while (NativeMethods.IsKeyDown(NativeMethods.VK_SHIFT) || NativeMethods.IsKeyDown(NativeMethods.VK_MENU)
               || NativeMethods.IsKeyDown(NativeMethods.VK_LWIN) || NativeMethods.IsKeyDown(NativeMethods.VK_RWIN))
        {
            if (Stopwatch.GetTimestamp() > deadline) return null;
            Thread.Sleep(10);
        }

        var snapshot = ClipboardAccess.TakeSnapshot();
        if (snapshot is null) return null; // can't preserve the clipboard safely: don't touch it

        var before = ClipboardAccess.SequenceNumber;
        ClipboardAccess.SendCopyKeystroke(Marker);

        var waitUntil = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 35 / 100;
        while (ClipboardAccess.SequenceNumber == before && Stopwatch.GetTimestamp() < waitUntil) Thread.Sleep(8);

        string? text = null;
        var changed = ClipboardAccess.SequenceNumber != before;
        if (changed)
        {
            Thread.Sleep(15); // let the app finish writing all formats
            text = ClipboardAccess.ReadText();
            var afterRead = ClipboardAccess.SequenceNumber;

            // Put the user's clipboard back, unless something else wrote to it in the meantime.
            if (snapshot.IsEmpty) ClipboardAccess.Clear();
            else ClipboardAccess.Restore(snapshot);
            _ = afterRead;
        }

        return text;
    }

    public void Dispose()
    {
        Stop();
        _cts.Cancel();
        try { _worker?.Wait(500); } catch { /* ignore */ }
        _cts.Dispose();
    }
}
