# Dev helper: simulate real user input with SendInput (per-monitor DPI aware => physical pixel coordinates).
# Dot-source:  . tools\dev\sim.ps1
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices; using System.Threading;
public static class Sim {
  [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr v);
  [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] static extern bool GetCursorPos(out P p);
  [DllImport("user32.dll")] static extern uint SendInput(uint n, I[] inputs, int size);
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool repaint);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [StructLayout(LayoutKind.Sequential)] public struct P { public int X, Y; }
  [StructLayout(LayoutKind.Sequential)] public struct R { public int L, T, Rt, B; }
  [StructLayout(LayoutKind.Sequential)] struct MI { public int dx, dy; public uint data, flags, time; public UIntPtr extra; }
  [StructLayout(LayoutKind.Sequential)] struct KI { public ushort vk, scan; public uint flags, time; public UIntPtr extra; }
  [StructLayout(LayoutKind.Explicit)] struct U { [FieldOffset(0)] public MI mi; [FieldOffset(0)] public KI ki; }
  [StructLayout(LayoutKind.Sequential)] struct I { public uint type; public U u; }
  public static void Init() { SetProcessDpiAwarenessContext(new IntPtr(-4)); }
  public static void Move(int x, int y) { SetCursorPos(x, y); Thread.Sleep(30); }
  static void Mouse(uint flags) { var i = new I[1]; i[0].type = 0; i[0].u.mi.flags = flags; SendInput(1, i, Marshal.SizeOf(typeof(I))); }
  public static void Down() { Mouse(0x0002); }
  public static void Up() { Mouse(0x0004); }
  public static void Click(int x, int y) { Move(x, y); Down(); Thread.Sleep(25); Up(); Thread.Sleep(60); }
  public static void DoubleClick(int x, int y) { Move(x, y); Down(); Thread.Sleep(15); Up(); Thread.Sleep(60); Down(); Thread.Sleep(15); Up(); Thread.Sleep(60); }
  public static void TripleClick(int x, int y) { Move(x, y); for (int k = 0; k < 3; k++) { Down(); Thread.Sleep(15); Up(); Thread.Sleep(60); } }
  public static void Drag(int x1, int y1, int x2, int y2) {
    Move(x1, y1); Down(); Thread.Sleep(60);
    int steps = 14; for (int s = 1; s <= steps; s++) { SetCursorPos(x1 + (x2 - x1) * s / steps, y1 + (y2 - y1) * s / steps); Thread.Sleep(14); }
    Thread.Sleep(60); Up(); Thread.Sleep(80);
  }
  public static void Key(ushort vk, bool up) { var i = new I[1]; i[0].type = 1; i[0].u.ki.vk = vk; i[0].u.ki.flags = up ? 2u : 0u; SendInput(1, i, Marshal.SizeOf(typeof(I))); }
  public static void Combo(params ushort[] vks) { foreach (var v in vks) { Key(v, false); Thread.Sleep(20); } Thread.Sleep(60); for (int k = vks.Length - 1; k >= 0; k--) { Key(vks[k], true); Thread.Sleep(20); } Thread.Sleep(120); }
}
"@
[Sim]::Init()
$VK = @{ Ctrl = 0x11; Shift = 0x10; Alt = 0x12; T = 0x54; X = 0x58; Esc = 0x1B; C = 0x43; A = 0x41; Right = 0x27; Left = 0x25; Home = 0x24; End = 0x23 }
function Shot([string]$Path, [int]$X, [int]$Y, [int]$W, [int]$H) {
  $bmp = New-Object System.Drawing.Bitmap $W, $H
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($X, $Y, 0, 0, (New-Object System.Drawing.Size $W, $H))
  $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png); $g.Dispose(); $bmp.Dispose()
}
function Get-WinRect($proc) { $r = New-Object Sim+R; [void][Sim]::GetWindowRect($proc.MainWindowHandle, [ref]$r); $r }
function Focus-Window($proc) {
  # Reliable activation: minimize + restore (SetForegroundWindow alone is often refused by the foreground lock)
  [void][Sim]::ShowWindow($proc.MainWindowHandle, 6); Start-Sleep -Milliseconds 250
  [void][Sim]::ShowWindow($proc.MainWindowHandle, 9); Start-Sleep -Milliseconds 400
  [void][Sim]::SetForegroundWindow($proc.MainWindowHandle); Start-Sleep -Milliseconds 300
}
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class TopM { [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  public static void Top(IntPtr h) { SetWindowPos(h, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002); }
  public static void NoTop(IntPtr h) { SetWindowPos(h, new IntPtr(-2), 0, 0, 0, 0, 0x0001 | 0x0002); } }
"@
function Make-TopMost($proc) { [TopM]::Top($proc.MainWindowHandle); Start-Sleep -Milliseconds 300 }
function Drop-TopMost($proc) { [TopM]::NoTop($proc.MainWindowHandle) }
