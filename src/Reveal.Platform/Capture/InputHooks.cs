using System.Runtime.InteropServices;
using Reveal.Platform.Native;

namespace Reveal.Platform.Capture;

public readonly record struct MouseHookEvent(int X, int Y, bool Left, bool Down, bool CursorIsIBeam, bool Shift, bool Ctrl, bool Alt, bool Injected);

public readonly record struct KeyHookEvent(int VirtualKey, bool Down, bool Shift, bool Ctrl, bool Alt, bool Injected, UIntPtr ExtraInfo);

/// <summary>
/// Low-level mouse (and optionally keyboard) hooks running on their own thread with a message loop.
/// Callbacks do almost nothing: they only forward lightweight events (Windows removes slow hooks).
/// </summary>
internal sealed class InputHooks : IDisposable
{
    private readonly bool _mouse;
    private readonly bool _keyboard;
    private readonly NativeMethods.LowLevelProc _mouseProc;
    private readonly NativeMethods.LowLevelProc _keyProc;
    private readonly IntPtr _ibeamCursor;
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _mouseHook;
    private IntPtr _keyHook;
    private readonly ManualResetEventSlim _started = new(false);

    public event Action<MouseHookEvent>? Mouse;

    public event Action<KeyHookEvent>? Key;

    /// <summary>Optional filter: return true to swallow the key (used by the hotkey fallback).</summary>
    public Func<KeyHookEvent, bool>? KeyFilter { get; set; }

    public InputHooks(bool keyboard, bool mouse = true)
    {
        _mouse = mouse;
        _keyboard = keyboard;
        _mouseProc = MouseProc;
        _keyProc = KeyProc;
        _ibeamCursor = NativeMethods.LoadCursor(IntPtr.Zero, NativeMethods.IDC_IBEAM);
    }

    public void Start()
    {
        if (_thread is not null) return;
        _thread = new Thread(Run) { IsBackground = true, Name = "Reveal.InputHooks" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        if (!_started.Wait(3000)) throw new InvalidOperationException("Could not start the input hooks.");
        if (_mouse && _mouseHook == IntPtr.Zero) throw new InvalidOperationException("Windows refused to install the mouse hook.");
        if (!_mouse && _keyboard && _keyHook == IntPtr.Zero) throw new InvalidOperationException("Windows refused to install the keyboard hook.");
    }

    private void Run()
    {
        _threadId = NativeMethods.GetCurrentThreadId();
        var module = NativeMethods.GetModuleHandle(null);
        if (_mouse) _mouseHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _mouseProc, module, 0);
        if (_keyboard) _keyHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _keyProc, module, 0);
        _started.Set();

        if (_mouse && _mouseHook == IntPtr.Zero) return;

        while (NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessage(ref msg);
        }

        if (_mouseHook != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(_mouseHook);
        if (_keyHook != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(_keyHook);
        _mouseHook = _keyHook = IntPtr.Zero;
    }

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var message = (int)wParam;
            if (message is NativeMethods.WM_LBUTTONDOWN or NativeMethods.WM_LBUTTONUP
                or NativeMethods.WM_RBUTTONDOWN or NativeMethods.WM_MBUTTONDOWN)
            {
                try
                {
                    var data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                    var info = new NativeMethods.CURSORINFO { cbSize = Marshal.SizeOf<NativeMethods.CURSORINFO>() };
                    var ibeam = NativeMethods.GetCursorInfo(ref info) && info.hCursor == _ibeamCursor;
                    var isLeft = message is NativeMethods.WM_LBUTTONDOWN or NativeMethods.WM_LBUTTONUP;
                    Mouse?.Invoke(new MouseHookEvent(
                        data.pt.X, data.pt.Y, isLeft,
                        message is NativeMethods.WM_LBUTTONDOWN or NativeMethods.WM_RBUTTONDOWN or NativeMethods.WM_MBUTTONDOWN,
                        ibeam,
                        NativeMethods.IsKeyDown(NativeMethods.VK_SHIFT),
                        NativeMethods.IsKeyDown(NativeMethods.VK_CONTROL),
                        NativeMethods.IsKeyDown(NativeMethods.VK_MENU),
                        (data.flags & NativeMethods.LLMHF_INJECTED) != 0));
                }
                catch
                {
                    // never let an exception escape a hook callback
                }
            }
        }

        return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private IntPtr KeyProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var message = (int)wParam;
            try
            {
                var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
                var down = message is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;
                var evt = new KeyHookEvent(
                    (int)data.vkCode, down,
                    NativeMethods.IsKeyDown(NativeMethods.VK_SHIFT),
                    NativeMethods.IsKeyDown(NativeMethods.VK_CONTROL),
                    NativeMethods.IsKeyDown(NativeMethods.VK_MENU),
                    (data.flags & NativeMethods.LLKHF_INJECTED) != 0,
                    data.dwExtraInfo);
                Key?.Invoke(evt);
                if (KeyFilter?.Invoke(evt) == true) return (IntPtr)1;
            }
            catch
            {
                // ignore
            }
        }

        return NativeMethods.CallNextHookEx(_keyHook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_thread is null) return;
        NativeMethods.PostThreadMessage(_threadId, NativeMethods.WM_QUIT, UIntPtr.Zero, IntPtr.Zero);
        _thread.Join(1500);
        _thread = null;
    }
}
