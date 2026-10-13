using System.Text;
using Reveal.Platform.Native;

namespace Reveal.Platform.Capture;

public readonly record struct ForegroundApp(IntPtr Window, uint ProcessId, string ProcessName)
{
    public static ForegroundApp Current()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return new ForegroundApp(IntPtr.Zero, 0, "");
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        return new ForegroundApp(hwnd, pid, ProcessNameOf(pid));
    }

    public static string ProcessNameOf(uint pid)
    {
        var handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return "";
        try
        {
            var sb = new StringBuilder(520);
            var size = (uint)sb.Capacity;
            return NativeMethods.QueryFullProcessImageName(handle, 0, sb, ref size)
                ? Path.GetFileNameWithoutExtension(sb.ToString()).ToLowerInvariant()
                : "";
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    /// <summary>
    /// Programs where a simulated Ctrl+C could interrupt a running process (terminals) instead of copying.
    /// For them only UI Automation is used.
    /// </summary>
    public bool IsTerminal => ProcessName is
        "windowsterminal" or "wt" or "conhost" or "openconsole" or "cmd" or "powershell" or "pwsh" or "mintty"
        or "alacritty" or "wezterm-gui" or "wezterm" or "hyper" or "putty" or "kitty" or "tabby" or "bash"
        or "ubuntu" or "wsl" or "wslhost" or "code" or "code - insiders" or "cursor" or "windsurf" or "devenv"
        or "idea64" or "pycharm64" or "webstorm64" or "rider64" or "clion64" or "goland64";

    public bool IsOwn => ProcessId == (uint)Environment.ProcessId;
}
