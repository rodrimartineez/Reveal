using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using Reveal.Platform.Capture;
using Reveal.Platform.Native;

namespace Reveal.Platform.Hotkeys;

/// <param name="Success">The shortcut works.</param>
/// <param name="Error">Why it could not be registered at all.</param>
/// <param name="Warning">It works, but with a caveat the user should know about.</param>
public sealed record HotkeyRegistration(bool Success, string? Error, string? Warning = null);

/// <summary>
/// Global hotkeys. The normal route is RegisterHotKey on a hidden message-only window. If another program
/// already owns the combination (RegisterHotKey fails with ERROR_HOTKEY_ALREADY_REGISTERED) Reveal falls back to a
/// low-level keyboard hook, which still sees the keystroke first, so the shortcut keeps working.
/// Must be created on the UI thread.
/// </summary>
public sealed class GlobalHotkeys : IDisposable
{
    private sealed class FallbackKey
    {
        public required int Id { get; init; }
        public required HotkeyGesture Gesture { get; init; }
        public required Action Handler { get; init; }
        public volatile bool Held;
    }

    private readonly HwndSource _source;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly Dictionary<int, Action> _handlers = new();
    private readonly List<FallbackKey> _fallbacks = new();
    private InputHooks? _hooks;
    private int _nextId = 0x5200;

    public GlobalHotkeys()
    {
        var parameters = new HwndSourceParameters("RevealHotkeyWindow")
        {
            ParentWindow = NativeMethods.HWND_MESSAGE,
            Width = 0,
            Height = 0,
            WindowStyle = 0,
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    /// <summary>Handle of the hidden window.</summary>
    public IntPtr Handle => _source.Handle;

    public int Register(HotkeyGesture gesture, Action handler, out HotkeyRegistration result)
    {
        var id = _nextId++;
        if (!gesture.IsValid)
        {
            result = new HotkeyRegistration(false, "Combinación de teclas no válida.");
            return 0;
        }

        if (NativeMethods.RegisterHotKey(_source.Handle, id, gesture.Modifiers | NativeMethods.MOD_NOREPEAT, gesture.VirtualKey))
        {
            _handlers[id] = handler;
            result = new HotkeyRegistration(true, null);
            return id;
        }

        var error = Marshal.GetLastPInvokeError();
        if (error == 1409) // ERROR_HOTKEY_ALREADY_REGISTERED
        {
            try
            {
                EnsureHooks();
                _fallbacks.Add(new FallbackKey { Id = id, Gesture = gesture, Handler = handler });
                result = new HotkeyRegistration(true, null,
                    $"{gesture} ya lo tiene reservado otra aplicación; Reveal lo detecta igualmente y lo intercepta, pero si prefieres evitar el conflicto, cámbialo.");
                return id;
            }
            catch (Exception ex)
            {
                result = new HotkeyRegistration(false, $"{gesture} ya está en uso por otra aplicación ({ex.Message}).");
                return 0;
            }
        }

        result = new HotkeyRegistration(false, $"No se pudo registrar {gesture}: {new Win32Exception(error).Message}");
        return 0;
    }

    public void Unregister(int id)
    {
        if (id == 0) return;
        if (_handlers.Remove(id)) NativeMethods.UnregisterHotKey(_source.Handle, id);
        _fallbacks.RemoveAll(f => f.Id == id);
        if (_fallbacks.Count == 0)
        {
            _hooks?.Dispose();
            _hooks = null;
        }
    }

    private void EnsureHooks()
    {
        if (_hooks is not null) return;
        _hooks = new InputHooks(keyboard: true, mouse: false) { KeyFilter = FilterKey };
        _hooks.Start();
    }

    /// <summary>Runs on the hook thread: swallow the shortcut (like RegisterHotKey would) and notify the UI thread.</summary>
    private bool FilterKey(KeyHookEvent e)
    {
        foreach (var f in _fallbacks.ToArray())
        {
            if (e.VirtualKey != (int)f.Gesture.VirtualKey) continue;

            if (e.Down)
            {
                var win = NativeMethods.IsKeyDown(NativeMethods.VK_LWIN) || NativeMethods.IsKeyDown(NativeMethods.VK_RWIN);
                if (e.Ctrl == f.Gesture.HasCtrl && e.Alt == f.Gesture.HasAlt && e.Shift == f.Gesture.HasShift && win == f.Gesture.HasWin)
                {
                    if (!f.Held)
                    {
                        f.Held = true;
                        _dispatcher.BeginInvoke(f.Handler);
                    }

                    return true;
                }
            }
            else if (f.Held)
            {
                f.Held = false;
                return true;
            }
        }

        return false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && _handlers.TryGetValue((int)wParam, out var handler))
        {
            handled = true;
            handler();
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (var id in _handlers.Keys.ToList()) NativeMethods.UnregisterHotKey(_source.Handle, id);
        _handlers.Clear();
        _fallbacks.Clear();
        _hooks?.Dispose();
        _hooks = null;
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
